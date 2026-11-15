using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ApiConfigTool.Services;

public sealed record BalanceAmountFormat(decimal QuotaPerUnit, decimal ExchangeRate, string Symbol)
{
    public string Format(decimal quota)
    {
        if (QuotaPerUnit <= 0 || ExchangeRate <= 0) return quota.ToString("N0", CultureInfo.InvariantCulture) + " 额度";
        try { return Symbol + (quota / QuotaPerUnit * ExchangeRate).ToString("N4", CultureInfo.InvariantCulture); }
        catch (OverflowException) { return quota.ToString("N0", CultureInfo.InvariantCulture) + " 额度"; }
    }
    public static BalanceAmountFormat Raw { get; } = new(0, 0, "");
}

public sealed record NewApiBalance(decimal TokenRemaining, decimal TokenUsed, bool UnlimitedToken,
    decimal? AccountRemaining, decimal? AccountUsed, DateTimeOffset? ExpiresAt,
    BalanceAmountFormat Format, DateTimeOffset UpdatedAt)
{
    public string Summary => AccountRemaining is { } account ? "账号余额 " + Format.Format(account) :
        UnlimitedToken ? "Key 额度不限额" : "Key 余额 " + Format.Format(TokenRemaining);
    public string Detail => AccountRemaining is not null
        ? (UnlimitedToken ? "Key：不限额" : "Key：" + Format.Format(TokenRemaining))
        : "账号余额未开放查询";
    public string Tooltip => $"{Summary}\n{(UnlimitedToken ? "Key：不限额" : "Key 剩余：" + Format.Format(TokenRemaining))}" +
        $"\nKey 已用：{Format.Format(TokenUsed)}" +
        (AccountUsed is { } used ? $"\n账号已用：{Format.Format(used)}" : "\n如需账号余额，请在 New API 的令牌设置中开启“允许查询用户额度”；旧版本可能不支持。") +
        (ExpiresAt is { } expires ? $"\nKey 到期：{expires.ToLocalTime():yyyy-MM-dd HH:mm}" : "") +
        (Format.QuotaPerUnit <= 0 ? "\n未取得站点换算参数，显示原始额度。" : "\n金额按站点公布的换算参数显示。") +
        $"\n更新：{UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
}

public sealed class BalanceQueryException(string message, TimeSpan? retryAfter = null, bool respectRetryAfter = false) : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public bool RespectRetryAfter { get; } = respectRetryAfter;
}

public interface IBalanceService
{
    Task<NewApiBalance> FetchAsync(ApiProfile profile, CancellationToken cancellationToken);
}

