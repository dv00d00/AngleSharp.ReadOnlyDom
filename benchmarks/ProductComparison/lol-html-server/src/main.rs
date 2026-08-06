use axum::body::{Body, Bytes};
use axum::extract::Request;
use axum::http::{header, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use axum::Router;
use http_body_util::BodyExt;
use lol_html::send::{HtmlRewriter, Settings};
use lol_html::{element, OutputSink};
use std::sync::{Arc, Mutex};
use tokio::sync::mpsc;
use tokio_stream::wrappers::UnboundedReceiverStream;

struct DiscardOutput;

impl OutputSink for DiscardOutput {
    fn handle_chunk(&mut self, _chunk: &[u8]) {}
}

// lol-html hands the sink one chunk per token run; shipping each as its own chunked-encoding
// frame is a syscall per handful of bytes. Coalescing to response-sized segments matches what
// the Kestrel lane gets for free from its pipe.
const COALESCE_BYTES: usize = 16 * 1024;

struct ChannelOutput {
    tx: mpsc::UnboundedSender<Result<Bytes, std::io::Error>>,
    buffer: Vec<u8>,
}

impl ChannelOutput {
    fn new(tx: mpsc::UnboundedSender<Result<Bytes, std::io::Error>>) -> Self {
        Self {
            tx,
            buffer: Vec::with_capacity(COALESCE_BYTES),
        }
    }

    fn flush(&mut self) {
        if !self.buffer.is_empty() {
            // A dropped receiver means the client went away; the write loop notices on its own.
            let _ = self.tx.send(Ok(Bytes::from(std::mem::take(&mut self.buffer))));
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
    let (tx, rx) = mpsc::unbounded_channel::<Result<Bytes, std::io::Error>>();
    let sink_tx = tx.clone();
    let mut body = request.into_body();
    tokio::spawn(async move {
        let settings = Settings::new_send().append_element_content_handler(element!(
            "ul.news-list li[dt-eid='em_item_article'] a[href]",
            |element| {
                element.set_attribute("data-q", "1")?;
                Ok(())
            }
        ));
        let mut rewriter = HtmlRewriter::new(settings, ChannelOutput::new(sink_tx));
        while let Some(frame) = body.frame().await {
            let Ok(frame) = frame else {
                let _ = tx.send(Err(std::io::Error::other("request body read failed")));
                return;
            };
            if let Ok(data) = frame.into_data() {
                if rewriter.write(&data).is_err() {
                    let _ = tx.send(Err(std::io::Error::other("rewrite failed")));
                    return;
                }
            }
        }
        if rewriter.end().is_err() {
            let _ = tx.send(Err(std::io::Error::other("rewrite failed")));
        }
    });
    (
        [(header::CONTENT_TYPE, "text/html; charset=utf-8")],
        Body::from_stream(UnboundedReceiverStream::new(rx)),
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
