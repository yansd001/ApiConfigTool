using System.Net;
using System.Net.Http;
using System.Text.Json;
using ApiConfigTool.Services;

internal static partial class Program
{
    private const string Usage = """
        {"code":true,"message":"ok","data":{"object":"token_usage","total_available":2500000,"total_used":500000,"unlimited_quota":false,"expires_at":0,"user_quota":{"total_available":21250000,"total_used":1000000}}}
        """;
    private const string Status = """
        {"success":true,"data":{"quota_per_unit":500000,"quota_display_type":"USD"}}
        """;
    private static ApiProfile BalanceProfile(string name = "A", string key = "sk-test-key") => new(name, name, "https://newapi.example/v1", key, "model");
    private static NewApiBalance ParseBalance(string text = Usage)
    {
        using var json = JsonDocument.Parse(text);
        return NewApiBalanceService.ParseUsage(json.RootElement, new(500000, 1, "$ "), DateTimeOffset.UtcNow);
    }
    private static BalanceAmountFormat ParseCurrency(string text)
    {
        using var json = JsonDocument.Parse(text);
        return NewApiBalanceService.ParseFormat(json.RootElement);
    }

    private static async Task BalanceChecks()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("New API account balance is distinguished from key quota", () => { var b = ParseBalance(); Check(b.AccountRemaining == 21250000 && b.TokenRemaining == 2500000); Check(b.Summary == "账号余额 $ 42.5000"); Check(b.Detail.Contains("5.0000")); return Task.CompletedTask; }),
            ("Token-only response never claims account balance", () => { var b = ParseBalance("{\"data\":{\"total_available\":1000000,\"total_used\":0,\"unlimited_quota\":false}}"); Check(b.AccountRemaining is null); Check(b.Summary == "Key 余额 $ 2.0000"); Check(b.Detail.Contains("未开放")); return Task.CompletedTask; }),
            ("Unlimited token does not mean unlimited account", () => { var b = ParseBalance(Usage.Replace("\"unlimited_quota\":false", "\"unlimited_quota\":true").Replace("21250000", "0")); Check(b.Summary == "账号余额 $ 0.0000"); Check(b.Detail.Contains("不限额")); var token = ParseBalance("{\"data\":{\"total_available\":0,\"total_used\":0,\"unlimited_quota\":true}}"); Check(token.Summary == "Key 额度不限额"); return Task.CompletedTask; }),
            ("Zero and negative quota remain valid values", () => { var b = ParseBalance(Usage.Replace("21250000", "-500000")); Check(b.Summary == "账号余额 $ -1.0000"); Check(b.ExpiresAt is null); return Task.CompletedTask; }),
            ("Currency display uses public site conversion parameters", () => { Check(ParseCurrency(Status).Format(500000) == "$ 1.0000"); Check(ParseCurrency("{\"data\":{\"quota_per_unit\":1000000,\"quota_display_type\":\"CNY\",\"usd_exchange_rate\":7.2}}").Format(1000000) == "¥ 7.2000"); Check(ParseCurrency("{\"data\":{\"quota_per_unit\":500000,\"quota_display_type\":\"CUSTOM\",\"custom_currency_symbol\":\"€\",\"custom_currency_exchange_rate\":0.9}}").Format(500000) == "€ 0.9000"); return Task.CompletedTask; }),
            ("Missing conversion never invents dollar amounts", () => { foreach (var s in new[] { "{}", "{\"data\":{\"quota_per_unit\":0}}", "{\"data\":{\"quota_per_unit\":500000,\"quota_display_type\":\"TOKENS\"}}", "{\"data\":{\"quota_per_unit\":500000,\"quota_display_type\":\"CNY\"}}" }) Check(ParseCurrency(s).Format(500000) == "500,000 额度"); return Task.CompletedTask; }),
            ("Invalid and denied usage responses do not look like zero balance", () => { foreach (var data in new[] { "{}", "[]", "{\"success\":false,\"message\":\"sk-secret\"}", "{\"data\":{\"total_available\":0}}", Usage.Replace("21250000", "\"bad-number\""), Usage.Replace("\"expires_at\":0", "\"expires_at\":\"bad\"") }) Throws(() => ParseBalance(data)); return Task.CompletedTask; }),
            ("Site roots preserve reverse-proxy prefixes and ports", () => { Check(NewApiBalanceService.GetSiteRoot("https://a.example/v1").AbsoluteUri == "https://a.example/"); Check(NewApiBalanceService.GetSiteRoot("https://a.example:8443/gateway/v1/models").AbsoluteUri == "https://a.example:8443/gateway/"); Check(NewApiBalanceService.GetSiteRoot("https://a.example/gateway/").AbsoluteUri == "https://a.example/gateway/"); Throws(() => NewApiBalanceService.GetSiteRoot("https://key@a.example/v1")); return Task.CompletedTask; }),
            ("HTTP usage sends Bearer only to intended endpoint", async () => { var calls = new List<string>(); using var http = new HttpClient(new StubHttp(request => { calls.Add(request.RequestUri!.AbsolutePath); if (request.RequestUri.AbsolutePath.EndsWith("/api/usage/token/")) { Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "sk-test-key"); return JsonResponse(Usage); } Check(request.Headers.Authorization is null); return JsonResponse(Status); })); using var service = new NewApiBalanceService(http); var b = await service.FetchAsync(BalanceProfile() with { BaseUrl = "https://newapi.example/prefix/v1" }, default); Check(b.Summary.Contains("42.5000")); Check(calls.SequenceEqual(new[] { "/prefix/api/usage/token/", "/prefix/api/status" })); }),
            ("Public status is cached between queries", async () => { var statusCalls = 0; using var http = new HttpClient(new StubHttp(request => { if (request.RequestUri!.AbsolutePath.EndsWith("status")) { statusCalls++; return JsonResponse(Status); } return JsonResponse(Usage); })); using var service = new NewApiBalanceService(http); await service.FetchAsync(BalanceProfile(), default); await service.FetchAsync(BalanceProfile(), default); Check(statusCalls == 1); }),
            ("Status failure keeps valid raw quota", async () => { using var http = new HttpClient(new StubHttp(request => request.RequestUri!.AbsolutePath.EndsWith("status") ? new(HttpStatusCode.Forbidden) : JsonResponse(Usage))); using var service = new NewApiBalanceService(http); var b = await service.FetchAsync(BalanceProfile(), default); Check(b.Summary == "账号余额 21,250,000 额度"); }),
            ("Non-New-API, denied and redirected endpoints fail without leaking bodies", async () => { foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.Redirect, HttpStatusCode.InternalServerError }) { using var http = new HttpClient(new StubHttp(_ => new(status) { Content = new StringContent("sk-secret") })); using var service = new NewApiBalanceService(http); var ex = await QueryFails(() => service.FetchAsync(BalanceProfile(), default)); Check(!ex.Message.Contains("sk-secret")); } }),
            ("Rate-limit Retry-After is honored", async () => { using var http = new HttpClient(new StubHttp(_ => { var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests); r.Headers.RetryAfter = new(TimeSpan.FromMinutes(20)); return r; })); using var service = new NewApiBalanceService(http); var error = await QueryFails(() => service.FetchAsync(BalanceProfile(), default)); Check(error.RetryAfter == TimeSpan.FromMinutes(20)); }),
            ("HTML and oversized responses are rejected", async () => { foreach (var text in new[] { "<html>Login</html>", new string('x', 300000) }) { using var http = new HttpClient(new StubHttp(_ => JsonResponse(text))); using var service = new NewApiBalanceService(http); await QueryFails(() => service.FetchAsync(BalanceProfile(), default)); } }),
            ("Balance monitor deduplicates across apps and models", async () => { var fake = new CountingBalance(); using var m = new BalanceMonitor(fake); var a = BalanceProfile(); await m.RefreshAsync(new[] { a, a with { Id = "B", Model = "other" }, a with { Id = "C", BaseUrl = "https://newapi.example" } }); Check(fake.Calls == 1); Check(m.Get(a).Balance is not null); }),
            ("Monitor only refreshes when due and preserves last good result", async () => { var time = new TestTime(); var fake = new CountingBalance(); using var m = new BalanceMonitor(fake, time); var p = BalanceProfile(); await m.RefreshAsync(new[] { p }); await m.RefreshAsync(new[] { p }); Check(fake.Calls == 1); time.Advance(TimeSpan.FromMinutes(5)); fake.Error = new BalanceQueryException("offline"); await m.RefreshAsync(new[] { p }); Check(fake.Calls == 2); Check(m.Get(p).Summary.Contains("账号余额")); Check(m.Get(p).Detail.Contains("上次结果")); fake.Error = null; time.Advance(TimeSpan.FromMinutes(5)); await m.RefreshAsync(new[] { p }); Check(m.Get(p).Error is null); }),
            ("Unsupported endpoints back off and manual refresh is debounced", async () => { var time = new TestTime(); var fake = new CountingBalance { Error = new BalanceQueryException("unsupported", TimeSpan.FromMinutes(30)) }; using var m = new BalanceMonitor(fake, time); var p = BalanceProfile(); await m.RefreshAsync(new[] { p }); await m.RefreshAsync(new[] { p }, true); Check(fake.Calls == 1); time.Advance(TimeSpan.FromMinutes(5)); await m.RefreshAsync(new[] { p }); Check(fake.Calls == 1); await m.RefreshAsync(new[] { p }, true); Check(fake.Calls == 2); }),
            ("Edited keys never reuse an old balance", async () => { var fake = new CountingBalance(); using var m = new BalanceMonitor(fake); var a = BalanceProfile(); await m.RefreshAsync(new[] { a }); var b = a with { ApiKey = "sk-new-key" }; Check(m.Get(b).Balance is null); await m.RefreshAsync(new[] { b }); Check(fake.Calls == 2); Check(m.Get(a).Balance is null); }),
            ("Manual refresh cannot bypass server Retry-After", async () => { var time = new TestTime(); var fake = new CountingBalance { Error = new BalanceQueryException("rate limited", TimeSpan.FromMinutes(20), true) }; using var m = new BalanceMonitor(fake, time); var p = BalanceProfile(); await m.RefreshAsync(new[] { p }); time.Advance(TimeSpan.FromMinutes(5)); await m.RefreshAsync(new[] { p }, true); Check(fake.Calls == 1); time.Advance(TimeSpan.FromMinutes(15)); await m.RefreshAsync(new[] { p }, true); Check(fake.Calls == 2); }),
            ("Polling limits concurrency, avoids overlap and cancels on close", async () => { var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var fake = new CountingBalance { Wait = gate.Task }; using var m = new BalanceMonitor(fake); var profiles = Enumerable.Range(0, 10).Select(i => BalanceProfile(i.ToString(), "sk-" + i)).ToArray(); var running = m.RefreshAsync(profiles); Check(fake.Calls == 3); await m.RefreshAsync(profiles, true); Check(fake.Calls == 3); m.Dispose(); await running; Check(!m.IsRunning); Check(profiles.All(p => !m.Get(p).Refreshing)); }),
            ("One failed query does not stop other balances", async () => { var fake = new SelectiveBalance(); using var m = new BalanceMonitor(fake); var a = BalanceProfile("A", "sk-good"); var b = BalanceProfile("B", "sk-bad"); await m.RefreshAsync(new[] { a, b }); Check(m.Get(a).Balance is not null); Check(m.Get(b).Error == "余额查询失败"); Check(!m.Get(b).Tooltip.Contains("sk-secret")); })
        };
        foreach (var test in tests) { await test.Run(); _passed++; Console.WriteLine("PASS " + test.Name); }
    }

    private static HttpResponseMessage JsonResponse(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json") };
    private static async Task<BalanceQueryException> QueryFails(Func<Task<NewApiBalance>> action)
    {
        try { await action(); } catch (BalanceQueryException ex) { return ex; }
        throw new Exception("Expected safe balance query error");
    }
    private sealed class StubHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    private sealed class TestTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class CountingBalance : IBalanceService
    {
        public int Calls;
        public Exception? Error;
        public Task? Wait;
        public async Task<NewApiBalance> FetchAsync(ApiProfile profile, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            if (Wait is not null) await Wait.WaitAsync(token);
            if (Error is not null) throw Error;
            return ParseBalance();
        }
    }
    private sealed class SelectiveBalance : IBalanceService
    {
        public Task<NewApiBalance> FetchAsync(ApiProfile p, CancellationToken token) => p.ApiKey == "sk-bad" ? throw new Exception("sk-secret") : Task.FromResult(ParseBalance());
    }
    private sealed class DemoBalanceService : IBalanceService
    {
        public Task<NewApiBalance> FetchAsync(ApiProfile p, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var balance = ParseBalance();
            if (p.Name.Contains("备用")) balance = balance with { AccountRemaining = null, AccountUsed = null, UnlimitedToken = true };
            return Task.FromResult(balance);
        }
    }
}
