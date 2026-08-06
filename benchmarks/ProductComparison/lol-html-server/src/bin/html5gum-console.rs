//! html5gum comparison lane: a spec-compliant Rust tokenizer with no tree builder and no
//! selector engine - the closest published shape to this repo's streaming tokenizer.
//!
//! Uses a custom Emitter (the crate's documented fast path) that materializes nothing but
//! `a[href]` hits, mirroring the lol-html console's match/extract workloads. html5gum has
//! no push API, so it always consumes the whole buffer: compare against the AngleSharp
//! console's buffer-trusted mode, not the chunked stream modes. Only the generic query is
//! supported - there is no selector engine to express the qq composite.

use html5gum::{Emitter, Error, State, Tokenizer};
use std::env;
use std::fs;
use std::time::{Duration, Instant};

#[derive(Default)]
struct HrefExtractor {
    tag_name: Vec<u8>,
    is_closing: bool,
    attr_name: Vec<u8>,
    attr_value: Vec<u8>,
    last_start_tag: Vec<u8>,
    element_has_href: bool,
    collect_values: bool,
    count: usize,
    checksum: i64,
    finished: Option<(usize, i64)>,
}

impl HrefExtractor {
    fn new(collect_values: bool) -> Self {
        Self {
            collect_values,
            checksum: 17,
            ..Self::default()
        }
    }

    fn flush_old_attribute(&mut self) {
        if !self.is_closing
            && !self.element_has_href
            && self.tag_name == b"a"
            && self.attr_name == b"href"
        {
            self.element_has_href = true;
            if self.collect_values {
                for value in &self.attr_value {
                    self.checksum = self
                        .checksum
                        .wrapping_mul(31)
                        .wrapping_add(i64::from(*value));
                }
            }
        }
        self.attr_name.clear();
        self.attr_value.clear();
    }
}

impl Emitter for HrefExtractor {
    type Token = (usize, i64);

    fn set_last_start_tag(&mut self, last_start_tag: Option<&[u8]>) {
        self.last_start_tag.clear();
        self.last_start_tag
            .extend(last_start_tag.unwrap_or_default());
    }

    fn pop_token(&mut self) -> Option<(usize, i64)> {
        self.finished.take()
    }

    fn emit_string(&mut self, _: &[u8]) {}

    fn init_start_tag(&mut self) {
        self.tag_name.clear();
        self.is_closing = false;
        self.element_has_href = false;
    }

    fn init_end_tag(&mut self) {
        self.tag_name.clear();
        self.is_closing = true;
    }

    fn emit_current_tag(&mut self) -> Option<State> {
        self.flush_old_attribute();
        if !self.is_closing && self.element_has_href {
            self.count += 1;
        }
        self.last_start_tag.clear();
        if !self.is_closing {
            self.last_start_tag.extend(&self.tag_name);
        }
        self.tag_name.clear();
        html5gum::naive_next_state(&self.last_start_tag)
    }

    fn set_self_closing(&mut self) {}

    fn push_tag_name(&mut self, s: &[u8]) {
        self.tag_name.extend(s);
    }

    fn init_attribute(&mut self) {
        self.flush_old_attribute();
    }

    fn push_attribute_name(&mut self, s: &[u8]) {
        self.attr_name.extend(s);
    }

    fn push_attribute_value(&mut self, s: &[u8]) {
        self.attr_value.extend(s);
    }

    fn current_is_appropriate_end_tag_token(&mut self) -> bool {
        self.is_closing && !self.tag_name.is_empty() && self.tag_name == self.last_start_tag
    }

    fn emit_current_comment(&mut self) {}
    fn emit_current_doctype(&mut self) {}

    fn emit_eof(&mut self) {
        self.finished = Some((self.count, self.checksum));
    }
    fn emit_error(&mut self, _: Error) {}
    fn init_comment(&mut self) {}
    fn init_doctype(&mut self) {}
    fn push_comment(&mut self, _: &[u8]) {}
    fn push_doctype_name(&mut self, _: &[u8]) {}
    fn push_doctype_public_identifier(&mut self, _: &[u8]) {}
    fn push_doctype_system_identifier(&mut self, _: &[u8]) {}
    fn set_doctype_public_identifier(&mut self, _: &[u8]) {}
    fn set_doctype_system_identifier(&mut self, _: &[u8]) {}
    fn set_force_quirks(&mut self) {}
}

fn parse(input: &str, collect_values: bool) -> (usize, i64) {
    let tokenizer = Tokenizer::new_with_emitter(input, HrefExtractor::new(collect_values));
    let mut result = (0, 17);
    for token in tokenizer {
        result = token.expect("html5gum parse failed");
    }
    result
}

fn main() {
    let mut values = env::args().skip(1);
    let mut input = None;
    let mut seconds = 10.0;
    let mut warmup = 120u64;
    let mut copies = 1usize;
    let mut workload = String::from("extract");
    while let Some(name) = values.next() {
        let value = values.next().expect("option value is missing");
        match name.as_str() {
            "--input" => input = Some(value),
            "--seconds" => seconds = value.parse().expect("invalid seconds"),
            "--warmup" => warmup = value.parse().expect("invalid warmup"),
            "--copies" => copies = value.parse().expect("invalid copies"),
            "--workload" => workload = value,
            // Accepted for harness compatibility; html5gum has no push API and the
            // selector is always the generic a[href].
            "--chunk-size" | "--query" | "--mode" | "--unlimited" => {}
            _ => panic!("unknown option: {name}"),
        }
    }
    assert!(matches!(workload.as_str(), "match" | "extract"));
    let source = fs::read(input.expect("--input is required")).expect("failed to read input");
    let repeated = repeat_body(&source, copies);
    let text = std::str::from_utf8(&repeated).expect("corpus is not UTF-8");
    let collect_values = workload == "extract";

    for _ in 0..warmup {
        let _ = parse(text, collect_values);
    }

    let started = Instant::now();
    let duration = Duration::from_secs_f64(seconds);
    let mut requests = 0u64;
    let mut checksum = 0i64;
    let last = loop {
        let result = parse(text, collect_values);
        checksum = checksum.wrapping_add(result.1);
        requests += 1;
        if started.elapsed() >= duration {
            break result;
        }
    };
    let elapsed = started.elapsed();

    println!(
        "RESULT service=html5gum workload={workload} copies={copies} requests={requests} elapsed_ms={:.3} cpu_ms=nan checksum={checksum} value_checksum={} urls={} bytes={}",
        elapsed.as_secs_f64() * 1000.0,
        last.1,
        last.0,
        text.len()
    );
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
