using System.Security.Cryptography;
using System.Text;

namespace ApiConfigTool.Services;

public sealed record BalanceSnapshot(NewApiBalance? Balance, string? Error, bool Refreshing, DateTimeOffset? LastAttempt, DateTimeOffset NextAttempt, DateTimeOffset? RetryNotBefore = null)
{
    public string Summary => Balance?.Summary ?? (Refreshing ? "余额查询中…" : Error ?? "余额待查询");
    public string Detail => Refreshing ? "正在刷新…" : Error is not null
        ? (Balance is null ? "稍后自动重试" : "更新失败 · 显示上次结果")
        : Balance is not null ? $"{Balance.Detail} · {Balance.UpdatedAt.ToLocalTime():HH:mm}" : "New API · 每 5 分钟更新";
    public string Tooltip => (Balance?.Tooltip ?? "使用配置中的 API Key 查询 New API 额度。") +
        (Error is null ? "" : $"\n本次查询：{Error}\n下次尝试：{NextAttempt.ToLocalTime():HH:mm:ss}");
}

public sealed class BalanceMonitor : IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private readonly IBalanceService _service;
    private readonly TimeProvider _time;
    private readonly bool _ownsService;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BalanceSnapshot> _cache = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _parallelism = new(3);
    private int _running;
    private bool _disposed;
    public event EventHandler? Changed;
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public BalanceMonitor(IBalanceService? service = null, TimeProvider? time = null)
    {
        _service = service ?? new NewApiBalanceService();
        _ownsService = service is null;
        _time = time ?? TimeProvider.System;
    }

    public BalanceSnapshot Get(ApiProfile profile) => _cache.GetValueOrDefault(Key(profile)) ?? new(null, null, false, null, DateTimeOffset.MinValue);

    // Same endpoint/key in multiple apps or models shares one query and one cache.
    internal static string Key(ApiProfile profile)
    {
        string endpoint;
        try { endpoint = NewApiBalanceService.GetSiteRoot(profile.BaseUrl).AbsoluteUri; }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException) { endpoint = profile.BaseUrl; }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint + "\n" + profile.ApiKey.Trim())));
    }

    public async Task RefreshAsync(IEnumerable<ApiProfile> profiles, bool force = false, CancellationToken cancellationToken = default)
    {
        if (_disposed || Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try
        {
            var unique = profiles.GroupBy(Key).ToDictionary(group => group.Key, group => group.First());
            foreach (var key in _cache.Keys.Where(key => !unique.ContainsKey(key))) _cache.TryRemove(key, out _);
            var now = _time.GetUtcNow();
            var due = unique.Where(p => !_cache.TryGetValue(p.Key, out var value) ||
                ((value.RetryNotBefore is null || value.RetryNotBefore <= now) &&
                 (force ? value.LastAttempt is null || now - value.LastAttempt >= TimeSpan.FromSeconds(10) : value.NextAttempt <= now)));
            Changed?.Invoke(this, EventArgs.Empty);
            await Task.WhenAll(due.Select(pair => RefreshOneAsync(pair.Key, pair.Value, linked.Token))).ConfigureAwait(false);
        }
        finally { Interlocked.Exchange(ref _running, 0); Changed?.Invoke(this, EventArgs.Empty); }
    }

    private async Task RefreshOneAsync(string key, ApiProfile profile, CancellationToken token)
    {
        var entered = false;
        var previous = _cache.GetValueOrDefault(key) ?? new(null, null, false, null, DateTimeOffset.MinValue);
        try
        {
            await _parallelism.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            _cache[key] = previous with { Refreshing = true };
            Changed?.Invoke(this, EventArgs.Empty);
            var result = await _service.FetchAsync(profile, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var now = _time.GetUtcNow();
            _cache[key] = new(result, null, false, now, now + RefreshInterval);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { _cache[key] = previous with { Refreshing = false }; }
        catch (Exception ex)
        {
            var now = _time.GetUtcNow();
            // Arbitrary remote error bodies and keys never reach the UI/log.
            var error = ex is BalanceQueryException expected ? expected.Message : "余额查询失败";
            var delay = ex is BalanceQueryException query ? query.RetryAfter ?? RefreshInterval : RefreshInterval;
            _cache[key] = new(previous.Balance, error, false, now, now + delay,
                ex is BalanceQueryException { RespectRetryAfter: true } ? now + delay : null);
        }
        finally { if (entered) _parallelism.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        if (_ownsService && _service is IDisposable disposable) disposable.Dispose();
        // In-flight tasks still need the semaphore/CTS while unwinding cancellation.
    }
}
