use lol_html::{element, HtmlRewriter, OutputSink, Settings};
use std::cell::RefCell;
use std::env;
use std::fs;
use std::rc::Rc;
use std::time::{Duration, Instant};

struct DiscardOutput;

impl OutputSink for DiscardOutput {
    fn handle_chunk(&mut self, _chunk: &[u8]) {}
}

struct Options {
    input: String,
    seconds: f64,
    warmup: u64,
    copies: usize,
    chunk_size: usize,
    workload: String,
}

fn main() {
    let options = parse_options();
    let source = fs::read(&options.input).expect("failed to read input");
    let input = repeat_body(&source, options.copies);

    for _ in 0..options.warmup {
        let _ = parse(&input, options.chunk_size, &options.workload);
    }

    let started = Instant::now();
    let duration = Duration::from_secs_f64(options.seconds);
    let mut requests = 0u64;
    let mut checksum = 0i64;
    let last = loop {
        let result = parse(&input, options.chunk_size, &options.workload);
        checksum = checksum.wrapping_add(result.1);
        requests += 1;
        if started.elapsed() >= duration {
            break result;
        }
    };
    let elapsed = started.elapsed();

    println!(
        "RESULT service=lol-html workload={} copies={} requests={} elapsed_ms={:.3} cpu_ms=nan checksum={} value_checksum={} urls={} bytes={}",
        options.workload,
        options.copies,
        requests,
        elapsed.as_secs_f64() * 1000.0,
        checksum,
        last.1,
        last.0,
        input.len()
    );
}

fn parse(input: &[u8], chunk_size: usize, workload: &str) -> (usize, i64) {
    if workload == "passthrough" {
        // Keep a never-matching handler so both products must parse tags while the
        // emitted document remains byte-for-byte unchanged. With no handlers at all,
        // lol-html takes a distinct raw forwarding fast path.
        let settings =
            Settings::default().append_element_content_handler(element!("zz", |_| Ok(())));
        let mut rewriter = HtmlRewriter::new(settings, DiscardOutput);
        for chunk in input.chunks(chunk_size) {
            rewriter.write(chunk).expect("lol-html parse failed");
        }
        rewriter.end().expect("lol-html completion failed");
        return (0, 0);
    }

    if workload == "match" {
        let matches = Rc::new(RefCell::new(0usize));
        let handler_matches = Rc::clone(&matches);
        let settings = Settings::default().append_element_content_handler(element!(
            "ul.news-list li[dt-eid='em_item_article'] a[href]",
            move |_| {
                *handler_matches.borrow_mut() += 1;
                Ok(())
            }
        ));
        let mut rewriter = HtmlRewriter::new(settings, DiscardOutput);
        for chunk in input.chunks(chunk_size) {
            rewriter.write(chunk).expect("lol-html parse failed");
        }
        rewriter.end().expect("lol-html completion failed");
        let count = *matches.borrow();
        return (count, count as i64);
    }

    if workload == "rewrite" {
        // The rewritten-output checksum is deterministic per corpus: compute it on the first
        // pass only so the hot loop measures rewriting and publishing, not checksumming.
        thread_local! {
            static REWRITE_CHECKSUM: RefCell<Option<i64>> = const { RefCell::new(None) };
        }
        struct ChecksumOutput {
            active: bool,
            checksum: Rc<RefCell<i64>>,
        }
        impl OutputSink for ChecksumOutput {
            fn handle_chunk(&mut self, chunk: &[u8]) {
                if self.active {
                    let mut checksum = self.checksum.borrow_mut();
                    for value in chunk {
                        *checksum = checksum.wrapping_mul(31).wrapping_add(i64::from(*value));
                    }
                }
            }
        }

        let matches = Rc::new(RefCell::new(0usize));
        let handler_matches = Rc::clone(&matches);
        let settings = Settings::default().append_element_content_handler(element!(
            "ul.news-list li[dt-eid='em_item_article'] a[href]",
            move |element| {
                *handler_matches.borrow_mut() += 1;
                element.set_attribute("data-q", "1")?;
                Ok(())
            }
        ));
        let first_pass = REWRITE_CHECKSUM.with(|cache| cache.borrow().is_none());
        let accumulator = Rc::new(RefCell::new(17i64));
        let mut rewriter = HtmlRewriter::new(
            settings,
            ChecksumOutput {
                active: first_pass,
                checksum: Rc::clone(&accumulator),
            },
        );
        for chunk in input.chunks(chunk_size) {
            rewriter.write(chunk).expect("lol-html parse failed");
        }
        rewriter.end().expect("lol-html completion failed");
        let checksum = REWRITE_CHECKSUM.with(|cache| {
            let mut cache = cache.borrow_mut();
            if first_pass {
                *cache = Some(*accumulator.borrow());
            }
            cache.expect("rewrite checksum cached")
        });
        let count = *matches.borrow();
        return (count, checksum);
    }

    let urls = Rc::new(RefCell::new(Vec::<String>::new()));
    let handler_urls = Rc::clone(&urls);
    let settings = Settings::default().append_element_content_handler(element!(
        "ul.news-list li[dt-eid='em_item_article'] a[href]",
        move |element| {
            if let Some(url) = element.get_attribute("href") {
                handler_urls.borrow_mut().push(url);
            }
            Ok(())
        }
    ));
    let mut rewriter = HtmlRewriter::new(settings, DiscardOutput);
    for chunk in input.chunks(chunk_size) {
        rewriter.write(chunk).expect("lol-html parse failed");
    }
    rewriter.end().expect("lol-html completion failed");

    let urls = urls.borrow();
    let mut checksum = 17i64;
    for url in urls.iter() {
        for value in url.as_bytes() {
            checksum = checksum.wrapping_mul(31).wrapping_add(i64::from(*value));
        }
    }
    (urls.len(), checksum)
}

