using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ApiConfigTool;
using ApiConfigTool.Services;
using Tomlyn;
using Tomlyn.Model;

internal static partial class Program
{
    private static int _passed;
    [STAThread]
    private static int Main()
    {
        var tests = new (string Name, Action<TestDirectory> Run)[]
        {
            ("Empty startup is read-only", d => { Check(d.Service.LoadProfiles().Profiles.Count == 0); Check(!File.Exists(d.Service.StatePath)); }),
            ("Save several profiles without changing live files", d => { var a = d.Save("A"); d.Save("B"); Check(d.Service.LoadProfiles().Profiles.Count == 2); Check(!File.Exists(d.Service.ConfigPath)); Check(!File.Exists(d.Service.AuthPath)); Check(new CodexConfigService(d.Path).LoadProfiles().Profiles[0].Id == a.Id); }),
            ("Activate A/B using one custom route", d => { var a = d.Save("A"); var b = d.Save("B"); d.Service.ActivateProfile(a.Id); d.Service.ActivateProfile(b.Id); var table = d.Config(); Check((string)table["model_provider"] == "custom"); var provider = d.Provider(); Check((string)provider["experimental_bearer_token"] == b.ApiKey); Check((string)provider["wire_api"] == "responses"); Check(!(bool)provider["requires_openai_auth"]); Check(((TomlTable)table["model_providers"]).Count == 1); Check(d.Service.LoadProfiles().ActiveId == b.Id); Check(!File.Exists(d.Service.AuthPath)); }),
            ("Identical credentials still track the explicitly selected row", d => { var a = d.Save("A"); var b = d.Service.SaveProfile(null, "Same endpoint", a.BaseUrl, a.ApiKey, a.Model); d.Service.ActivateProfile(b.Id); Check(d.Service.LoadProfiles().ActiveId == b.Id); }),
            ("Editing active profile is a draft until activation", d => { var a = d.Save("A"); d.Service.ActivateProfile(a.Id); var before = File.ReadAllBytes(d.Service.ConfigPath); d.Service.SaveProfile(a.Id, "A edited", a.BaseUrl, "new-key", "new-model"); Check(before.SequenceEqual(File.ReadAllBytes(d.Service.ConfigPath))); Check(d.Service.LoadProfiles().ActiveId is null); d.Service.ActivateProfile(a.Id); Check((string)d.Provider()["experimental_bearer_token"] == "new-key"); }),
            ("Live GPT login is kept byte-for-byte", d => { d.WriteConfig("model = 'official-model'\n"); d.WriteAuth("{\"tokens\":{\"access_token\":\"official\",\"refresh_token\":\"refresh\"},\"auth_mode\":\"chatgpt\",\"unknown\":17}"); var before = File.ReadAllBytes(d.Service.AuthPath); var a = d.Save("A"); d.Service.ActivateProfile(a.Id); Check(before.SequenceEqual(File.ReadAllBytes(d.Service.AuthPath))); Check((bool)d.Provider()["requires_openai_auth"]); }),
            ("Latest GPT token wins over old snapshot on return", d => { d.WriteConfig("model = 'official-model'\n"); d.WriteAuth("{\"tokens\":{\"refresh_token\":\"old\"}}"); var a = d.Save("A"); d.Service.ActivateProfile(a.Id); d.WriteAuth("{\"tokens\":{\"refresh_token\":\"rotated\"}}"); d.Service.SwitchToGptAccount(); Check((string)d.Config()["model"] == "official-model"); Check(!d.Config().ContainsKey("model_provider")); Check(d.Auth()["tokens"]!["refresh_token"]!.GetValue<string>() == "rotated"); }),
            ("No-login GPT switch works and API can be reactivated", d => { var a = d.Save("A"); d.Service.ActivateProfile(a.Id); d.Service.SwitchToGptAccount(); Check(!d.Config().ContainsKey("model")); Check(d.Service.LoadProfiles().Mode == CodexConfigurationMode.GptAccount); d.Service.ActivateProfile(a.Id); Check(d.Service.LoadProfiles().ActiveId == a.Id); }),
            ("MCP, project trust, comments and features survive switching", d => { d.WriteConfig("# personal settings\npersonality = 'friendly'\nmodel_reasoning_effort = 'medium'\n[mcp_servers.example]\ncommand = 'node' # keep me\nargs = ['tool.js']\n[projects.'C:\\my project']\ntrust_level = 'trusted'\n[features]\nmulti_agent = true\n[model_providers.other]\nname = 'Other'\nbase_url = 'https://other.example/v1'\n"); var a = d.Save("A"); d.Service.ActivateProfile(a.Id); d.Service.SwitchToGptAccount(); var table = d.Config(); Check((string)table["personality"] == "friendly"); Check((string)table["model_reasoning_effort"] == "medium"); Check(((TomlTable)table["mcp_servers"]).ContainsKey("example")); Check(((TomlTable)table["projects"]).ContainsKey("C:\\my project")); Check(((TomlTable)table["model_providers"]).ContainsKey("other")); Check(File.ReadAllText(d.Service.ConfigPath).Contains("keep me")); }),
            ("Import old API and remove stale auth-mode selectors", d => { d.WriteConfig("model_provider = 'old.provider'\nmodel = 'legacy'\npreferred_auth_method = 'apikey'\n[model_providers.'old.provider']\nname = 'Legacy'\nbase_url = 'https://old.example/v1'\n"); d.WriteAuth("{\"OPENAI_API_KEY\":\"legacy-key\",\"auth_mode\":\"apikey\",\"tokens\":{\"refresh_token\":\"account\"}}"); var list = d.Service.LoadProfiles(); Check(list.Profiles.Count == 1); var id = list.Profiles[0].Id; Check(id == d.Service.LoadProfiles().Profiles[0].Id); d.Service.ActivateProfile(id); Check(!d.Config().ContainsKey("preferred_auth_method")); Check(!d.Auth().ContainsKey("OPENAI_API_KEY")); Check(!d.Auth().ContainsKey("auth_mode")); Check((string)d.Provider()["experimental_bearer_token"] == "legacy-key"); }),
            ("Migrate legacy API/GPT backups without losing login", d => { var legacy = new JsonObject { ["current_mode"] = "gpt", ["api"] = new JsonObject { ["config_toml"] = "model_provider='legacy'\nmodel='m'\n[model_providers.legacy]\nbase_url='https://old.example/v1'\n", ["auth_json"] = new JsonObject { ["OPENAI_API_KEY"] = "old-key" } }, ["gpt"] = new JsonObject { ["config"] = "model='official'\n", ["auth"] = new JsonObject { ["tokens"] = new JsonObject { ["refresh_token"] = "backup-login" } } } }; File.WriteAllText(d.Service.StatePath, legacy.ToJsonString()); var list = d.Service.LoadProfiles(); Check(list.Profiles.Count == 1); d.Service.ActivateProfile(list.Profiles[0].Id); d.Service.SwitchToGptAccount(); Check(d.Auth()["tokens"]!["refresh_token"]!.GetValue<string>() == "backup-login"); Check(JsonNode.Parse(File.ReadAllText(d.Service.StatePath))!["version"]!.GetValue<int>() == 2); }),
            ("Corrupt TOML is never overwritten", d => { var a = d.Save("A"); d.WriteConfig("[broken"); var before = d.Bytes(); Throws(() => d.Service.ActivateProfile(a.Id)); d.Unchanged(before); }),
            ("Corrupt auth is never overwritten", d => { var a = d.Save("A"); d.WriteAuth("{bad"); var before = d.Bytes(); Throws(() => d.Service.ActivateProfile(a.Id)); d.Unchanged(before); }),
            ("Corrupt store is never silently reset", d => { File.WriteAllText(d.Service.StatePath, "{broken"); var before = d.Bytes(); Throws(() => d.Save("A")); d.Unchanged(before); }),
            ("Config failure rolls auth back", d => Rollback(d, "config.toml")),
            ("State failure rolls both live files back", d => Rollback(d, "apiconfig_state.json")),
            ("Auth failure leaves everything unchanged", d => Rollback(d, "auth.json")),
            ("Rollback removes newly-created auth and config", d => { var a = d.Save("A"); var before = d.Bytes(); var fail = new CodexConfigService(d.Path, (path, bytes) => { if (path.EndsWith("apiconfig_state.json")) throw new IOException("injected"); AtomicFile.Write(path, bytes); }); Throws(() => fail.ActivateProfile(a.Id)); d.Unchanged(before); }),
            ("Codex deletes inactive, active and last profiles without touching live files", d =>
            {
                var a = d.Save("A"); var b = d.Save("B");
                d.WriteAuth("{\"tokens\":{\"refresh_token\":\"keep\"}}");
                d.Service.ActivateProfile(a.Id);
                var config = File.ReadAllBytes(d.Service.ConfigPath); var auth = File.ReadAllBytes(d.Service.AuthPath);
                d.Service.DeleteProfile(b.Id);
                Check(d.Service.LoadProfiles().ActiveId == a.Id);
                b = d.Save("B");
                d.Service.DeleteProfile(a.Id);
                var state = d.Service.LoadProfiles();
                Check(state.Profiles.Single().Id == b.Id && state.ActiveId is null && state.Mode == CodexConfigurationMode.ApiConfiguration);
                Check(!JsonNode.Parse(File.ReadAllText(d.Service.StatePath))!.AsObject().ContainsKey("active_profile_id"));
                d.Service.ActivateProfile(b.Id);
                config = File.ReadAllBytes(d.Service.ConfigPath);
                d.Service.DeleteProfile(b.Id);
                Check(new CodexConfigService(d.Path).LoadProfiles().Profiles.Count == 0);
                Check(config.SequenceEqual(File.ReadAllBytes(d.Service.ConfigPath)) && auth.SequenceEqual(File.ReadAllBytes(d.Service.AuthPath)));
            }),
            ("External config changes clear active status", d => { var a = d.Save("A"); d.Service.ActivateProfile(a.Id); d.WriteConfig("model='external'\n"); var list = d.Service.LoadProfiles(); Check(list.ActiveId is null && list.Mode == CodexConfigurationMode.GptAccount); }),
            ("Default Codex profile cannot silently override activation", d => { var a = d.Save("A"); d.WriteConfig("profile='work'\nmodel='m'\n[profiles.work]\nmodel_provider='openai'\n"); var before = d.Bytes(); Throws(() => d.Service.ActivateProfile(a.Id)); d.Unchanged(before); }),
            ("Active detection honors default Codex profile", d => { var a = d.Save("A"); d.Service.ActivateProfile(a.Id); var live = File.ReadAllText(d.Service.ConfigPath); d.WriteConfig("profile='work'\n" + live + "\n[profiles.work]\nmodel='different-model'\n"); Check(d.Service.LoadProfiles().ActiveId is null); }),
            ("Explicit import is idempotent", d => { var a = d.Save("A"); d.Service.ActivateProfile(a.Id); Check(d.Service.ImportCurrent().Id == a.Id); Check(d.Service.ImportCurrent().Id == a.Id); Check(d.Service.LoadProfiles().Profiles.Count == 1); }),
            ("Quoted names, multiline TOML and arrays remain valid", d => { d.WriteConfig("instructions = '''\nmodel = \"not a setting\"\n[model_providers.fake]\n'''\n[[example]]\nmodel = 'nested'\n"); var a = d.Service.SaveProfile(null, "测试 \"名称\"", "https://example.com/v1", "key\\with\"quote", "model-quoted"); d.Service.ActivateProfile(a.Id); Check((string)d.Provider()["name"] == a.Name); Check((string)d.Provider()["experimental_bearer_token"] == a.ApiKey); Check(((string)d.Config()["instructions"]).Contains("not a setting")); }),
            ("Custom URL paths are preserved", d => { var a = d.Service.SaveProfile(null, null, "example.com/api/coding/v3/", "key", "m"); Check(a.BaseUrl == "https://example.com/api/coding/v3"); Check(ModelsApiService.BuildModelsEndpoint(a.BaseUrl) == "https://example.com/api/coding/v3/models"); Check(ModelsApiService.BuildModelsEndpoint("https://example.com") == "https://example.com/v1/models"); }),
            ("Input validation rejects empty keys and unsafe URLs", d => { foreach (var url in new[] { "", "https://", "https://user:pass@example.com", "https://example.com?key=secret", "https://example.com/#part", "https://bad host" }) Throws(() => d.Service.SaveProfile(null, "", url, "key", "m")); Throws(() => d.Service.SaveProfile(null, "", "https://example.com", "", "m")); Throws(() => d.Service.SaveProfile(null, "", "https://example.com", "key\nvalue", "m")); Check(!File.Exists(d.Service.StatePath)); }),
            ("Reserved provider names migrate with profile references", d => { d.WriteConfig("model_provider='openai'\nmodel='m'\n[model_providers.openai]\nbase_url='https://old.example/v1'\n[profiles.old]\nmodel_provider='openai'\n"); d.WriteAuth("{\"OPENAI_API_KEY\":\"key\"}"); var a = d.Service.LoadProfiles().Profiles.Single(); d.Service.ActivateProfile(a.Id); var providers = (TomlTable)d.Config()["model_providers"]; Check(!providers.ContainsKey("openai") && providers.ContainsKey("openai_legacy")); Check((string)((TomlTable)((TomlTable)d.Config()["profiles"])["old"])["model_provider"] == "openai_legacy"); }),
            ("Store lock prevents a second writer", d => { using var held = new FileStream(System.IO.Path.Combine(d.Path, ".apiconfig.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); Throws(() => d.Save("A")); Check(!File.Exists(d.Service.StatePath)); }),
            ("CODEX_HOME is respected", d => { var old = Environment.GetEnvironmentVariable("CODEX_HOME"); try { Environment.SetEnvironmentVariable("CODEX_HOME", d.Path); Check(new CodexConfigService().ConfigPath == d.Service.ConfigPath); } finally { Environment.SetEnvironmentVariable("CODEX_HOME", old); } }),
            ("Claude existing settings are preserved", d => { var service = new ClaudeConfigService(d.Path); File.WriteAllText(service.SettingsPath, "{\"permissions\":{\"allow\":[\"Read\"]},\"env\":{\"OTHER\":\"keep\"}}"); service.Apply("https://example.com/v1", "key", "model"); var root = JsonNode.Parse(File.ReadAllText(service.SettingsPath))!; Check(root["env"]!["OTHER"]!.GetValue<string>() == "keep"); Check(root["permissions"] is not null); })
        };
        try
        {
            foreach (var test in tests)
            {
                using var directory = new TestDirectory();
                test.Run(directory);
                Console.WriteLine("PASS " + test.Name);
                _passed++;
            }
            ExtendedChecks();
            BalanceChecks().GetAwaiter().GetResult();
            UiSmoke();
            Console.WriteLine($"PASS all {_passed} checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"FAIL after {_passed} checks: {ex}"); return 1; }
    }
    private static void Rollback(TestDirectory d, string failFile)
    {
        d.WriteConfig("model='official'\n");
        d.WriteAuth("{\"OPENAI_API_KEY\":\"stale-key\",\"tokens\":{\"refresh_token\":\"keep\"}}");
        var a = d.Save("A"); var before = d.Bytes();
        var fail = new CodexConfigService(d.Path, (path, bytes) => { if (System.IO.Path.GetFileName(path) == failFile) throw new IOException("injected"); AtomicFile.Write(path, bytes); });
        Throws(() => fail.ActivateProfile(a.Id)); d.Unchanged(before);
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected failure"); }

    private static void UiSmoke()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var d = new TestDirectory();
        var a = d.Service.SaveProfile(null, "日常开发", "https://api.example.com/v1", "demo-key", "gpt-5");
        d.Service.SaveProfile(null, "备用服务", "https://backup.example.com/v1", "demo-backup", "gpt-5-mini");
        d.Service.ActivateProfile(a.Id);
        var claude = new ClaudeConfigService(d.Path);
        var c = claude.SaveProfile(null, "Claude 日常", "https://claude.example.com", "demo-claude", "claude-sonnet");
        claude.SaveProfile(null, "Claude 备用", "https://claude-backup.example.com", "demo-backup", "claude-haiku");
        claude.ActivateProfile(c.Id);
        var balanceMonitor = new BalanceMonitor(new DemoBalanceService());
        var window = new MainWindow(d.Service, claude, balanceMonitor) { Left = -12000, Top = -12000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show();
        Pump();
        Check(((ListBox)window.FindName("ProfileList")).Items.Count == 2);
        var balanceCard = (MainWindow.ProfileCard)((ListBox)window.FindName("ProfileList")).Items[0];
        Check(balanceCard.BalanceSummary.Contains("账号余额"));
        Check(balanceCard.BalanceSummary.Contains("42.5000"));
        ((CheckBox)window.FindName("AutoBalanceCheck")).IsChecked = false;
        Check(((CheckBox)window.FindName("AutoBalanceCheck")).IsChecked == false);
        Click(window, "RefreshBalancesButton");
        ((CheckBox)window.FindName("AutoBalanceCheck")).IsChecked = true;
        Check(((TextBox)window.FindName("ProfileNameBox")).Text == "日常开发");
        var combo = (ComboBox)window.FindName("ModelCombo");
        Check(combo.Text == "gpt-5");
        var editor = (TextBox)combo.Template.FindName("PART_EditableTextBox", combo);
        editor.Text = "typed-model"; Pump(); Check(combo.Text == "typed-model");
        editor.Text = "gpt-5"; Pump();
        ModelDropdownChecks(window, combo);
        var profiles = (ListBox)window.FindName("ProfileList");
        var baseUrl = (TextBox)window.FindName("BaseUrlBox");
        baseUrl.Text = "https://draft.example.com/v1";
        profiles.SelectedIndex = 1; Pump(); profiles.SelectedIndex = 0; Pump();
        Check(baseUrl.Text == "https://draft.example.com/v1");
        baseUrl.Text = a.BaseUrl;
        Capture(window, "codex-desktop.png");
        window.Width = 760; window.Height = 740; Pump();
        Check(((FrameworkElement)window.FindName("BrandLabel")).Visibility == Visibility.Collapsed);
        Check(((FrameworkElement)window.FindName("EditorPanel")).ActualWidth > 300);
        Capture(window, "codex-compact.png");
        window.Width = 1180; window.Height = 800;
        ((RadioButton)window.FindName("ClaudeNav")).IsChecked = true; Pump();
        Check(((TextBlock)window.FindName("PageTitle")).Text == "Claude Code");
        Capture(window, "claude-desktop.png");
        ExtendedUi(window, d, claude);
        ((RadioButton)window.FindName("CodexNav")).IsChecked = true;
        Click(window, "NewProfileButton");
        ((TextBox)window.FindName("ProfileNameBox")).Text = "UI test";
        baseUrl.Text = "https://ui.example.com/v1";
        ((PasswordBox)window.FindName("ApiKeyBox")).Password = "ui-key";
        combo.Text = "ui-model";
        Click(window, "SaveProfileButton");
        Check(d.Service.LoadProfiles().Profiles.Count == 3);
        Check(d.Service.LoadProfiles().ActiveId == a.Id);
        Click(window, "SaveActivateButton");
        Check((string)d.Provider()["experimental_bearer_token"] == "ui-key");
        var newId = d.Service.LoadProfiles().ActiveId!;
        ConfirmedDeletionChecks(window, d.Service, newId);
        Check(d.Service.LoadProfiles().Profiles.Count == 2 && d.Service.LoadProfiles().ActiveId is null);
        Click(window, "GptButton");
        Check(d.Service.LoadProfiles().Mode == CodexConfigurationMode.GptAccount);
        DeleteOtherAndLastProfileChecks(window, d.Service, a.Id);
        window.Close(); app.Shutdown();
        _passed++;
        Console.WriteLine("PASS WPF render, editable model, draft retention, compact layout, navigation and save/activate/GPT/delete buttons");
    }
    private static void Click(Window window, string name)
    {
        ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var output = System.IO.Path.GetFullPath("artifacts/ui"); Directory.CreateDirectory(output);
        using var file = File.Create(System.IO.Path.Combine(output, name)); encoder.Save(file);
    }
    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apiconfig-tests-" + Guid.NewGuid().ToString("N"));
        public CodexConfigService Service { get; }
        public TestDirectory() { Directory.CreateDirectory(Path); Service = new CodexConfigService(Path); }
        public ApiProfile Save(string name) => Service.SaveProfile(null, name, "https://" + name.ToLowerInvariant() + ".example/v1", "key-" + name, "model-" + name);
        public void WriteConfig(string text) => File.WriteAllText(Service.ConfigPath, text);
        public void WriteAuth(string text) => File.WriteAllText(Service.AuthPath, text);
        public TomlTable Config() => Toml.ToModel(File.ReadAllText(Service.ConfigPath));
        public TomlTable Provider() => (TomlTable)((TomlTable)Config()["model_providers"])["custom"];
        public JsonObject Auth() => (JsonObject)JsonNode.Parse(File.ReadAllText(Service.AuthPath))!;
        public byte[]?[] Bytes() => new[] { AtomicFile.Read(Service.AuthPath), AtomicFile.Read(Service.ConfigPath), AtomicFile.Read(Service.StatePath) };
        public void Unchanged(byte[]?[] before) { var after = Bytes(); for (var i = 0; i < before.Length; i++) Check(before[i] is null ? after[i] is null : after[i] is not null && before[i]!.SequenceEqual(after[i]!)); }
        public void Dispose()
        {
            // Only delete the verified, generated test directory.
            var full = System.IO.Path.GetFullPath(Path);
            var prefix = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apiconfig-tests-");
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test directory");
            Directory.Delete(full, recursive: true);
        }
    }
}
