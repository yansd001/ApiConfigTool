use super::*;
use serde_json::json;

#[test]
fn new_api_distinguishes_account_key_zero_and_unlimited() {
    let mut data = json!({"code":true,"data":{"total_available":"2500000","total_used":500000,"unlimited_quota":true,"user_quota":{"total_available":0,"total_used":1000000}}});
    let (rows, note) = parse_usage(&data).unwrap();
    assert_eq!(rows[0].label, "账号余额");
    assert_eq!(rows[0].remaining, Some(0.));
    assert!(rows[1].unlimited);
    assert!(rows[1].remaining.is_none());
    assert!(note.is_empty());
    data["data"].as_object_mut().unwrap().remove("user_quota");
    let (rows, note) = parse_usage(&data).unwrap();
    assert_eq!(rows.len(), 1);
    assert!(note.contains("未开放查询"));
    data["data"]["total_used"] = json!("NaN");
    assert!(parse_usage(&data).is_err());
    assert!(parse_usage(&json!({"success":false})).is_err());
}
#[test]
fn site_paths_and_currency_follow_the_site() {
    assert_eq!(
        site_root("https://example.com/gateway/v1/messages")
            .unwrap()
            .as_str(),
        "https://example.com/gateway/"
    );
    assert_eq!(
        site_root("https://example.com").unwrap().as_str(),
        "https://example.com/"
    );
    let status =
        json!({"data":{"quota_per_unit":500000,"quota_display_type":"CNY","usd_exchange_rate":7}});
    let (scale, unit) = parse_format(&status).unwrap();
    assert_eq!(unit, "CNY");
    assert!((2500000. * scale - 35.).abs() < 1e-8);
    assert!(parse_format(&json!({"data":{"quota_per_unit":500000}})).is_none());
    assert!(
        parse_format(&json!({"data":{"quota_per_unit":0,"quota_display_type":"USD"}})).is_none()
    );
}
#[test]
fn custom_data_rejects_invalid_or_missing_numbers() {
    let rows =
        parse_custom(&json!([{"planName":"A","total":10,"used":2,"unit":"USD"},{"remaining":-1}]))
            .unwrap();
    assert_eq!(rows[0].remaining, Some(8.));
    assert_eq!(rows[1].remaining, Some(-1.));
    for value in [
        json!({}),
        json!({"remaining":null}),
        json!({"remaining":"NaN"}),
        json!({"isValid":false,"remaining":0}),
    ] {
        assert!(parse_custom(&value).is_err());
    }
}

fn serve(responses: Vec<String>) -> (String, std::thread::JoinHandle<Vec<String>>) {
    use std::io::{Read, Write};
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    listener.set_nonblocking(true).unwrap();
    let base = format!("http://{}", listener.local_addr().unwrap());
    let task = std::thread::spawn(move || {
        let mut requests = Vec::new();
        for response in responses {
            let deadline = std::time::Instant::now() + Duration::from_secs(8);
            let mut stream = loop {
                match listener.accept() {
                    Ok((stream, _)) => break stream,
                    Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                        assert!(
                            std::time::Instant::now() < deadline,
                            "missing fixture request"
                        );
                        std::thread::sleep(Duration::from_millis(5));
                    }
                    Err(e) => panic!("fixture accept: {e}"),
                }
            };
            stream
                .set_read_timeout(Some(Duration::from_secs(5)))
                .unwrap();
            let mut bytes = Vec::new();
            let mut chunk = [0; 4096];
            loop {
                let count = stream.read(&mut chunk).unwrap();
                if count == 0 {
                    break;
                }
                bytes.extend_from_slice(&chunk[..count]);
                if let Some(end) = bytes.windows(4).position(|w| w == b"\r\n\r\n") {
                    let headers = String::from_utf8_lossy(&bytes[..end]).to_lowercase();
                    let length = headers
                        .lines()
                        .find_map(|line| {
                            line.strip_prefix("content-length:")
                                .and_then(|n| n.trim().parse::<usize>().ok())
                        })
                        .unwrap_or(0);
                    if bytes.len() >= end + 4 + length {
                        break;
                    }
                }
            }
            requests.push(String::from_utf8(bytes).unwrap());
            let _ = stream.write_all(response.as_bytes());
        }
        requests
    });
    (base, task)
}
fn response(code: &str, headers: &str, body: &str) -> String {
    format!("HTTP/1.1 {code}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n{headers}\r\n{body}", body.len())
}

