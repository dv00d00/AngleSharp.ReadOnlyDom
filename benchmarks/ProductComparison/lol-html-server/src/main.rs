use axum::body::{Body, Bytes};
use axum::extract::Request;
use axum::http::{header, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use axum::Router;
use http_body_util::BodyExt;
use lol_html::send::{HtmlRewriter, Settings};
use lol_html::{element, OutputSink};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use tokio::sync::mpsc;
use tokio_stream::wrappers::ReceiverStream;

struct DiscardOutput;

impl OutputSink for DiscardOutput {
    fn handle_chunk(&mut self, _chunk: &[u8]) {}
}

// lol-html hands the sink one chunk per token run; shipping each as its own chunked-encoding
// frame is a syscall per handful of bytes. Coalescing to response-sized segments matches what
// the Kestrel lane gets for free from its pipe.
const COALESCE_BYTES: usize = 16 * 1024;
const CHANNEL_CAPACITY: usize = 2;

struct ChannelOutput {
    tx: mpsc::Sender<Result<Bytes, std::io::Error>>,
    buffer: Vec<u8>,
    receiver_closed: Arc<AtomicBool>,
}

impl ChannelOutput {
    fn new(
        tx: mpsc::Sender<Result<Bytes, std::io::Error>>,
        receiver_closed: Arc<AtomicBool>,
    ) -> Self {
        Self {
            tx,
            buffer: Vec::with_capacity(COALESCE_BYTES),
            receiver_closed,
        }
    }

    fn flush(&mut self) {
        if self.buffer.is_empty() || self.receiver_closed.load(Ordering::Relaxed) {
            return;
        }
        let chunk = Bytes::from(std::mem::take(&mut self.buffer));
        if self.tx.blocking_send(Ok(chunk)).is_err() {
            self.receiver_closed.store(true, Ordering::Relaxed);
        }
    }
}

impl OutputSink for ChannelOutput {
    fn handle_chunk(&mut self, chunk: &[u8]) {
        self.buffer.extend_from_slice(chunk);
        if self.buffer.len() >= COALESCE_BYTES {
            self.flush();
        }
    }
}

impl Drop for ChannelOutput {
    // The rewriter owns the sink and drops it after end(); this is where the tail ships.
    fn drop(&mut self) {
        self.flush();
    }
}

#[tokio::main]
async fn main() {
    let port = std::env::var("BENCHMARK_PORT")
        .ok()
        .and_then(|value| value.parse::<u16>().ok())
        .unwrap_or(5082);
    let app = Router::new()
        .route("/health", get(|| async { "ok" }))
        .route("/extract", post(extract))
        .route("/rewrite", post(rewrite));
    let listener = tokio::net::TcpListener::bind(("127.0.0.1", port))
        .await
        .expect("failed to bind benchmark server");
    println!("READY http://127.0.0.1:{port}");
    // Kestrel disables Nagle by default; without the same here every small chunked write
    // risks a delayed-ACK stall and the comparison measures TCP timers instead of parsers.
    axum::serve(NoDelayListener(listener), app)
        .await
        .expect("benchmark server failed");
}

struct NoDelayListener(tokio::net::TcpListener);

impl axum::serve::Listener for NoDelayListener {
    type Io = tokio::net::TcpStream;
    type Addr = std::net::SocketAddr;

    async fn accept(&mut self) -> (Self::Io, Self::Addr) {
        loop {
            if let Ok((stream, address)) = self.0.accept().await {
                let _ = stream.set_nodelay(true);
                return (stream, address);
            }
        }
    }

    fn local_addr(&self) -> std::io::Result<Self::Addr> {
        self.0.local_addr()
    }
}

// Full-duplex streaming: rewritten output leaves through the response while the request body
// is still arriving. The spawned task owns the rewriter; its channel closing (on completion or
// error) is what terminates the chunked response.
async fn rewrite(request: Request) -> Response {
    // The async request pump and synchronous lol-html worker are separated by bounded channels.
    // The worker blocks when the response stops draining, propagating socket backpressure all the
    // way to request-body consumption instead of queueing the complete rewritten document.
    let (input_tx, mut input_rx) = mpsc::channel::<Result<Bytes, std::io::Error>>(CHANNEL_CAPACITY);
    let (output_tx, output_rx) = mpsc::channel::<Result<Bytes, std::io::Error>>(CHANNEL_CAPACITY);
    let mut body = request.into_body();
    tokio::spawn(async move {
        while let Some(frame) = body.frame().await {
            let item = match frame {
                Ok(frame) => match frame.into_data() {
                    Ok(data) => Ok(data),
                    Err(_) => continue,
                },
                Err(_) => Err(std::io::Error::other("request body read failed")),
            };
            let failed = item.is_err();
            if input_tx.send(item).await.is_err() || failed {
                return;
            }
        }
    });

    tokio::task::spawn_blocking(move || {
        let settings = Settings::new_send().append_element_content_handler(element!(
            "ul.news-list li[dt-eid='em_item_article'] a[href]",
            |element| {
                element.set_attribute("data-q", "1")?;
                Ok(())
            }
        ));
        let receiver_closed = Arc::new(AtomicBool::new(false));
        let mut rewriter = HtmlRewriter::new(
            settings,
            ChannelOutput::new(output_tx.clone(), Arc::clone(&receiver_closed)),
        );
        while let Some(frame) = input_rx.blocking_recv() {
            let data = match frame {
                Ok(data) => data,
                Err(error) => {
                    drop(rewriter);
                    let _ = output_tx.blocking_send(Err(error));
                    return;
                }
            };
            if rewriter.write(&data).is_err() {
                drop(rewriter);
                let _ = output_tx.blocking_send(Err(std::io::Error::other("rewrite failed")));
                return;
            }
            if receiver_closed.load(Ordering::Relaxed) {
                return;
            }
        }
        if rewriter.end().is_err() {
            let _ = output_tx.blocking_send(Err(std::io::Error::other("rewrite failed")));
        }
    });
    (
        [(header::CONTENT_TYPE, "text/html; charset=utf-8")],
        Body::from_stream(ReceiverStream::new(output_rx)),
    )
        .into_response()
}

async fn extract(request: Request) -> Result<Response, StatusCode> {
    let urls = Arc::new(Mutex::new(Vec::<String>::new()));
    let handler_urls = Arc::clone(&urls);
    let settings = Settings::new_send().append_element_content_handler(element!(
        "ul.news-list li[dt-eid='em_item_article'] a[href]",
        move |element| {
            if let Some(url) = element.get_attribute("href") {
                handler_urls.lock().expect("URL lock poisoned").push(url);
            }
            Ok(())
        }
    ));
    let mut rewriter = HtmlRewriter::new(settings, DiscardOutput);
    let mut body = request.into_body();
    while let Some(frame) = body.frame().await {
        let frame = frame.map_err(|_| StatusCode::BAD_REQUEST)?;
        if let Ok(data) = frame.into_data() {
            rewriter
                .write(&data)
                .map_err(|_| StatusCode::UNPROCESSABLE_ENTITY)?;
        }
    }
    rewriter
        .end()
        .map_err(|_| StatusCode::UNPROCESSABLE_ENTITY)?;

    let urls = urls.lock().expect("URL lock poisoned");
    let capacity = urls.iter().map(|url| url.len() + 1).sum();
    let mut output = String::with_capacity(capacity);
    for url in urls.iter() {
        output.push_str(url);
        output.push('\n');
    }
    Ok((
        [(header::CONTENT_TYPE, "text/plain; charset=utf-8")],
        Body::from(output),
    )
        .into_response())
}
