using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ApiConfigTool.Services;

public sealed class ClaudeConfigService : IProfileConfigService
{
    private readonly string _directory;
    private readonly Action<string, byte[]?>? _writer;
    public string ConfigPath => Path.Combine(_directory, "settings.json");
    public string SettingsPath => ConfigPath;
    public string StatePath => Path.Combine(_directory, "apiconfig_claude_state.json");

    public ClaudeConfigService(string? claudeDir = null) : this(claudeDir, null) { }
    internal ClaudeConfigService(string? claudeDir, Action<string, byte[]?>? writer)
    {
        var configured = claudeDir ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        _directory = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude") : configured);
        _writer = writer;
    }

    public ApiProfileList LoadProfiles()
    {
        var state = LoadState();
        var live = ReadLive();
        var profiles = ReadProfiles(state);
        var matches = profiles.Where(p => Matches(p, live)).ToList();
        var active = matches.FirstOrDefault(p => p.Id == String(state, "active_profile_id")) ?? matches.FirstOrDefault();
        return new(profiles, active?.Id);
    }

    public ApiProfile SaveProfile(string? id, string? name, string baseUrl, string apiKey, string model, string? configText = null)
    {
        using var guard = LockStore();
        if (configText is not null)
        {
            var parsed = ParseConfig(configText, id, name);
            baseUrl = parsed.BaseUrl; apiKey = parsed.ApiKey; model = parsed.Model;
        }
        var url = ProfileConfiguration.ValidateUrl(baseUrl);
        ProfileConfiguration.ValidateFields(apiKey, model);
        var state = LoadState();
        var profiles = ReadProfiles(state);
        var index = profiles.FindIndex(p => p.Id == id);
        if (id is not null && index < 0) throw new InvalidOperationException("该配置已被删除，请刷新后重试。");
        var profile = new ApiProfile(id ?? Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(name) ? new Uri(url).Host : name.Trim(), url, apiKey.Trim(), model.Trim(), configText);
        if (index < 0) profiles.Add(profile); else profiles[index] = profile;
        SetProfiles(state, profiles);
        WriteState(state);
        return profile;
    }

    public void ActivateProfile(string id)
    {
        using var guard = LockStore();
        var state = LoadState();
        var profile = ReadProfiles(state).SingleOrDefault(p => p.Id == id)
            ?? throw new InvalidOperationException("未找到该配置，请先保存。");
        var before = ReadText();
        var live = Parse(before ?? "{}");
        var output = BuildConfig(profile, live);
        _ = ParseConfig(output, id, profile.Name);
        if (ReadText() != before) throw new IOException("Claude Code 配置已被其他程序修改，请刷新后重试。");
        state["active_profile_id"] = id;
        AtomicFile.Commit(new[]
        {
            (ConfigPath, AtomicFile.Encode(output)),
            (StatePath, AtomicFile.Encode(Serialize(state)))
        }, _writer);
    }

    public void DeleteProfile(string id)
    {
        using var guard = LockStore();
        var state = LoadState();
        var profiles = ReadProfiles(state);
        var profile = profiles.SingleOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("未找到该配置。");
        if (Matches(profile, ReadLive()) || String(state, "active_profile_id") == id)
            throw new InvalidOperationException("请先启用其他配置，再删除当前配置。");
        profiles.Remove(profile);
        SetProfiles(state, profiles);
        WriteState(state);
    }

    public ApiProfile ImportCurrent()
    {
        using var guard = LockStore();
        var live = ReadLive();
        var parsed = ParseConfig(Serialize(live), null, "本机配置");
        var state = LoadState();
        var profiles = ReadProfiles(state);
        var existing = profiles.FirstOrDefault(p => SameFields(p, live));
        var profile = parsed with { Id = existing?.Id ?? StableId(parsed), Name = existing?.Name ?? parsed.Name };
        var index = profiles.FindIndex(p => p.Id == profile.Id);
        if (index < 0) profiles.Add(profile); else profiles[index] = profile;
        SetProfiles(state, profiles);
        state["active_profile_id"] = profile.Id;
        WriteState(state);
        return profile;
    }

    public string PreviewConfig(ApiProfile profile) => BuildConfig(profile, ReadLive());

