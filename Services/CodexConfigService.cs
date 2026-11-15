using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tomlyn;
using Tomlyn.Model;

namespace ApiConfigTool.Services;

public sealed class CodexConfigService : IProfileConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory;
    private readonly Action<string, byte[]?>? _writer;
    public string ConfigPath => Path.Combine(_directory, "config.toml");
    public string AuthPath => Path.Combine(_directory, "auth.json");
    public string StatePath => Path.Combine(_directory, "apiconfig_state.json");

    public CodexConfigService(string? codexDir = null) : this(codexDir, null) { }

    internal CodexConfigService(string? codexDir, Action<string, byte[]?>? writer)
    {
        var configured = codexDir ?? Environment.GetEnvironmentVariable("CODEX_HOME");
        _directory = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : configured);
        _writer = writer;
    }

    public ApiProfileList LoadProfiles()
    {
        var state = LoadState();
        var live = ReadLive();
        var profiles = ReadProfiles(state);
        var mode = DetectMode(live.Config);
        // Never trust the cached active id after Codex or another tool edits live files.
        var actual = ReadSettings(live.Config, live.Auth);
        var matching = profiles.Where(p => Matches(p, actual) && SourceMatches(p, live)).ToList();
        var active = mode == CodexConfigurationMode.ApiConfiguration
            ? (matching.FirstOrDefault(p => p.Id == GetString(state, "active_profile_id")) ?? matching.FirstOrDefault())?.Id : null;
        return new(profiles, active, mode);
    }

    public ApiProfile SaveProfile(string? id, string? name, string baseUrl, string apiKey, string model, string? configText = null)
    {
        using var guard = LockStore();
        var state = LoadState();
        var profiles = ReadProfiles(state);
        if (configText is not null)
        {
            var parsed = ParseConfig(configText, id, name);
            baseUrl = parsed.BaseUrl; apiKey = parsed.ApiKey; model = parsed.Model;
        }
        var normalized = configText is null ? ValidateUrl(baseUrl) : ProfileConfiguration.ValidateUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("请填写 API Key。");
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("请填写模型名称。");
        if (apiKey.Any(char.IsControl) || model.Any(char.IsControl))
            throw new ArgumentException("API Key 和模型名称不能包含换行或控制字符。");
        if (id is not null && profiles.All(p => p.Id != id))
            throw new InvalidOperationException("该配置已被删除，请刷新后重试。");
        var profile = new ApiProfile(id ?? Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(name) ? new Uri(normalized).Host : name.Trim(),
            normalized, apiKey.Trim(), model.Trim(), configText);
        var index = profiles.FindIndex(p => p.Id == profile.Id);
        if (index < 0) profiles.Add(profile); else profiles[index] = profile;
        SetProfiles(state, profiles);
        WriteState(state); // Saving a draft does not change live files, even for the current row.
        return profile;
    }

    public void DeleteProfile(string id)
    {
        using var guard = LockStore();
        var state = LoadState();
        var profiles = ReadProfiles(state);
        var profile = profiles.SingleOrDefault(p => p.Id == id)
            ?? throw new InvalidOperationException("未找到该配置。");
        profiles.Remove(profile);
        SetProfiles(state, profiles);
        if (GetString(state, "active_profile_id") == id) state.Remove("active_profile_id");
        WriteState(state);
    }

    public void ActivateProfile(string id)
    {
        using var guard = LockStore();
        var state = LoadState();
        var profile = ReadProfiles(state).SingleOrDefault(p => p.Id == id)
            ?? throw new InvalidOperationException("未找到该配置，请先保存。");
        ValidateUrl(profile.BaseUrl);
        if (string.IsNullOrWhiteSpace(profile.ApiKey) || string.IsNullOrWhiteSpace(profile.Model))
            throw new InvalidDataException("配置缺少 API Key 或模型，请编辑后重试。");
        if (profile.ConfigText is not null) _ = ParseConfig(profile.ConfigText, id, profile.Name);

        var live = ReadLive(); // Parse everything before the first write.
        if (DetectMode(live.Config) == CodexConfigurationMode.GptAccount &&
            (live.ConfigText is not null || live.AuthText is not null || state["gpt"] is null))
            state["gpt"] = Backup(live.ConfigText, live.AuthText);

        var auth = OfficialAuth(live.Auth);
        var configText = BuildConfig(profile, live);
        state["active_profile_id"] = id;
        state["current_mode"] = "api";
        Commit(live, configText, AuthText(live, auth), state);
    }

    public string PreviewConfig(ApiProfile profile) => BuildConfig(profile, ReadLive());

    public ApiProfile ParseConfig(string text, string? id, string? name)
    {
        var table = ParseToml(text);
        ValidateEffectiveProfile(table);
        if (TableString(table, "model_provider") != "custom")
            throw new InvalidDataException("API 配置的 model_provider 必须为 custom。");
        var providers = GetOrCreateTable(table, "model_providers");
        var provider = GetOrCreateTable(providers, "custom");
        var url = ProfileConfiguration.ValidateUrl(TableString(provider, "base_url") ?? "");
        var key = TableString(provider, "experimental_bearer_token") ?? "";
        var model = TableString(table, "model") ?? "";
        ProfileConfiguration.ValidateFields(key, model);
        var authHeaders = new[] { "http_headers", "env_http_headers" }.Any(field =>
            provider.TryGetValue(field, out var headers) && headers is TomlTable tableHeaders &&
            tableHeaders.Keys.Any(key => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)));
        if (provider.ContainsKey("env_key") || provider.ContainsKey("auth") || provider.ContainsKey("aws") || authHeaders)
            throw new InvalidDataException("此 API 配置使用 experimental_bearer_token，请移除 provider 中其他鉴权方式，避免密钥被覆盖。");
        if (TableString(provider, "wire_api") is not ("responses" or "chat"))
            throw new InvalidDataException("wire_api 必须为 responses 或 chat。");
        foreach (var legacy in new[] { "openai_base_url", "base_url", "experimental_bearer_token", "preferred_auth_method" })
            if (table.ContainsKey(legacy)) throw new InvalidDataException($"请移除顶层旧路由字段 {legacy}，在 model_providers.custom 中配置。");
        return new(id ?? "", string.IsNullOrWhiteSpace(name) ? new Uri(url).Host : name.Trim(), url, key.Trim(), model.Trim(), text);
    }

    private static string BuildConfig(ApiProfile profile, Live live)
    {
        var config = ParseToml(profile.ConfigText ?? live.Config);
        // CC Switch's direct projection (live/project/codex.rs): one stable custom
        // provider, explicit bearer credential, official login retained separately.
        var auth = OfficialAuth(live.Auth);
        config["model_provider"] = "custom";
        config["model"] = profile.Model;
        if (profile.ConfigText is null && config.ContainsKey("review_model")) config["review_model"] = profile.Model;
        config.Remove("preferred_auth_method");
        config.Remove("openai_base_url");
        config.Remove("base_url");
        config.Remove("experimental_bearer_token");
        config.Remove("wire_api");
        var providers = GetOrCreateTable(config, "model_providers");
        // Reserved built-in ids are not legal custom tables in current Codex.
        // Preserve their settings under an unused id and repair profile references.
        MigrateReservedProviders(config, providers);
        var provider = profile.ConfigText is null ? new TomlTable() : GetOrCreateTable(providers, "custom");
        provider["name"] = profile.Name;
        provider["base_url"] = profile.BaseUrl;
        if (!provider.ContainsKey("wire_api")) provider["wire_api"] = "responses";
        provider["experimental_bearer_token"] = profile.ApiKey;
        provider["requires_openai_auth"] = HasLogin(auth);
        providers["custom"] = provider;
        return Toml.FromModel(config);
    }

    private static bool SourceMatches(ApiProfile profile, Live live) => profile.ConfigText is null ||
        JsonNode.DeepEquals(JsonSerializer.SerializeToNode(ParseToml(BuildConfig(profile, live))),
            JsonSerializer.SerializeToNode(ParseToml(live.Config)));

    public void SwitchToGptAccount()
    {
        using var guard = LockStore();
        var state = LoadState();
        var live = ReadLive();
        if (DetectMode(live.Config) == CodexConfigurationMode.GptAccount) return;
        var config = ParseToml(live.Config);
        var backup = state["gpt"] as JsonObject;
        var original = ParseToml(BackupConfig(backup));
        config.Remove("model_provider");
        config.Remove("openai_base_url");
        config.Remove("base_url");
        config.Remove("experimental_bearer_token");
        config.Remove("wire_api");
        config.Remove("preferred_auth_method");
        foreach (var key in new[] { "model", "review_model" })
        {
            if (original.TryGetValue(key, out var value)) config[key] = value;
            else config.Remove(key);
        }
        if (config.TryGetValue("model_providers", out var valueProviders) && valueProviders is TomlTable providers)
        {
            providers.Remove("custom");
            MigrateReservedProviders(config, providers);
        }
        // Prefer the live, possibly refreshed, login to any stale snapshot.
        var auth = OfficialAuth(live.Auth);
        if (!HasLogin(auth) && backup is not null)
        {
            var saved = BackupAuth(backup);
            if (HasLogin(saved)) auth = OfficialAuth(saved);
        }
        state.Remove("active_profile_id");
        state["current_mode"] = "gpt";
        Commit(live, Toml.FromModel(config), AuthText(live, auth), state);
    }

    public ApiProfile ImportCurrent()
    {
        using var guard = LockStore();
        var state = LoadState();
        var live = ReadLive();
        var settings = ReadSettings(live.Config, live.Auth);
        if (DetectMode(live.Config) != CodexConfigurationMode.ApiConfiguration || !Complete(settings))
            throw new InvalidOperationException("当前没有可导入的 API 配置。使用环境变量认证时，请在表单中填写 API Key。");
        var profiles = ReadProfiles(state);
        var profile = profiles.FirstOrDefault(p => Matches(p, settings));
        if (profile is null)
        {
            profile = FromSettings("本机配置", settings);
            profiles.Add(profile);
        }
        // Explicit import captures the complete current configuration.
        var importedConfig = ParseToml(live.Config);
        var providers = GetOrCreateTable(importedConfig, "model_providers");
        var selected = TableString(EffectiveConfig(ParseToml(live.Config)), "model_provider") ?? "openai";
        if (providers.TryGetValue(selected, out var provider) && provider is TomlTable)
            providers["custom"] = provider;
        var importedText = BuildConfig(profile with { ConfigText = Toml.FromModel(importedConfig) }, live);
        _ = ParseConfig(importedText, profile.Id, profile.Name);
        profile = profile with { ConfigText = importedText };
        profiles[profiles.FindIndex(p => p.Id == profile.Id)] = profile;
        SetProfiles(state, profiles);
        state["active_profile_id"] = profile.Id;
        WriteState(state);
        return profile;
    }

    private JsonObject LoadState()
    {
        var state = ReadJson(StatePath) ?? new JsonObject();
        if (state.ContainsKey("version"))
        {
            if (state["version"]?.GetValue<int>() != 2)
                throw new InvalidDataException("配置库版本不受支持，请使用更新版本的工具。");
            _ = ReadProfiles(state);
            return state;
        }
        var profiles = new List<ApiProfile>();
        var live = ReadLive();
        var current = ReadSettings(live.Config, live.Auth);
        if (DetectMode(live.Config) == CodexConfigurationMode.ApiConfiguration && Complete(current))
        {
            var imported = FromSettings("本机配置", current);
            profiles.Add(imported);
            state["active_profile_id"] = imported.Id;
        }
        if (state["api"] is JsonObject legacy)
        {
            var settings = ReadSettings(BackupConfig(legacy), BackupAuth(legacy));
            if (Complete(settings) && !profiles.Any(p => Matches(p, settings)))
                profiles.Add(FromSettings("原 API 配置", settings));
        }
        state["version"] = 2;
        SetProfiles(state, profiles);
        // Read-only startup leaves an empty installation untouched. The migration
        // becomes durable on the next explicit save/import/switch operation.
        return state;
    }

    private static List<ApiProfile> ReadProfiles(JsonObject state)
    {
        if (state["profiles"] is not JsonArray rows) throw new InvalidDataException("配置库缺少 profiles 列表。");
        var result = new List<ApiProfile>();
        foreach (var row in rows)
        {
            if (row is not JsonObject item) throw new InvalidDataException("配置库中存在无效条目。");
            string Required(string key) => GetString(item, key) is { Length: > 0 } text ? text
                : throw new InvalidDataException($"配置库条目缺少 {key}。");
            result.Add(new(Required("id"), Required("name"), Required("base_url"), Required("api_key"), Required("model"), GetString(item, "config_text")));
        }
        if (result.Select(p => p.Id).Distinct().Count() != result.Count)
            throw new InvalidDataException("配置库中存在重复 ID。");
        return result;
    }

    private static void SetProfiles(JsonObject state, IEnumerable<ApiProfile> profiles) =>
        state["profiles"] = new JsonArray(profiles.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id, ["name"] = p.Name, ["base_url"] = p.BaseUrl,
            ["api_key"] = p.ApiKey, ["model"] = p.Model, ["config_text"] = p.ConfigText
        }).ToArray());

    private sealed record Live(string? ConfigText, string? AuthText, JsonObject? Auth)
    {
        public string Config => ConfigText ?? string.Empty;
    }

    private Live ReadLive()
    {
        var config = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null;
        var auth = File.Exists(AuthPath) ? File.ReadAllText(AuthPath) : null;
        _ = ParseToml(config);
        return new(config, auth, auth is null ? null : ParseJson(auth, AuthPath));
    }

    private static TomlTable ParseToml(string? text)
    {
        try { return Toml.ToModel(text ?? string.Empty); }
        catch { throw new InvalidDataException("Codex config.toml 格式无效，未覆盖原文件。请先修复 TOML 语法。"); }
    }

    private static JsonObject? ReadJson(string path) => File.Exists(path) ? ParseJson(File.ReadAllText(path), path) : null;
    private static JsonObject ParseJson(string text, string path)
    {
        try { return JsonNode.Parse(text) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidDataException($"JSON 格式无效，未覆盖原文件：{path}"); }
    }
    private static string? GetString(JsonObject? root, string key) => root?[key]?.GetValue<string>();
    private static string? TableString(TomlTable root, string key) => root.TryGetValue(key, out var v) ? v as string : null;
    private static TomlTable GetOrCreateTable(TomlTable root, string key)
    {
        if (!root.TryGetValue(key, out var value)) root[key] = value = new TomlTable();
        return value as TomlTable ?? throw new InvalidDataException($"Codex 的 {key} 必须是 TOML 表。");
    }

    private static CodexConfigurationMode DetectMode(string text)
    {
        var table = EffectiveConfig(ParseToml(text));
        var provider = TableString(table, "model_provider");
        return (!string.IsNullOrWhiteSpace(provider) && provider != "openai") ||
            !string.IsNullOrWhiteSpace(TableString(table, "openai_base_url")) ||
            !string.IsNullOrWhiteSpace(TableString(table, "base_url")) ||
            (table.TryGetValue("model_providers", out var p) && p is TomlTable providers &&
             providers.TryGetValue("openai", out var o) && o is TomlTable official && TableString(official, "base_url") is not null)
            ? CodexConfigurationMode.ApiConfiguration : CodexConfigurationMode.GptAccount;
    }

    private static (string? BaseUrl, string? Key, string? Model) ReadSettings(string? text, JsonObject? auth)
    {
        var table = EffectiveConfig(ParseToml(text));
        var providerId = TableString(table, "model_provider") ?? "openai";
        TomlTable? provider = null;
        if (table.TryGetValue("model_providers", out var value) && value is TomlTable providers &&
            providers.TryGetValue(providerId, out var selected)) provider = selected as TomlTable;
        return (provider is null ? TableString(table, "openai_base_url") ?? TableString(table, "base_url") : TableString(provider, "base_url"),
            (provider is null ? null : TableString(provider, "experimental_bearer_token")) ??
            TableString(table, "experimental_bearer_token") ?? GetString(auth, "OPENAI_API_KEY"), TableString(table, "model"));
    }

    private static bool Complete((string? BaseUrl, string? Key, string? Model) s) =>
        !string.IsNullOrWhiteSpace(s.BaseUrl) && !string.IsNullOrWhiteSpace(s.Key) && !string.IsNullOrWhiteSpace(s.Model);
    private static bool Matches(ApiProfile p, (string? BaseUrl, string? Key, string? Model) s) =>
        p.BaseUrl.TrimEnd('/') == s.BaseUrl?.TrimEnd('/') && p.ApiKey == s.Key && p.Model == s.Model;
    private static ApiProfile FromSettings(string name, (string? BaseUrl, string? Key, string? Model) s) =>
        new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{s.BaseUrl}\n{s.Key}\n{s.Model}")))[..32], name, s.BaseUrl!, s.Key!, s.Model!);

    private static TomlTable? SelectedApiProfile(TomlTable config)
    {
        var name = TableString(config, "profile");
        return name is not null && config.TryGetValue("profiles", out var entries) && entries is TomlTable profiles &&
            profiles.TryGetValue(name, out var value) ? value as TomlTable : null;
    }

    private static TomlTable EffectiveConfig(TomlTable config)
    {
        if (SelectedApiProfile(config) is { } profile)
            foreach (var key in new[] { "model_provider", "model", "openai_base_url", "base_url", "experimental_bearer_token" })
                if (profile.TryGetValue(key, out var value)) config[key] = value;
        return config;
    }

    private static void ValidateEffectiveProfile(TomlTable config)
    {
        if (SelectedApiProfile(config) is not { } profile) return;
        foreach (var key in new[] { "model_provider", "model", "openai_base_url", "base_url", "experimental_bearer_token" })
        {
            var value = TableString(profile, key);
            var expected = TableString(config, key) ?? (key == "model_provider" ? "openai" : null);
            if (!string.IsNullOrEmpty(value) && value != expected)
                throw new InvalidOperationException($"Codex 默认 profile「{TableString(config, "profile")}」覆盖了 {key}，切换未写入。请先移除该覆盖设置或取消默认 profile。");
        }
    }

    private static string ValidateUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("请填写 Base URL。");
        var url = ModelsApiService.EnsureScheme(input).TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != "https" && parsed.Scheme != "http") || string.IsNullOrEmpty(parsed.Host) ||
            parsed.UserInfo.Length > 0 || parsed.Query.Length > 0 || parsed.Fragment.Length > 0 || input.Any(char.IsWhiteSpace))
            throw new ArgumentException("Base URL 必须是有效的 HTTP(S) 地址，不能包含用户信息、查询参数或片段。");
        // Preserve provider-specific paths such as /api/coding/v3; only a bare origin needs /v1.
        if (url.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) url = url[..^7].TrimEnd('/');
        if (new Uri(url).AbsolutePath == "/") url += "/v1";
        return url;
    }

    private static bool HasLogin(JsonObject? auth) => auth?["tokens"] is JsonObject tokens &&
        new[] { "access_token", "refresh_token", "id_token" }.Any(k => !string.IsNullOrWhiteSpace(GetString(tokens, k)));
    private static JsonObject? OfficialAuth(JsonObject? auth)
    {
        var result = auth?.DeepClone() as JsonObject;
        result?.Remove("OPENAI_API_KEY");
        if (GetString(result, "auth_mode") == "apikey") result!.Remove("auth_mode");
        return result;
    }
    private static string? AuthText(Live live, JsonObject? auth) => JsonNode.DeepEquals(live.Auth, auth)
        ? live.AuthText : auth?.ToJsonString(JsonOptions) + (auth is null ? null : Environment.NewLine);
    private static JsonObject Backup(string? config, string? auth) => new()
    {
        ["config"] = config, ["auth"] = auth is null ? null : JsonNode.Parse(auth)
    };
    private static string? BackupConfig(JsonObject? backup) => GetString(backup, "config") ?? GetString(backup, "config_toml");
    private static JsonObject? BackupAuth(JsonObject? backup) => (backup?["auth"] ?? backup?["auth_json"])?.DeepClone() as JsonObject;

    private static void MigrateReservedProviders(TomlTable config, TomlTable providers)
    {
        foreach (var reserved in new[] { "openai", "ollama", "lmstudio" })
        {
            if (!providers.TryGetValue(reserved, out var table)) continue;
            var replacement = reserved + "_legacy";
            for (var i = 2; providers.ContainsKey(replacement); i++) replacement = reserved + "_legacy_" + i;
            providers[replacement] = table;
            providers.Remove(reserved);
            if (config.TryGetValue("profiles", out var values) && values is TomlTable profiles)
                foreach (var profile in profiles.Values.OfType<TomlTable>())
                    if (TableString(profile, "model_provider") == reserved) profile["model_provider"] = replacement;
        }
    }

    private void Commit(Live live, string config, string? auth, JsonObject state)
    {
        ValidateEffectiveProfile(ParseToml(config));
        if (auth is not null) _ = ParseJson(auth, AuthPath);
        // Abort when a concurrent client changed live files during preparation.
        if ((File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null) != live.ConfigText ||
            (File.Exists(AuthPath) ? File.ReadAllText(AuthPath) : null) != live.AuthText)
            throw new IOException("Codex 配置刚被其他程序修改，请刷新后重试。");
        AtomicFile.Commit(new[]
        {
            (AuthPath, AtomicFile.Encode(auth)),
            (ConfigPath, AtomicFile.Encode(config)),
            (StatePath, AtomicFile.Encode(state.ToJsonString(JsonOptions) + Environment.NewLine))
        }, _writer);
    }
    private void WriteState(JsonObject state) => AtomicFile.Write(StatePath, AtomicFile.Encode(state.ToJsonString(JsonOptions) + Environment.NewLine));
    private FileStream LockStore()
    {
        Directory.CreateDirectory(_directory);
        try { return new FileStream(Path.Combine(_directory, ".apiconfig.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("另一个配置工具正在写入，请稍后重试。"); }
    }
}