#[test]
fn http_queries_deduplicate_preserve_last_good_and_respect_rate_limits() {
    let (base, server) = serve(vec![
        response(
            "200 OK",
            "",
            r#"{"data":{"total_available":500000,"total_used":0,"unlimited_quota":false,"user_quota":{"total_available":21250000,"total_used":1000000}}}"#,
        ),
        response(
            "200 OK",
            "",
            r#"{"data":{"quota_per_unit":500000,"quota_display_type":"USD"}}"#,
        ),
        response(
            "429 Too Many Requests",
            "Retry-After: 700\r\n",
            "private-server-error",
        ),
    ]);
    let p = Profile {
        base_url: format!("{base}/gateway/v1"),
        api_key: "fixture-key".into(),
        ..Default::default()
    };
    tauri::async_runtime::block_on(async {
        let service = BalanceService::default();
        let (a, b) = tokio::join!(service.query(&p, false), service.query(&p, false));
        let a = a.unwrap();
        assert_eq!(a.rows[0].remaining, Some(42.5));
        assert_eq!(a.rows[1].remaining, Some(1.));
        assert_eq!(b.unwrap().updated_at, a.updated_at);
        let entry = service.cache.lock().await.values().next().unwrap().clone();
        {
            let mut cache = entry.lock().await;
            cache.state.next_retry_at = 0;
            cache.state.can_refresh_at = 0;
        }
        let failed = service.query(&p, false).await.unwrap();
        assert_eq!(failed.rows[0].remaining, Some(42.5));
        assert!(failed.error.as_deref().unwrap().contains("过于频繁"));
        assert_eq!(failed.updated_at, a.updated_at);
        assert!(failed.can_refresh_at >= now() + 699);
        assert_eq!(
            service.query(&p, true).await.unwrap().can_refresh_at,
            failed.can_refresh_at
        );
    });
    let requests = server.join().unwrap();
    assert!(requests[0].starts_with("GET /gateway/api/usage/token/"));
    assert!(requests[0]
        .to_lowercase()
        .contains("authorization: bearer fixture-key"));
    assert!(requests[1].starts_with("GET /gateway/api/status "));
    assert!(!requests[1].to_lowercase().contains("authorization"));
}

#[test]
fn custom_http_uses_overrides_json_body_and_multiple_plans() {
    let (base, server) = serve(vec![response(
        "200 OK",
        "",
        r#"{"balance":9,"used":1,"echo":"query-key"}"#,
    )]);
    let p = Profile { base_url: "https://unused.invalid/v1".into(), api_key: "provider-key".into(), balance: BalanceConfig {
        mode: Mode::Custom, base_url: base, api_key: "query-key".into(), access_token: "panel-token".into(), user_id: "99".into(),
        script: r#"({request:{url:'{{baseUrl}}/balance?user={{userId}}',method:'POST',headers:{Authorization:'Bearer {{apiKey}}','x-panel-token':'{{accessToken}}'},body:{user:'{{userId}}'}},extractor:r=>[{planName:'账号余额',remaining:r.balance,used:r.used,unit:'USD',extra:r.echo},{planName:'套餐',total:100,used:2,unit:'次'}]})"#.into(),
        ..Default::default()
    }, ..Default::default() };
    let result = tauri::async_runtime::block_on(BalanceService::default().query(&p, true)).unwrap();
    assert!(result.error.is_none(), "{:?}", result.error);
    assert_eq!(result.rows[0].remaining, Some(9.));
    assert_eq!(result.rows[0].extra, "[已隐藏]");
    assert_eq!(result.rows[1].remaining, Some(98.));
    let requests = server.join().unwrap();
    assert!(requests[0].starts_with("POST /balance?user=99 "));
    assert!(requests[0]
        .to_lowercase()
        .contains("authorization: bearer query-key"));
    assert!(requests[0].contains(r#"{"user":"99"}"#));
    assert!(!requests[0].contains("provider-key"));
}

#[test]
fn errors_do_not_follow_redirects_echo_bodies_or_become_zero() {
    for (code, headers, body) in [
        (
            "302 Found",
            "Location: http://127.0.0.1:1/leak\r\n",
            "private error",
        ),
        ("401 Unauthorized", "", "private error"),
        ("200 OK", "", "<html>private error</html>"),
        ("200 OK", "", r#"{"success":true,"data":{}}"#),
    ] {
        let (base, server) = serve(vec![response(code, headers, body)]);
        let p = Profile {
            base_url: base,
            api_key: "secret-key".into(),
            ..Default::default()
        };
        let result =
            tauri::async_runtime::block_on(BalanceService::default().query(&p, false)).unwrap();
        assert!(result.rows.is_empty());
        assert!(result.updated_at.is_none());
        let error = result.error.unwrap();
        assert!(!error.contains("private error"));
        assert!(!error.contains("secret-key"));
        server.join().unwrap();
    }
}
#[test]
fn metadata_failure_keeps_raw_quota_and_manual_mode_does_not_request() {
    let (base, server) = serve(vec![
        response(
            "200 OK",
            "",
            r#"{"data":{"total_available":0,"total_used":5,"unlimited_quota":false}}"#,
        ),
        response("404 Not Found", "", ""),
    ]);
    let mut p = Profile {
        base_url: base,
        api_key: "key".into(),
        ..Default::default()
    };
    p.balance.interval_minutes = 0;
    tauri::async_runtime::block_on(async {
        let service = BalanceService::default();
        let manual = service.query(&p, false).await.unwrap();
        assert!(manual.updated_at.is_none());
        let result = service.query(&p, true).await.unwrap();
        assert_eq!(result.rows[0].remaining, Some(0.));
        assert_eq!(result.rows[0].unit, "额度");
        assert!(result.note.contains("未开放查询"));
        assert!(result.note.contains("原始额度"));
        p.balance.mode = Mode::Off;
        assert!(service.query(&p, true).await.unwrap().rows.is_empty());
    });
    server.join().unwrap();
}