fn repeat_body(source: &[u8], copies: usize) -> Vec<u8> {
    if copies == 1 {
        return source.to_vec();
    }

    let text = std::str::from_utf8(source).expect("corpus is not UTF-8");
    let lower = text.to_ascii_lowercase();
    let body_open = lower.find("<body").expect("corpus has no body element");
    let body_content = body_open
        + text[body_open..]
            .find('>')
            .expect("body start tag is incomplete")
        + 1;
    let body_close = lower.rfind("</body").expect("body end tag is missing");
    let body = &source[body_content..body_close];
    let mut output = Vec::with_capacity(source.len() + body.len() * (copies - 1));
    output.extend_from_slice(&source[..body_content]);
    for _ in 0..copies {
        output.extend_from_slice(body);
    }
    output.extend_from_slice(&source[body_close..]);
    output
}

fn parse_options() -> Options {
    let mut values = env::args().skip(1);
    let mut input = None;
    let mut seconds = 10.0;
    let mut warmup = 120;
    let mut copies = 1;
    let mut chunk_size = 4096;
    let mut workload = String::from("extract");
    while let Some(name) = values.next() {
        let value = values.next().expect("option value is missing");
        match name.as_str() {
            "--input" => input = Some(value),
            "--seconds" => seconds = value.parse().expect("invalid seconds"),
            "--warmup" => warmup = value.parse().expect("invalid warmup"),
            "--copies" => copies = value.parse().expect("invalid copies"),
            "--chunk-size" => chunk_size = value.parse().expect("invalid chunk size"),
            "--workload" => workload = value,
            _ => panic!("unknown option: {name}"),
        }
    }
    assert!(matches!(
        workload.as_str(),
        "passthrough" | "match" | "extract" | "rewrite"
    ));
    Options {
        input: input.expect("--input is required"),
        seconds,
        warmup,
        copies,
        chunk_size,
        workload,
    }
}