public sealed class NewApiBalanceService : IBalanceService, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly TimeProvider _time;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (BalanceAmountFormat Format, DateTimeOffset Expires)> _formats = new();

    public NewApiBalanceService(HttpClient? http = null, TimeProvider? time = null)
    {
        _ownsClient = http is null;
        // Never forward keys through redirects or reuse a panel's cookies.
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        if (_ownsClient) _http.Timeout = TimeSpan.FromSeconds(15);
        _time = time ?? TimeProvider.System;
    }

    public static Uri GetSiteRoot(string baseUrl)
    {
        var uri = new Uri(ProfileConfiguration.ValidateUrl(baseUrl));
        var path = uri.AbsolutePath.TrimEnd('/');
        // Retain reverse-proxy prefixes: /gateway/v1 -> /gateway/api/usage/token/.
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var version = Array.FindLastIndex(parts, p => p.Equals("v1", StringComparison.OrdinalIgnoreCase));
        if (version >= 0) path = version == 0 ? "" : "/" + string.Join('/', parts.Take(version));
        else if (parts.LastOrDefault() is "models" or "messages" or "responses")
            path = "/" + string.Join('/', parts.SkipLast(1));
        return new UriBuilder(uri) { Path = path.TrimEnd('/') + "/", Query = "", Fragment = "" }.Uri;
    }

    public async Task<NewApiBalance> FetchAsync(ApiProfile profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.ApiKey) || profile.ApiKey.Any(char.IsControl))
            throw new BalanceQueryException("API Key 无效", TimeSpan.FromMinutes(30));
        Uri root;
        try { root = GetSiteRoot(profile.BaseUrl); }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        { throw new BalanceQueryException("余额查询地址无效", TimeSpan.FromMinutes(30)); }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "api/usage/token/"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey.Trim());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw HttpError(response);
            using var usage = await ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
            // Validate before looking up currency metadata, so failures cannot appear as zero.
            var balance = ParseUsage(usage.RootElement, BalanceAmountFormat.Raw, _time.GetUtcNow());
            var format = await ReadFormatAsync(root, timeout.Token).ConfigureAwait(false);
            return balance with { Format = format, UpdatedAt = _time.GetUtcNow() };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new BalanceQueryException("余额查询超时"); }
        catch (HttpRequestException) { throw new BalanceQueryException("无法连接余额接口"); }
        catch (IOException) { throw new BalanceQueryException("余额接口读取失败"); }
        catch (JsonException) { throw new BalanceQueryException("未返回 New API 额度数据", TimeSpan.FromMinutes(30)); }
    }

    private async Task<BalanceAmountFormat> ReadFormatAsync(Uri root, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_formats.TryGetValue(root.AbsoluteUri, out var cached) && cached.Expires > now) return cached.Format;
        var format = BalanceAmountFormat.Raw;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "api/status"));
            // Public metadata endpoint deliberately receives no Authorization header.
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var json = await ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
                format = ParseFormat(json.RootElement);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or BalanceQueryException or OperationCanceledException) { }
        cancellationToken.ThrowIfCancellationRequested();
        _formats[root.AbsoluteUri] = (format, now.AddMinutes(format.QuotaPerUnit > 0 ? 60 : 5));
        return format;
    }

    internal static NewApiBalance ParseUsage(JsonElement root, BalanceAmountFormat format, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object) throw InvalidData();
        if ((root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) ||
            (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.False))
            throw new BalanceQueryException("查询被拒绝，请检查 Key 或站点权限", TimeSpan.FromMinutes(30));
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) throw InvalidData();
        if (!data.TryGetProperty("unlimited_quota", out var unlimited) || unlimited.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidData();
        var remaining = RequiredNumber(data, "total_available");
        var used = RequiredNumber(data, "total_used");
        decimal? accountRemaining = null, accountUsed = null;
        if (data.TryGetProperty("user_quota", out var account) && account.ValueKind != JsonValueKind.Null)
        {
            if (account.ValueKind != JsonValueKind.Object) throw InvalidData();
            accountRemaining = RequiredNumber(account, "total_available");
            accountUsed = RequiredNumber(account, "total_used");
        }
        DateTimeOffset? expires = null;
        if (data.TryGetProperty("expires_at", out var expiry) && expiry.ValueKind != JsonValueKind.Null)
        {
            if (expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt64(out var seconds)) throw InvalidData();
            try { if (seconds > 0) expires = DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { throw InvalidData(); }
        }
        return new(remaining, used, unlimited.GetBoolean(), accountRemaining, accountUsed, expires, format, now);
    }

    internal static BalanceAmountFormat ParseFormat(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)) return BalanceAmountFormat.Raw;
        if (!TryNumber(data, "quota_per_unit", out var unit) || unit <= 0) return BalanceAmountFormat.Raw;
        var type = data.TryGetProperty("quota_display_type", out var display) && display.ValueKind == JsonValueKind.String ? display.GetString() : null;
        if (type is null && data.TryGetProperty("display_in_currency", out var legacy))
            type = legacy.ValueKind == JsonValueKind.True ? "USD" : "TOKENS";
        switch (type)
        {
            case "USD": return new(unit, 1, "$ ");
            case "CNY" when TryNumber(data, "usd_exchange_rate", out var cny) && cny > 0: return new(unit, cny, "¥ ");
            case "CUSTOM" when TryNumber(data, "custom_currency_exchange_rate", out var rate) && rate > 0:
                var symbol = data.TryGetProperty("custom_currency_symbol", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                return !string.IsNullOrWhiteSpace(symbol) && symbol.Length <= 8 && !symbol.Any(char.IsControl) ? new(unit, rate, symbol + " ") : BalanceAmountFormat.Raw;
            default: return BalanceAmountFormat.Raw;
        }
    }

    private static decimal RequiredNumber(JsonElement data, string name) => TryNumber(data, name, out var number) ? number : throw InvalidData();
    private static bool TryNumber(JsonElement data, string name, out decimal value)
    {
        value = 0;
        return data.TryGetProperty(name, out var node) && (node.ValueKind == JsonValueKind.Number ? node.TryGetDecimal(out value) :
            node.ValueKind == JsonValueKind.String && decimal.TryParse(node.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value));
    }
    private static BalanceQueryException InvalidData() => new("未返回 New API 额度数据", TimeSpan.FromMinutes(30));

    private BalanceQueryException HttpError(HttpResponseMessage response)
    {
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new("Key 无效或已禁用", TimeSpan.FromMinutes(30)),
            HttpStatusCode.Forbidden => new("查询被拒绝：权限或 IP 限制", TimeSpan.FromMinutes(30)),
            HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => new("站点不支持 New API 余额查询", TimeSpan.FromMinutes(30)),
            HttpStatusCode.TooManyRequests => new("查询过于频繁，稍后重试", RetryDelay(response), respectRetryAfter: true),
            _ when (int)response.StatusCode is >= 300 and < 400 => new("余额接口发生重定向，请核对地址", TimeSpan.FromMinutes(30)),
            _ => new($"余额接口暂不可用（HTTP {(int)response.StatusCode}）")
        };
    }
    private TimeSpan RetryDelay(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date - _time.GetUtcNow()) ?? TimeSpan.FromMinutes(5);
        return TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 300, 3600));
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        const int limit = 256 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw InvalidData();
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(block, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > limit) throw InvalidData();
            buffer.Write(block, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
