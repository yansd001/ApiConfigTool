using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ApiConfigTool;
using ApiConfigTool.Services;
using Tomlyn;
using Tomlyn.Model;

internal static partial class Program
{
    private static void ExtendedChecks()
    {
        var tests = new (string Name, Action<TestDirectory> Run)[]
        {
            ("Claude startup does not create files", d => { var c = new ClaudeConfigService(d.Path); Check(c.LoadProfiles().Profiles.Count == 0); Check(!File.Exists(c.StatePath)); Check(!File.Exists(c.ConfigPath)); }),
            ("Claude save A/B then switch persists across restart", d => { var c = new ClaudeConfigService(d.Path); var a = c.SaveProfile(null, "A", "https://a.example/api", "a-key", "a-model"); var b = c.SaveProfile(null, "B", "https://b.example", "b-key", "b-model"); Check(!File.Exists(c.ConfigPath)); c.ActivateProfile(a.Id); c.ActivateProfile(b.Id); c = new ClaudeConfigService(d.Path); Check(c.LoadProfiles().Profiles.Count == 2 && c.LoadProfiles().ActiveId == b.Id); var json = ReadClaude(c); Check(json["env"]!["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>() == "b-key"); Check(json["model"]!.GetValue<string>() == "b-model"); }),
            ("Claude drafts do not modify active file", d => { var c = new ClaudeConfigService(d.Path); var a = c.SaveProfile(null, "A", "https://a.example", "key", "m"); c.ActivateProfile(a.Id); var before = File.ReadAllBytes(c.ConfigPath); c.SaveProfile(a.Id, "A", "https://new.example", "new-key", "new-model"); Check(before.SequenceEqual(File.ReadAllBytes(c.ConfigPath))); Check(c.LoadProfiles().ActiveId is null); }),
            ("Claude imports an existing API-key config on first launch", d => { var c = new ClaudeConfigService(d.Path); File.WriteAllText(c.ConfigPath, "{\"env\":{\"ANTHROPIC_API_KEY\":\"key\",\"ANTHROPIC_BASE_URL\":\"https://existing.example/api\"},\"model\":\"m\",\"permissions\":{\"allow\":[\"Read\"]}}"); var list = c.LoadProfiles(); Check(list.Profiles.Count == 1 && list.ActiveId == list.Profiles[0].Id); Check(list.Profiles[0].Id == c.LoadProfiles().Profiles[0].Id); var imported = c.ImportCurrent(); c.ActivateProfile(imported.Id); Check(ReadClaude(c)["env"]!["ANTHROPIC_API_KEY"]!.GetValue<string>() == "key"); Check(ReadClaude(c)["permissions"] is JsonObject); }),
            ("Claude removes stale alternate authentication", d => { var c = new ClaudeConfigService(d.Path); File.WriteAllText(c.ConfigPath, "{\"env\":{\"ANTHROPIC_API_KEY\":\"stale\",\"CUSTOM\":\"keep\"}}"); var a = c.SaveProfile(null, "A", "https://a.example", "new", "m"); c.ActivateProfile(a.Id); var env = (JsonObject)ReadClaude(c)["env"]!; Check(!env.ContainsKey("ANTHROPIC_API_KEY")); Check(env["CUSTOM"]!.GetValue<string>() == "keep"); }),
            ("Claude protects active profile and deletes inactive", d => { var c = new ClaudeConfigService(d.Path); var a = c.SaveProfile(null, "A", "https://a.example", "key", "m"); var b = c.SaveProfile(null, "B", "https://b.example", "key", "m"); c.ActivateProfile(a.Id); Throws(() => c.DeleteProfile(a.Id)); c.DeleteProfile(b.Id); Check(c.LoadProfiles().Profiles.Count == 1); }),
            ("Claude identical fields track selected ID", d => { var c = new ClaudeConfigService(d.Path); c.SaveProfile(null, "A", "https://a.example", "key", "m"); var b = c.SaveProfile(null, "B", "https://a.example", "key", "m"); c.ActivateProfile(b.Id); Check(c.LoadProfiles().ActiveId == b.Id); }),
            ("Claude source adds, edits and removes arbitrary settings", d => { var c = new ClaudeConfigService(d.Path); File.WriteAllText(c.ConfigPath, "{\"permissions\":{\"allow\":[\"Read\"]},\"env\":{\"REMOVE_ME\":\"old\"}}"); var preview = c.PreviewConfig(new("", "A", "https://a.example", "key", "m")); var json = JsonNode.Parse(preview)!.AsObject(); json["permissions"]!["allow"] = new JsonArray("Read", "Edit"); json["custom"] = new JsonObject { ["array"] = new JsonArray(1, 2, 3), ["flag"] = true }; ((JsonObject)json["env"]!).Remove("REMOVE_ME"); var p = c.SaveProfile(null, "A", "ignored", "ignored", "ignored", json.ToJsonString()); Check(!ReadClaude(c).ContainsKey("custom")); c.ActivateProfile(p.Id); Check(JsonNode.DeepEquals(json, ReadClaude(c))); Check(c.LoadProfiles().ActiveId == p.Id); }),
            ("Claude custom source survives form edits", d => { var c = new ClaudeConfigService(d.Path); var raw = "{\"env\":{\"ANTHROPIC_AUTH_TOKEN\":\"key\",\"ANTHROPIC_BASE_URL\":\"https://a.example\",\"CUSTOM\":\"keep\"},\"model\":\"m\",\"permissions\":{\"allow\":[\"Read\"]}}"; var p = c.SaveProfile(null, "A", "", "", "", raw); var updated = c.PreviewConfig(p with { Model = "new-model", ApiKey = "new-key" }); p = c.SaveProfile(p.Id, "A", "", "", "", updated); c.ActivateProfile(p.Id); Check(ReadClaude(c)["model"]!.GetValue<string>() == "new-model"); Check(ReadClaude(c)["env"]!["CUSTOM"]!.GetValue<string>() == "keep"); }),
            ("Claude A/B source snapshots restore independent settings", d => { var c = new ClaudeConfigService(d.Path); var raw = c.PreviewConfig(new("", "A", "https://a.example", "key", "m")); var aJson = JsonNode.Parse(raw)!.AsObject(); aJson["custom"] = "A"; var a = c.SaveProfile(null, "A", "", "", "", aJson.ToJsonString()); var bJson = JsonNode.Parse(raw)!.AsObject(); bJson["custom"] = "B"; var b = c.SaveProfile(null, "B", "", "", "", bJson.ToJsonString()); c.ActivateProfile(a.Id); c.ActivateProfile(b.Id); Check(ReadClaude(c)["custom"]!.GetValue<string>() == "B"); c.ActivateProfile(a.Id); Check(ReadClaude(c)["custom"]!.GetValue<string>() == "A"); }),
            ("Claude malformed/duplicate/invalid source leaves store unchanged", d => { var c = new ClaudeConfigService(d.Path); c.SaveProfile(null, "A", "https://a.example", "key", "m"); var before = File.ReadAllBytes(c.StatePath); foreach (var raw in new[] { "{broken", "[]", "{\"env\":7}", "{\"env\":{},\"env\":{}}", "{\"model\":7}" }) Throws(() => c.SaveProfile(null, "A", "", "", "", raw)); Check(before.SequenceEqual(File.ReadAllBytes(c.StatePath))); Check(!File.Exists(c.ConfigPath)); }),
            ("Claude rejects competing raw credentials", d => { var c = new ClaudeConfigService(d.Path); var raw = c.PreviewConfig(new("", "A", "https://a.example", "key", "m")); var json = JsonNode.Parse(raw)!; json["env"]!["ANTHROPIC_API_KEY"] = "other"; Throws(() => c.ParseConfig(json.ToJsonString(), null, "A")); }),
            ("Claude corrupt live and corrupt store never overwritten", d => { var c = new ClaudeConfigService(d.Path); var a = c.SaveProfile(null, "A", "https://a.example", "key", "m"); File.WriteAllText(c.ConfigPath, "{broken"); var state = File.ReadAllBytes(c.StatePath); Throws(() => c.ActivateProfile(a.Id)); Check(File.ReadAllText(c.ConfigPath) == "{broken"); Check(state.SequenceEqual(File.ReadAllBytes(c.StatePath))); File.WriteAllText(c.StatePath, "{broken-state"); Throws(() => c.SaveProfile(null, "B", "https://b.example", "key", "m")); Check(File.ReadAllText(c.StatePath) == "{broken-state"); }),
            ("Claude later write failure restores previous settings", d => { var c = new ClaudeConfigService(d.Path); File.WriteAllText(c.ConfigPath, "{\"custom\":\"original\"}"); var a = c.SaveProfile(null, "A", "https://a.example", "key", "m"); var before = File.ReadAllBytes(c.ConfigPath); var stateBefore = File.ReadAllBytes(c.StatePath); var fail = new ClaudeConfigService(d.Path, (path, bytes) => { if (path == c.StatePath) throw new IOException("injected"); AtomicFile.Write(path, bytes); }); Throws(() => fail.ActivateProfile(a.Id)); Check(before.SequenceEqual(File.ReadAllBytes(c.ConfigPath))); Check(stateBefore.SequenceEqual(File.ReadAllBytes(c.StatePath))); }),
            ("Claude rollback removes a new settings file", d => { var c = new ClaudeConfigService(d.Path); var a = c.SaveProfile(null, "A", "https://a.example", "key", "m"); var fail = new ClaudeConfigService(d.Path, (path, bytes) => { if (path == c.StatePath) throw new IOException("injected"); AtomicFile.Write(path, bytes); }); Throws(() => fail.ActivateProfile(a.Id)); Check(!File.Exists(c.ConfigPath)); }),
            ("Claude external source changes clear enabled status", d => { var c = new ClaudeConfigService(d.Path); var raw = c.PreviewConfig(new("", "A", "https://a.example", "key", "m")); var a = c.SaveProfile(null, "A", "", "", "", raw); c.ActivateProfile(a.Id); var json = ReadClaude(c); json["new_setting"] = 1; File.WriteAllText(c.ConfigPath, json.ToJsonString()); Check(c.LoadProfiles().ActiveId is null); }),
            ("Claude custom directory and writer lock are honored", d => { var old = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"); try { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", d.Path); var c = new ClaudeConfigService(); Check(c.ConfigPath == Path.Combine(d.Path, "settings.json")); using var held = new FileStream(Path.Combine(d.Path, ".apiconfig-claude.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); Throws(() => c.SaveProfile(null, "A", "https://a.example", "key", "m")); } finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", old); } }),
            ("Codex preview is read-only", d => { var a = d.Save("A"); var before = d.Bytes(); var preview = d.Service.PreviewConfig(a); Check(preview.Contains("experimental_bearer_token")); d.Unchanged(before); }),
            ("Codex explicit reimport takes live extras over stale saved source", d => { var p = d.Save("A"); p = d.Service.SaveProfile(p.Id, "A", "", "", "", d.Service.PreviewConfig(p)); d.Service.ActivateProfile(p.Id); d.WriteConfig("model_reasoning_effort='low'\n" + File.ReadAllText(d.Service.ConfigPath)); var imported = d.Service.ImportCurrent(); Check(imported.ConfigText!.Contains("model_reasoning_effort")); Check(d.Service.LoadProfiles().ActiveId == p.Id); }),
            ("Codex manual TOML settings survive save and activation", d => { var p = d.Save("A"); var preview = d.Service.PreviewConfig(p); var table = Toml.ToModel(preview); table["model_reasoning_effort"] = "low"; table["review_model"] = "different-review"; table["features"] = new TomlTable { ["custom_flag"] = true }; ((TomlTable)((TomlTable)table["model_providers"])["custom"])["request_max_retries"] = 7L; var raw = "# hand edited\n" + Toml.FromModel(table); p = d.Service.SaveProfile(p.Id, "A", "", "", "", raw); Check(!File.Exists(d.Service.ConfigPath)); d.Service.ActivateProfile(p.Id); Check((string)d.Config()["review_model"] == "different-review"); Check((long)d.Provider()["request_max_retries"] == 7L); Check(File.ReadAllText(d.Service.ConfigPath).Contains("hand edited")); Check(d.Service.LoadProfiles().ActiveId == p.Id); }),
            ("Codex source credentials sync to card and form edits retain extras", d => { var p = d.Save("A"); var raw = "model_reasoning_effort = 'low'\n" + d.Service.PreviewConfig(p).Replace("key-A", "source-key").Replace("model-A", "source-model"); p = d.Service.SaveProfile(p.Id, "A", "stale", "stale", "stale", raw); Check(p.ApiKey == "source-key" && p.Model == "source-model"); var updated = d.Service.PreviewConfig(p with { Model = "form-model" }); p = d.Service.SaveProfile(p.Id, "A", "", "", "", updated); d.Service.ActivateProfile(p.Id); Check((string)d.Config()["model_reasoning_effort"] == "low"); Check((string)d.Config()["model"] == "form-model"); }),
            ("Codex malformed or incompatible source never updates store", d => { var p = d.Save("A"); var before = d.Bytes(); foreach (var raw in new[] { "[broken", "model='missing-provider'", d.Service.PreviewConfig(p).Replace("model_provider = \"custom\"", "model_provider = \"openai\"") }) Throws(() => d.Service.SaveProfile(p.Id, "A", "", "", "", raw)); d.Unchanged(before); }),
            ("Codex source cannot override explicit key with env auth", d => { var p = d.Save("A"); var raw = d.Service.PreviewConfig(p) + "env_key = 'OTHER_KEY'\n"; Throws(() => d.Service.ParseConfig(raw, p.Id, p.Name)); }),
            ("Codex source permits custom headers without competing authorization", d => { var p = d.Save("A"); var raw = d.Service.PreviewConfig(p) + "[model_providers.custom.http_headers]\nX-Custom = 'keep'\n"; p = d.Service.SaveProfile(p.Id, "A", "", "", "", raw); d.Service.ActivateProfile(p.Id); Check((string)((TomlTable)d.Provider()["http_headers"])["X-Custom"] == "keep"); Throws(() => d.Service.ParseConfig(raw + "authorization='conflict'\n", p.Id, p.Name)); }),
            ("Codex raw source detects external extra-setting changes", d => { var p = d.Save("A"); p = d.Service.SaveProfile(p.Id, "A", "", "", "", d.Service.PreviewConfig(p)); d.Service.ActivateProfile(p.Id); d.WriteConfig("model_reasoning_effort='low'\n" + File.ReadAllText(d.Service.ConfigPath)); Check(d.Service.LoadProfiles().ActiveId is null); }),
            ("Codex source login requirement tracks refreshed current auth", d => { var p = d.Save("A"); p = d.Service.SaveProfile(p.Id, "A", "", "", "", d.Service.PreviewConfig(p)); d.WriteAuth("{\"tokens\":{\"refresh_token\":\"new-account\"}}"); var auth = File.ReadAllBytes(d.Service.AuthPath); d.Service.ActivateProfile(p.Id); Check((bool)d.Provider()["requires_openai_auth"]); Check(auth.SequenceEqual(File.ReadAllBytes(d.Service.AuthPath))); })
        };
        foreach (var test in tests)
        {
            using var d = new TestDirectory();
            test.Run(d);
            _passed++;
            Console.WriteLine("PASS " + test.Name);
        }
    }

    private static JsonObject ReadClaude(ClaudeConfigService c) => JsonNode.Parse(File.ReadAllText(c.ConfigPath))!.AsObject();
    private static void Mode(MainWindow window, string name) { ((RadioButton)window.FindName(name)).IsChecked = true; Pump(); }

    private static void ExtendedUi(MainWindow window, TestDirectory d, ClaudeConfigService claude)
    {
        var source = (TextBox)window.FindName("SourceBox");
        var model = (ComboBox)window.FindName("ModelCombo");
        var list = (ListBox)window.FindName("ProfileList");
        var name = (TextBox)window.FindName("ProfileNameBox");
        Check(list.Items.Count == 2);
        Mode(window, "SourceModeButton");
        Check(((TextBlock)window.FindName("SourceFileLabel")).Text == "settings.json");
        var original = source.Text;
        var json = JsonNode.Parse(original)!.AsObject();
        json["model"] = "edited-from-source";
        json["permissions"] = new JsonObject { ["allow"] = new JsonArray("Read") };
        source.Text = json.ToJsonString(ProfileConfiguration.JsonOptions);
        Mode(window, "BasicModeButton");
        Check(model.Text == "edited-from-source");
        model.Text = "edited-from-form";
        Mode(window, "SourceModeButton");
        Check(JsonNode.Parse(source.Text)!["model"]!.GetValue<string>() == "edited-from-form");
        Check(JsonNode.Parse(source.Text)!["permissions"] is JsonObject);
        Click(window, "SaveProfileButton");
        Check(ReadClaude(claude)["model"]!.GetValue<string>() != "edited-from-form");
        Click(window, "SaveActivateButton");
        Check(ReadClaude(claude)["model"]!.GetValue<string>() == "edited-from-form");
        Capture(window, "claude-source.png");
        var savedText = source.Text;
        source.Text = "{invalid source draft";
        var before = File.ReadAllBytes(claude.StatePath);
        Click(window, "SaveActivateButton");
        Check(before.SequenceEqual(File.ReadAllBytes(claude.StatePath)));
        Check(source.Text == "{invalid source draft");
        Mode(window, "BasicModeButton");
        Check(((FrameworkElement)window.FindName("SourceEditor")).Visibility == Visibility.Visible);
        list.SelectedIndex = 1; Pump(); list.SelectedIndex = 0; Pump();
        Check(source.Text == "{invalid source draft");
        Mode(window, "CodexNav");
        Check(name.Text == "日常开发");
        Mode(window, "SourceModeButton");
        Check(source.Text.Contains("model_providers.custom"));
        source.Text = "model_reasoning_effort='low'\n" + source.Text;
        Mode(window, "ClaudeNav");
        Check(source.Text == "{invalid source draft");
        source.Text = savedText;
        Click(window, "SaveProfileButton");
        Mode(window, "CodexNav");
        Check(source.Text.StartsWith("model_reasoning_effort='low'"));
        Click(window, "ValidateSourceButton");
        Click(window, "SaveActivateButton");
        Check((string)d.Config()["model_reasoning_effort"] == "low");
        Capture(window, "codex-source.png");
        Click(window, "ExpandSourceButton");
        Check(((FrameworkElement)window.FindName("ProfilesPanel")).Visibility == Visibility.Collapsed);
        Capture(window, "source-expanded.png");
        Click(window, "ExpandSourceButton");
        window.Width = 760; window.Height = 740; Pump();
        Capture(window, "source-compact.png");
        window.Width = 1180; window.Height = 800; Pump();
        Mode(window, "BasicModeButton");
        Mode(window, "ClaudeNav");
        Click(window, "NewProfileButton");
        name.Text = "Claude UI 新增";
        ((TextBox)window.FindName("BaseUrlBox")).Text = "https://ui-claude.example";
        ((PasswordBox)window.FindName("ApiKeyBox")).Password = "ui-key";
        model.Text = "ui-model";
        Click(window, "SaveProfileButton");
        Check(claude.LoadProfiles().Profiles.Count == 3);
        var newId = claude.LoadProfiles().Profiles.Last().Id;
        Click(window, "SaveActivateButton");
        Check(claude.LoadProfiles().ActiveId == newId);
        ((Button)window.FindName("DeleteProfileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(claude.LoadProfiles().Profiles.Count == 3);
        var target = claude.LoadProfiles().Profiles.First();
        var activate = new Button { Tag = target.Id };
        typeof(MainWindow).GetMethod("ActivateProfile_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, new object[] { activate, new RoutedEventArgs(Button.ClickEvent) });
        Pump();
        Click(window, "DeleteProfileButton");
        Check(claude.LoadProfiles().Profiles.Count == 2);
        Mode(window, "BasicModeButton");
        _passed++;
        Console.WriteLine("PASS Claude UI create/switch/delete, TOML/JSON source-form sync, syntax rejection and draft isolation");
    }
}
