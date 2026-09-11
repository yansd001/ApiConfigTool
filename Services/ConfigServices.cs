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
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private sealed record ConfigurationBackup(string? ConfigToml, JsonNode? Auth, bool HasConfigValue, bool HasAuthValue);

    private readonly string _codexDir;
    private readonly string _configPath;
    private readonly string _authPath;
    private readonly string _statePath;

    public CodexConfigService(string? codexDir = null)
    {
        _codexDir = codexDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        _configPath = Path.Combine(_codexDir, "config.toml");
        _authPath = Path.Combine(_codexDir, "auth.json");
        _statePath = Path.Combine(_codexDir, "apiconfig_state.json");
    }

    public string ConfigPath => _configPath;
    public string AuthPath => _authPath;
    public string StatePath => _statePath;
    public bool HasApiBackup => ReadBackup("api").HasConfigValue;

    public CodexConfigurationMode GetCurrentMode()
    {
        var storedMode = ReadStoredMode();
        if (storedMode.HasValue)
            return storedMode.Value;

        // 首次使用时（配置文件不存在），默认为 API 配置模式，方便用户直接配置
        if (!File.Exists(_configPath))
            return RememberDetectedMode(CodexConfigurationMode.ApiConfiguration);

        var text = File.ReadAllText(_configPath);
        if (TryGetModelProvider(text, out var providerName) &&
            ContainsModelProviderSection(text, providerName!))
            return RememberDetectedMode(CodexConfigurationMode.ApiConfiguration);

        // Accept older/API configurations that omit the top-level
        // model_provider key but still define a provider with base_url.
        try
        {
            var table = Toml.ToModel(text);
            if (table.TryGetValue("model_providers", out var providersObj) && providersObj is TomlTable providers &&
                providers.Values.OfType<TomlTable>().Any(provider =>
                    provider.TryGetValue("base_url", out var baseUrl) && baseUrl is string value &&
                    !string.IsNullOrWhiteSpace(value)))
                return RememberDetectedMode(CodexConfigurationMode.ApiConfiguration);
        }
        catch
        {
            // Fall through to GPT mode for unreadable configuration files.
        }

        // A pre-existing GPT login without an API snapshot has never been
        // managed by this tool. Start in API mode so the user can configure it
        // once; subsequent launches follow the persisted state.
        var initialMode = HasApiBackup
            ? CodexConfigurationMode.GptAccount
            : CodexConfigurationMode.ApiConfiguration;
        return RememberDetectedMode(initialMode);
    }

    private CodexConfigurationMode RememberDetectedMode(CodexConfigurationMode mode)
    {
        WriteStoredMode(mode);
        return mode;
    }

    public void SwitchToGptAccount()
    {
        if (GetCurrentMode() == CodexConfigurationMode.GptAccount)
            return;

        var gptBackup = ReadBackup("gpt");

        var currentConfig = File.Exists(_configPath) ? File.ReadAllText(_configPath) : null;
        var currentAuth = ReadJsonNode(_authPath);
        WriteBackup("api", currentConfig, currentAuth);

        var configForGpt = gptBackup.HasConfigValue
            ? gptBackup.ConfigToml
            : RemoveApiProviderConfiguration(currentConfig ?? string.Empty, out _);
        RestoreFile(_configPath, configForGpt);

        if (gptBackup.HasAuthValue)
            RestoreFile(_authPath, gptBackup.Auth?.ToJsonString(IndentedJson));
        else
            RemoveApiKeyFromAuth();

        WriteStoredMode(CodexConfigurationMode.GptAccount);
    }

    public void SwitchToApiConfiguration()
    {
        if (GetCurrentMode() == CodexConfigurationMode.ApiConfiguration)
            return;

        var apiBackup = ReadBackup("api");
        if (!apiBackup.HasConfigValue)
            throw new InvalidOperationException($"未找到 API 配置备份：{_statePath}。请先使用 API 配置保存一次，再切换到 GPT 账号后重试。");

        var currentConfig = File.Exists(_configPath) ? File.ReadAllText(_configPath) : null;
        var currentAuth = ReadJsonNode(_authPath);
        WriteBackup("gpt", currentConfig, currentAuth);

        RestoreFile(_configPath, apiBackup.ConfigToml);
        if (apiBackup.HasAuthValue)
            RestoreFile(_authPath, apiBackup.Auth?.ToJsonString(IndentedJson));
        else
            RestoreFile(_authPath, null);

        WriteStoredMode(CodexConfigurationMode.ApiConfiguration);
    }

    public (string? BaseUrl, string? Model, string? ApiKey) LoadCurrent()
    {
        string? baseUrl = null;
        string? model = null;
        string? apiKey = null;

        string? configText = File.Exists(_configPath) ? File.ReadAllText(_configPath) : null;

        if (!string.IsNullOrWhiteSpace(configText))
        {
            try
            {
                var modelTable = Toml.ToModel(configText);
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

        var authText = File.Exists(_authPath) ? File.ReadAllText(_authPath) : null;
        if (!string.IsNullOrWhiteSpace(authText))
        {
            try
            {
                using var doc = JsonDocument.Parse(authText);
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

        // When the user opens the tool while Codex is using GPT account login,
        // allow saving API settings directly while retaining the account state
        // for a later switch back to GPT.
        if (GetCurrentMode() == CodexConfigurationMode.GptAccount && !ReadBackup("gpt").HasConfigValue)
        {
            var currentConfig = File.Exists(_configPath) ? File.ReadAllText(_configPath) : null;
            var currentAuth = ReadJsonNode(_authPath);
            WriteBackup("gpt", currentConfig, currentAuth);
        }

        if (!File.Exists(_configPath))
            CreateDefaultConfig(normalizedBaseUrl, model);
        else
            UpdateExistingConfig(normalizedBaseUrl, model);

        WriteAuth(apiKey);
        WriteStoredMode(CodexConfigurationMode.ApiConfiguration);
    }

    private static bool TryGetModelProvider(string text, out string? providerName)
    {
        providerName = null;
        var inSection = false;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                inSection = true;
                continue;
            }

            if (inSection)
                continue;

            var match = Regex.Match(line, "^\\s*model_provider\\s*=\\s*(?<value>\\\"(?:\\\\.|[^\\\"])*\\\"|'[^']*')", RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            providerName = ParseTomlString(match.Groups["value"].Value);
            return !string.IsNullOrWhiteSpace(providerName);
        }

        return false;
    }

    private static bool ContainsModelProviderSection(string text, string providerName)
    {
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var header = ParseSectionHeader(line);
            if (header is not null && IsModelProviderSection(header, providerName))
                return true;
        }

        return false;
    }

    // Keep the user's TOML formatting and unrelated sections while removing the selected provider.
    private static string RemoveApiProviderConfiguration(string text, out string? providerName)
    {
        providerName = null;
        if (string.IsNullOrEmpty(text))
            return text;

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var topLevelProviderLine = -1;
        var inSection = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var header = ParseSectionHeader(lines[index]);
            if (header is not null)
            {
                inSection = true;
                continue;
            }

            if (inSection)
                continue;

            var match = Regex.Match(lines[index], "^\\s*model_provider\\s*=\\s*(?<value>\\\"(?:\\\\.|[^\\\"])*\\\"|'[^']*')", RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            providerName = ParseTomlString(match.Groups["value"].Value);
            topLevelProviderLine = index;
            break;
        }

        if (string.IsNullOrWhiteSpace(providerName))
            return text;

        var removeStart = -1;
        var removeEnd = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            var header = ParseSectionHeader(lines[index]);
            if (header is null)
                continue;

            if (removeStart < 0)
            {
                if (IsModelProviderSection(header, providerName))
                    removeStart = index;
                continue;
            }

            if (!IsModelProviderSection(header, providerName))
            {
                removeEnd = index;
                break;
            }
        }

        if (removeStart >= 0)
        {
            removeEnd = removeEnd >= 0 ? removeEnd : lines.Count;
            lines.RemoveRange(removeStart, removeEnd - removeStart);
            if (topLevelProviderLine > removeStart)
                topLevelProviderLine -= removeEnd - removeStart;
        }

        if (topLevelProviderLine >= 0 && topLevelProviderLine < lines.Count)
            lines.RemoveAt(topLevelProviderLine);

        return string.Join(newline, lines);
    }

    private static string? ParseSectionHeader(string line)
    {
        var match = Regex.Match(line, @"^\s*\[(?<header>[^\]]+)\]\s*(?:#.*)?$", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["header"].Value.Trim() : null;
    }

    private static bool IsModelProviderSection(string header, string providerName)
    {
        const string prefix = "model_providers.";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var path = header[prefix.Length..];
        var first = FirstTomlPathSegment(path);
        var parsed = ParseTomlString(first) ?? first;
        return string.Equals(parsed, providerName, StringComparison.Ordinal);
    }

    private static string FirstTomlPathSegment(string path)
    {
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < path.Length; index++)
        {
            var character = path[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (quote == '"' && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            if (character == '.')
                return path[..index].Trim();
        }

        return path.Trim();
    }

    private static string? ParseTomlString(string value)
    {
        value = value.Trim();
        if (value.Length < 2)
            return null;

        if (value[0] == '\'' && value[^1] == '\'')
            return value[1..^1];
        if (value[0] != '"' || value[^1] != '"')
            return null;

        var content = value[1..^1];
        return content
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private void WriteBackup(string slot, string? configToml, JsonNode? auth)
    {
        // Store raw TOML plus the complete auth JSON so switching does not discard unknown fields.
        var root = new JsonObject
        {
            ["config"] = configToml,
            ["auth"] = auth?.DeepClone()
        };
        var state = ReadStateRoot();
        state[slot] = root;
        WriteStateRoot(state);
    }

    private CodexConfigurationMode? ReadStoredMode()
    {
        try
        {
            var root = ReadStateRoot();
            var mode = root["current_mode"]?.GetValue<string>();
            if (string.Equals(mode, "api", StringComparison.OrdinalIgnoreCase))
                return CodexConfigurationMode.ApiConfiguration;
            if (string.Equals(mode, "gpt", StringComparison.OrdinalIgnoreCase))
                return CodexConfigurationMode.GptAccount;
        }
        catch
        {
        }

        return null;
    }

    private void WriteStoredMode(CodexConfigurationMode mode)
    {
        var root = ReadStateRoot();
        root["current_mode"] = mode == CodexConfigurationMode.ApiConfiguration ? "api" : "gpt";
        WriteStateRoot(root);
    }

    private JsonObject ReadStateRoot()
    {
        if (!File.Exists(_statePath))
            return new JsonObject();

        try
        {
            return JsonNode.Parse(File.ReadAllText(_statePath)) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private void WriteStateRoot(JsonObject root)
    {
        var json = root.ToJsonString(IndentedJson) + Environment.NewLine;
        WriteTextAtomically(_statePath, json);
    }

    private ConfigurationBackup ReadBackup(string slot)
    {
        try
        {
            var root = ReadStateRoot()[slot]?.DeepClone() as JsonObject;
            if (root is null)
                return new ConfigurationBackup(null, null, false, false);

            string? config = null;
            if (root["config"] is JsonValue configValue && configValue.TryGetValue<string>(out var configText))
                config = configText;
            else if (root["config_toml"] is JsonValue legacyConfig && legacyConfig.TryGetValue<string>(out var legacyText))
                config = legacyText;

            var hasConfig = root.ContainsKey("config") || root.ContainsKey("config_toml");
            var hasAuth = root.ContainsKey("auth") || root.ContainsKey("auth_json");
            var auth = root["auth"]?.DeepClone() ?? root["auth_json"]?.DeepClone();
            return new ConfigurationBackup(config, auth, hasConfig, hasAuth);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"无法读取 Codex 配置备份：{_statePath}", ex);
        }
    }

    private static JsonNode? ReadJsonNode(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonNode.Parse(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private void RemoveApiKeyFromAuth()
    {
        if (!File.Exists(_authPath))
            return;

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(_authPath)) as JsonObject;
        }
        catch
        {
            root = null;
        }

        if (root is null)
        {
            RestoreFile(_authPath, "{}" + Environment.NewLine);
            return;
        }

        root.Remove("OPENAI_API_KEY");
        RestoreFile(_authPath, root.ToJsonString(IndentedJson) + Environment.NewLine);
    }

    private static void RestoreFile(string path, string? content)
    {
        if (content is null)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        WriteTextAtomically(path, content);
    }

    private static void WriteTextAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
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
        text = RemoveLegacyCodexSettings(text);

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
            text = EnsureProviderAuthRequirement(text, providerName);

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
        else
        {
            text = EnsureProviderAuthRequirement(text, providerName);
        }

        return text;
    }

    private static string RemoveLegacyCodexSettings(string text)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var inSection = false;

        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                inSection = true;
                continue;
            }

            if (!inSection && Regex.IsMatch(lines[index], @"^\s*(?:preferred_auth_method|personality)\s*=", RegexOptions.CultureInvariant))
            {
                lines.RemoveAt(index);
                index--;
            }
        }

        return string.Join(newline, lines);
    }

    private static string EnsureProviderAuthRequirement(string text, string providerName)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var headerPattern = new Regex(
            @"^\s*\[model_providers\." + Regex.Escape(providerName) + @"\]\s*(?:#.*)?$",
            RegexOptions.CultureInvariant);
        var start = lines.FindIndex(line => headerPattern.IsMatch(line));
        if (start < 0)
            return text;

        var end = lines.Count;
        for (var index = start + 1; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                end = index;
                break;
            }
        }

        var wireIndex = -1;
        for (var index = start + 1; index < end; index++)
        {
            if (Regex.IsMatch(lines[index], @"^\s*wire_api\s*=", RegexOptions.CultureInvariant))
            {
                wireIndex = index;
                break;
            }
        }

        for (var index = end - 1; index > start; index--)
        {
            if (!Regex.IsMatch(lines[index], @"^\s*requires_openai_auth\s*=", RegexOptions.CultureInvariant))
                continue;

            lines.RemoveAt(index);
            end--;
            if (wireIndex > index)
                wireIndex--;
        }

        if (wireIndex < 0)
        {
            var insertAt = end;
            while (insertAt > start + 1 && string.IsNullOrWhiteSpace(lines[insertAt - 1]))
                insertAt--;

            lines.Insert(insertAt++, "wire_api = \"responses\"");
            lines.Insert(insertAt, "requires_openai_auth = true");
        }
        else
        {
            var indentation = Regex.Match(lines[wireIndex], @"^\s*").Value;
            lines.Insert(wireIndex + 1, indentation + "requires_openai_auth = true");
        }

        return string.Join(newline, lines);
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
