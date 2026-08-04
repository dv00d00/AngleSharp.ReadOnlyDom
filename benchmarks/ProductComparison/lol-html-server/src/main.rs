use axum::body::Body;
use axum::extract::Request;
use axum::http::{header, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use axum::Router;
use http_body_util::BodyExt;
use lol_html::send::{HtmlRewriter, Settings};
use lol_html::{element, OutputSink};
use std::sync::{Arc, Mutex};

struct DiscardOutput;

impl OutputSink for DiscardOutput {
    fn handle_chunk(&mut self, _chunk: &[u8]) {}
}

#[tokio::main]
async fn main() {
    let port = std::env::var("BENCHMARK_PORT")
        .ok()
        .and_then(|value| value.parse::<u16>().ok())
        .unwrap_or(5082);
    let app = Router::new()
        .route("/health", get(|| async { "ok" }))
        .route("/extract", post(extract));
    let listener = tokio::net::TcpListener::bind(("127.0.0.1", port))
        .await
        .expect("failed to bind benchmark server");
    println!("READY http://127.0.0.1:{port}");
    axum::serve(listener, app)
        .await
        .expect("benchmark server failed");
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
