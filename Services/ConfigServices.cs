using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApiConfigTool.Services;

public enum CodexConfigurationMode
{
    GptAccount,
    ApiConfiguration
}

public sealed class ModelsApiService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<IReadOnlyList<string>> FetchModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Base URL cannot be empty.");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API Key cannot be empty.");

        var endpoint = BuildModelsEndpoint(baseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"获取模型失败（HTTP {(int)response.StatusCode}），请检查地址、密钥及服务状态。");

        return ParseModelIds(body);
    }

    public static string EnsureScheme(string baseUrl)
    {
        var url = baseUrl.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }
        return url;
    }

    public static string NormalizeCodexBaseUrl(string baseUrl)
    {
        var url = EnsureScheme(baseUrl).TrimEnd('/');

        if (url.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            url = url[..^"/models".Length].TrimEnd('/');

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/")
            url += "/v1";

        return url;
    }

    public static string NormalizeClaudeBaseUrl(string baseUrl)
    {
        var url = EnsureScheme(baseUrl);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            url = url.TrimEnd('/');
            if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                url = url[..^3].TrimEnd('/');
            return url;
        }

        return string.Format("{0}://{1}", uri.Scheme, uri.Authority);
    }

    public static string NormalizeBaseUrl(string baseUrl) => NormalizeCodexBaseUrl(baseUrl);

    public static string BuildModelsEndpoint(string baseUrl)
    {
        var url = EnsureScheme(baseUrl).TrimEnd('/');

        if (url.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            return url;
        if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return url + "/models";

        return NormalizeCodexBaseUrl(url) + "/models";
    }

    private static IReadOnlyList<string> ParseModelIds(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var ids = new List<string>();

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    var id = idEl.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                        ids.Add(id!);
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var id = item.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                        ids.Add(id!);
                }
                else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    var id = idEl.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                        ids.Add(id!);
                }
            }
        }

        return ids
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

}
