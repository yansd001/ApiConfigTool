using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace ApiConfigTool.Services;

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
            throw new InvalidOperationException($"Fetch models failed ({(int)response.StatusCode}): {Truncate(body, 300)}");

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

        if (!url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
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

        return url + "/v1/models";
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

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) ? string.Empty : (text.Length <= max ? text : text[..max] + "...");
}

public sealed class CodexConfigService
{
    private readonly string _codexDir;
    private readonly string _configPath;
    private readonly string _authPath;

    public CodexConfigService(string? codexDir = null)
    {
        _codexDir = codexDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        _configPath = Path.Combine(_codexDir, "config.toml");
        _authPath = Path.Combine(_codexDir, "auth.json");
    }

    public string ConfigPath => _configPath;
    public string AuthPath => _authPath;

    public (string? BaseUrl, string? Model, string? ApiKey) LoadCurrent()
    {
        string? baseUrl = null;
        string? model = null;
        string? apiKey = null;

        if (File.Exists(_configPath))
        {
            try
            {
                var modelTable = Toml.ToModel(File.ReadAllText(_configPath));
                if (modelTable.TryGetValue("model", out var modelObj) && modelObj is string m)
                    model = m;

                string providerName = "OpenAI";
                if (modelTable.TryGetValue("model_provider", out var providerObj) && providerObj is string p && !string.IsNullOrWhiteSpace(p))
                    providerName = p;

                if (modelTable.TryGetValue("model_providers", out var providersObj) && providersObj is TomlTable providers)
                {
                    if (providers.TryGetValue(providerName, out var providerTableObj) && providerTableObj is TomlTable providerTable)
                    {
                        if (providerTable.TryGetValue("base_url", out var bu) && bu is string s)
                            baseUrl = s;
                    }
                    else
                    {
                        foreach (var kv in providers)
                        {
                            if (kv.Value is TomlTable t && t.TryGetValue("base_url", out var bu) && bu is string s)
                            {
                                baseUrl = s;
                                break;
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        if (File.Exists(_authPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_authPath));
                if (doc.RootElement.TryGetProperty("OPENAI_API_KEY", out var key) && key.ValueKind == JsonValueKind.String)
                    apiKey = key.GetString();
            }
            catch
            {
            }
        }

        return (baseUrl, model, apiKey);
    }

    public void Apply(string baseUrl, string apiKey, string model)
    {
        Directory.CreateDirectory(_codexDir);
        var normalizedBaseUrl = ModelsApiService.NormalizeBaseUrl(baseUrl);

        if (!File.Exists(_configPath))
            CreateDefaultConfig(normalizedBaseUrl, model);
        else
            UpdateExistingConfig(normalizedBaseUrl, model);

        WriteAuth(apiKey);
    }

    private void CreateDefaultConfig(string baseUrl, string model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated by ApiConfigTool");
        sb.AppendLine("model_provider = \"OpenAI\"");
        sb.AppendLine($"model = \"{EscapeTomlString(model)}\"");
        sb.AppendLine($"review_model = \"{EscapeTomlString(model)}\"");
        sb.AppendLine("model_reasoning_effort = \"xhigh\"");
        sb.AppendLine("disable_response_storage = true");
        sb.AppendLine("network_access = \"enabled\"");
        sb.AppendLine("windows_wsl_setup_acknowledged = true");
        sb.AppendLine();
        sb.AppendLine("[model_providers.OpenAI]");
        sb.AppendLine("name = \"OpenAI\"");
        sb.AppendLine($"base_url = \"{EscapeTomlString(baseUrl)}\"");
        sb.AppendLine("wire_api = \"responses\"");
        sb.AppendLine("requires_openai_auth = true");
        sb.AppendLine();
        sb.AppendLine("[features]");
        sb.AppendLine("goals = true");
        File.WriteAllText(_configPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void UpdateExistingConfig(string baseUrl, string model)
    {
        var text = File.ReadAllText(_configPath);

        try
        {
            var table = Toml.ToModel(text);
            string providerName = "OpenAI";
            if (table.TryGetValue("model_provider", out var providerObj) && providerObj is string p && !string.IsNullOrWhiteSpace(p))
                providerName = p;

            text = ApplySurgicalTomlUpdates(text, model, baseUrl, providerName);
            File.WriteAllText(_configPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            text = ApplySurgicalTomlUpdates(text, model, baseUrl, "OpenAI");
            File.WriteAllText(_configPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static string ApplySurgicalTomlUpdates(string text, string model, string baseUrl, string providerName)
    {
        // Update top-level model = "..."
        var modelPattern = new Regex("^(\\s*model\\s*=\\s*)\"([^\"]*)\"", RegexOptions.Multiline);
        if (modelPattern.IsMatch(text))
            text = modelPattern.Replace(text, m => m.Groups[1].Value + "\"" + EscapeTomlString(model) + "\"", 1);
        else
            text = "model = \"" + EscapeTomlString(model) + "\"\n" + text;

        var baseUrlUpdated = false;
        var sectionHeader = "[model_providers." + providerName + "]";
        var sectionIndex = text.IndexOf(sectionHeader, StringComparison.Ordinal);
        if (sectionIndex >= 0)
        {
            var after = text.Substring(sectionIndex + sectionHeader.Length);
            var nextSection = after.IndexOf("\n[", StringComparison.Ordinal);
            var sectionBody = nextSection >= 0 ? after.Substring(0, nextSection) : after;
            var baseUrlPattern = new Regex("^(\\s*base_url\\s*=\\s*)\"([^\"]*)\"", RegexOptions.Multiline);
            if (baseUrlPattern.IsMatch(sectionBody))
            {
                var newBody = baseUrlPattern.Replace(sectionBody, m => m.Groups[1].Value + "\"" + EscapeTomlString(baseUrl) + "\"", 1);
                text = text.Substring(0, sectionIndex + sectionHeader.Length) + newBody + (nextSection >= 0 ? after.Substring(nextSection) : string.Empty);
                baseUrlUpdated = true;
            }
        }

        if (!baseUrlUpdated)
        {
            var anyBaseUrl = new Regex("^(\\s*base_url\\s*=\\s*)\"([^\"]*)\"", RegexOptions.Multiline);
            if (anyBaseUrl.IsMatch(text))
            {
                text = anyBaseUrl.Replace(text, m => m.Groups[1].Value + "\"" + EscapeTomlString(baseUrl) + "\"", 1);
                baseUrlUpdated = true;
            }
        }

        if (!baseUrlUpdated)
        {
            var append = new StringBuilder();
            append.AppendLine();
            append.AppendLine();
            append.AppendLine("[model_providers." + providerName + "]");
            append.AppendLine("name = \"" + EscapeTomlString(providerName) + "\"");
            append.AppendLine("base_url = \"" + EscapeTomlString(baseUrl) + "\"");
            append.AppendLine("wire_api = \"responses\"");
            append.AppendLine("requires_openai_auth = true");
            text = text.TrimEnd() + append.ToString();
        }

        return text;
    }

    private void WriteAuth(string apiKey)
    {
        JsonObject root;
        if (File.Exists(_authPath))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(_authPath)) as JsonObject ?? new JsonObject();
            }
            catch
            {
                root = new JsonObject();
            }
        }
        else
        {
            root = new JsonObject();
        }

        root["OPENAI_API_KEY"] = apiKey.Trim();
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_authPath, json + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string EscapeTomlString(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

public sealed class ClaudeConfigService
{
    private readonly string _claudeDir;
    private readonly string _settingsPath;

    public ClaudeConfigService(string? claudeDir = null)
    {
        _claudeDir = claudeDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        _settingsPath = Path.Combine(_claudeDir, "settings.json");
    }

    public string SettingsPath => _settingsPath;

    public (string? BaseUrl, string? Model, string? ApiKey) LoadCurrent()
    {
        if (!File.Exists(_settingsPath))
            return (null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
            var root = doc.RootElement;
            string? model = null;
            string? baseUrl = null;
            string? apiKey = null;

            if (root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                model = modelEl.GetString();

            if (root.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
            {
                if (env.TryGetProperty("ANTHROPIC_BASE_URL", out var bu) && bu.ValueKind == JsonValueKind.String)
                    baseUrl = bu.GetString();
                if (env.TryGetProperty("ANTHROPIC_AUTH_TOKEN", out var tok) && tok.ValueKind == JsonValueKind.String)
                    apiKey = tok.GetString();
            }

            return (baseUrl, model, apiKey);
        }
        catch
        {
            return (null, null, null);
        }
    }

    public void Apply(string baseUrl, string apiKey, string model)
    {
        Directory.CreateDirectory(_claudeDir);
        var normalizedBaseUrl = ModelsApiService.NormalizeClaudeBaseUrl(baseUrl);

        JsonObject root;
        if (File.Exists(_settingsPath))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(_settingsPath)) as JsonObject ?? new JsonObject();
            }
            catch
            {
                root = CreateDefaultRoot(normalizedBaseUrl, apiKey, model);
                Write(root);
                return;
            }

            var env = root["env"] as JsonObject;
            if (env is null)
            {
                env = new JsonObject();
                root["env"] = env;
            }

            env["ANTHROPIC_AUTH_TOKEN"] = apiKey.Trim();
            env["ANTHROPIC_BASE_URL"] = normalizedBaseUrl;
            if (env["API_TIMEOUT_MS"] is null)
                env["API_TIMEOUT_MS"] = "300000";

            root["model"] = model;
            Write(root);
        }
        else
        {
            root = CreateDefaultRoot(normalizedBaseUrl, apiKey, model);
            Write(root);
        }
    }

    private static JsonObject CreateDefaultRoot(string baseUrl, string apiKey, string model)
        => new()
        {
            ["env"] = new JsonObject
            {
                ["ANTHROPIC_AUTH_TOKEN"] = apiKey.Trim(),
                ["ANTHROPIC_BASE_URL"] = baseUrl,
                ["API_TIMEOUT_MS"] = "300000"
            },
            ["model"] = model
        };

    private void Write(JsonObject root)
    {
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