    public ApiProfile ParseConfig(string text, string? id, string? name)
    {
        var root = Parse(text);
        var env = Env(root);
        var token = String(env, "ANTHROPIC_AUTH_TOKEN");
        var apiKey = String(env, "ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidDataException("ANTHROPIC_AUTH_TOKEN 与 ANTHROPIC_API_KEY 请只保留一种，避免鉴权冲突。");
        var key = token ?? apiKey ?? "";
        var url = ProfileConfiguration.ValidateUrl(String(env, "ANTHROPIC_BASE_URL") ?? "");
        var model = String(root, "model") ?? "";
        ProfileConfiguration.ValidateFields(key, model);
        foreach (var entry in env)
            if (entry.Value is not JsonValue v || !v.TryGetValue<string>(out _))
                throw new InvalidDataException("env 中的环境变量值必须是字符串。");
        if (root.ContainsKey("permissions") && root["permissions"] is not JsonObject)
            throw new InvalidDataException("permissions 必须是 JSON 对象。");
        return new(id ?? "", string.IsNullOrWhiteSpace(name) ? new Uri(url).Host : name.Trim(), url, key.Trim(), model.Trim(), text);
    }

    private static string BuildConfig(ApiProfile profile, JsonObject live)
    {
        var root = profile.ConfigText is null ? (JsonObject)live.DeepClone() : Parse(profile.ConfigText);
        var env = Env(root);
        // A basic profile uses token authentication; an explicit source profile may
        // select ANTHROPIC_API_KEY. Never leave a stale competing key behind.
        var keyName = profile.ConfigText is not null && env.ContainsKey("ANTHROPIC_API_KEY") && !env.ContainsKey("ANTHROPIC_AUTH_TOKEN")
            ? "ANTHROPIC_API_KEY" : "ANTHROPIC_AUTH_TOKEN";
        env.Remove(keyName == "ANTHROPIC_API_KEY" ? "ANTHROPIC_AUTH_TOKEN" : "ANTHROPIC_API_KEY");
        env[keyName] = profile.ApiKey;
        env["ANTHROPIC_BASE_URL"] = profile.BaseUrl;
        if (profile.ConfigText is null && !env.ContainsKey("API_TIMEOUT_MS")) env["API_TIMEOUT_MS"] = "300000";
        root["model"] = profile.Model;
        return Serialize(root);
    }

    private static bool SameFields(ApiProfile p, JsonObject live)
    {
        var env = Env(live);
        return p.BaseUrl.TrimEnd('/') == String(env, "ANTHROPIC_BASE_URL")?.TrimEnd('/') &&
            p.ApiKey == (String(env, "ANTHROPIC_AUTH_TOKEN") ?? String(env, "ANTHROPIC_API_KEY")) && p.Model == String(live, "model");
    }
    private static bool Matches(ApiProfile p, JsonObject live) => SameFields(p, live) &&
        (p.ConfigText is null || JsonNode.DeepEquals(Parse(BuildConfig(p, live)), live));
    private static string StableId(ApiProfile p) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{p.BaseUrl}\n{p.ApiKey}\n{p.Model}")))[..32];

    private JsonObject LoadState()
    {
        if (File.Exists(StatePath))
        {
            var state = ProfileConfiguration.ParseJson(File.ReadAllText(StatePath), "Claude 配置库");
            if (state["version"]?.GetValue<int>() != 1) throw new InvalidDataException("Claude 配置库版本不受支持。");
            _ = ReadProfiles(state);
            return state;
        }
        var root = new JsonObject { ["version"] = 1 };
        var profiles = new List<ApiProfile>();
        var live = ReadLive();
        var env = Env(live);
        if (!string.IsNullOrWhiteSpace(String(env, "ANTHROPIC_BASE_URL")) &&
            !string.IsNullOrWhiteSpace(String(env, "ANTHROPIC_AUTH_TOKEN") ?? String(env, "ANTHROPIC_API_KEY")) &&
            !string.IsNullOrWhiteSpace(String(live, "model")))
        {
            var imported = ParseConfig(Serialize(live), null, "本机配置");
            imported = imported with { Id = StableId(imported) };
            profiles.Add(imported);
            root["active_profile_id"] = imported.Id;
        }
        SetProfiles(root, profiles);
        return root;
    }

    private static List<ApiProfile> ReadProfiles(JsonObject state)
    {
        if (state["profiles"] is not JsonArray rows) throw new InvalidDataException("Claude 配置库缺少 profiles 列表。");
        var result = new List<ApiProfile>();
        foreach (var row in rows)
        {
            if (row is not JsonObject item) throw new InvalidDataException("Claude 配置库存在无效条目。");
            string Required(string key) => String(item, key) is { Length: > 0 } value ? value : throw new InvalidDataException($"配置缺少 {key}。");
            result.Add(new(Required("id"), Required("name"), Required("base_url"), Required("api_key"), Required("model"), String(item, "config_text")));
        }
        if (result.Select(p => p.Id).Distinct().Count() != result.Count) throw new InvalidDataException("配置 ID 重复。");
        return result;
    }

    private static void SetProfiles(JsonObject state, IEnumerable<ApiProfile> profiles) => state["profiles"] = new JsonArray(profiles.Select(p => (JsonNode)new JsonObject
    {
        ["id"] = p.Id, ["name"] = p.Name, ["base_url"] = p.BaseUrl, ["api_key"] = p.ApiKey, ["model"] = p.Model, ["config_text"] = p.ConfigText
    }).ToArray());
    private static string? String(JsonObject root, string key)
    {
        if (root[key] is null) return null;
        if (root[key] is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        throw new InvalidDataException($"{key} 必须是字符串。");
    }
    private static JsonObject Env(JsonObject root)
    {
        if (!root.ContainsKey("env")) root["env"] = new JsonObject();
        return root["env"] as JsonObject ?? throw new InvalidDataException("env 必须是 JSON 对象。");
    }
    private static JsonObject Parse(string text) => ProfileConfiguration.ParseJson(text, "Claude settings.json");
    private static string Serialize(JsonObject root) => root.ToJsonString(ProfileConfiguration.JsonOptions) + Environment.NewLine;
    private string? ReadText() => File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null;
    private JsonObject ReadLive() => Parse(ReadText() ?? "{}");
    private void WriteState(JsonObject state) => AtomicFile.Write(StatePath, AtomicFile.Encode(Serialize(state)));
    private FileStream LockStore()
    {
        Directory.CreateDirectory(_directory);
        try { return new FileStream(Path.Combine(_directory, ".apiconfig-claude.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("另一个配置工具正在写入 Claude 配置，请稍后重试。"); }
    }

    public void Apply(string baseUrl, string apiKey, string model)
    {
        var profile = SaveProfile(null, null, baseUrl, apiKey, model);
        ActivateProfile(profile.Id);
    }
}
