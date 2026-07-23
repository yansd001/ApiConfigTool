using System.IO;
using System.Windows;
using System.Windows.Controls;
using ApiConfigTool.Services;

namespace ApiConfigTool;

public partial class MainWindow : Window
{
    private readonly ModelsApiService _modelsApi = new();
    private readonly CodexConfigService _codex = new();
    private readonly ClaudeConfigService _claude = new();

    public MainWindow()
    {
        InitializeComponent();
        CodexPathHint.Text = $"配置路径：{_codex.ConfigPath}\n认证路径：{_codex.AuthPath}";
        ClaudePathHint.Text = $"配置路径：{_claude.SettingsPath}";
        Loaded += (_, _) =>
        {
            LoadCodexExisting(quiet: true);
            LoadClaudeExisting(quiet: true);
            AppendLog("就绪。请在标签页中分别配置 Codex 或 Claude Code。");
        };
    }

    private void CodexShowKeyCheck_Changed(object sender, RoutedEventArgs e)
    {
        ToggleKeyVisibility(CodexShowKeyCheck, CodexApiKeyBox, CodexApiKeyPlainBox);
    }

    private async void CodexFetchModelsButton_Click(object sender, RoutedEventArgs e)
    {
        await FetchModelsAsync(
            CodexBaseUrlBox.Text,
            GetApiKey(CodexShowKeyCheck, CodexApiKeyBox, CodexApiKeyPlainBox),
            CodexModelCombo,
            CodexFetchModelsButton,
            "Codex");
    }

    private void CodexLoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!LoadCodexExisting(quiet: false))
            MessageBox.Show("未找到现有 Codex 配置。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CodexApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInputs(
                CodexBaseUrlBox.Text,
                GetApiKey(CodexShowKeyCheck, CodexApiKeyBox, CodexApiKeyPlainBox),
                GetModel(CodexModelCombo),
                out var baseUrl, out var apiKey, out var model))
            return;

        try
        {
            var existed = File.Exists(_codex.ConfigPath);
            var normalized = ModelsApiService.NormalizeCodexBaseUrl(baseUrl);
            _codex.Apply(baseUrl, apiKey, model);
            CodexBaseUrlBox.Text = normalized;
            AppendLog(existed
                ? $"已更新 Codex 配置（仅替换 base_url / model / api_key）\n  base_url => {normalized}\n  config: {_codex.ConfigPath}\n  auth: {_codex.AuthPath}"
                : $"已创建 Codex 配置\n  base_url => {normalized}\n  config: {_codex.ConfigPath}\n  auth: {_codex.AuthPath}");
            MessageBox.Show("Codex 配置已写入成功。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog("写入 Codex 失败: " + ex.Message);
            MessageBox.Show(ex.Message, "写入失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool LoadCodexExisting(bool quiet)
    {
        var current = _codex.LoadCurrent();
        if (current.BaseUrl is null && current.Model is null && current.ApiKey is null)
        {
            if (!quiet) AppendLog("未找到 Codex 配置文件。");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(current.BaseUrl))
            CodexBaseUrlBox.Text = current.BaseUrl;
        if (!string.IsNullOrWhiteSpace(current.Model))
            CodexModelCombo.Text = current.Model!;
        if (!string.IsNullOrWhiteSpace(current.ApiKey))
            SetApiKey(CodexApiKeyBox, CodexApiKeyPlainBox, current.ApiKey);

        if (!quiet) AppendLog("已加载现有 Codex 配置。");
        else AppendLog("已自动加载本机 Codex 配置。");
        return true;
    }

    private void ClaudeShowKeyCheck_Changed(object sender, RoutedEventArgs e)
    {
        ToggleKeyVisibility(ClaudeShowKeyCheck, ClaudeApiKeyBox, ClaudeApiKeyPlainBox);
    }

    private async void ClaudeFetchModelsButton_Click(object sender, RoutedEventArgs e)
    {
        await FetchModelsAsync(
            ClaudeBaseUrlBox.Text,
            GetApiKey(ClaudeShowKeyCheck, ClaudeApiKeyBox, ClaudeApiKeyPlainBox),
            ClaudeModelCombo,
            ClaudeFetchModelsButton,
            "Claude Code");
    }

    private void ClaudeLoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!LoadClaudeExisting(quiet: false))
            MessageBox.Show("未找到现有 Claude Code 配置。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ClaudeApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInputs(
                ClaudeBaseUrlBox.Text,
                GetApiKey(ClaudeShowKeyCheck, ClaudeApiKeyBox, ClaudeApiKeyPlainBox),
                GetModel(ClaudeModelCombo),
                out var baseUrl, out var apiKey, out var model))
            return;

        try
        {
            var existed = File.Exists(_claude.SettingsPath);
            var normalized = ModelsApiService.NormalizeClaudeBaseUrl(baseUrl);
            _claude.Apply(baseUrl, apiKey, model);
            ClaudeBaseUrlBox.Text = normalized;
            AppendLog(existed
                ? $"已更新 Claude Code 配置（仅替换 base_url / model / api_key）\n  base_url => {normalized}\n  {_claude.SettingsPath}"
                : $"已创建 Claude Code 配置\n  base_url => {normalized}\n  {_claude.SettingsPath}");
            MessageBox.Show("Claude Code 配置已写入成功。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog("写入 Claude Code 失败: " + ex.Message);
            MessageBox.Show(ex.Message, "写入失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool LoadClaudeExisting(bool quiet)
    {
        var current = _claude.LoadCurrent();
        if (current.BaseUrl is null && current.Model is null && current.ApiKey is null)
        {
            if (!quiet) AppendLog("未找到 Claude Code 配置文件。");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(current.BaseUrl))
            ClaudeBaseUrlBox.Text = current.BaseUrl;
        if (!string.IsNullOrWhiteSpace(current.Model))
            ClaudeModelCombo.Text = current.Model!;
        if (!string.IsNullOrWhiteSpace(current.ApiKey))
            SetApiKey(ClaudeApiKeyBox, ClaudeApiKeyPlainBox, current.ApiKey);

        if (!quiet) AppendLog("已加载现有 Claude Code 配置。");
        else AppendLog("已自动加载本机 Claude Code 配置。");
        return true;
    }

    private static void ToggleKeyVisibility(CheckBox showCheck, PasswordBox passwordBox, TextBox plainBox)
    {
        if (showCheck.IsChecked == true)
        {
            plainBox.Text = passwordBox.Password;
            passwordBox.Visibility = Visibility.Collapsed;
            plainBox.Visibility = Visibility.Visible;
        }
        else
        {
            passwordBox.Password = plainBox.Text;
            plainBox.Visibility = Visibility.Collapsed;
            passwordBox.Visibility = Visibility.Visible;
        }
    }

    private static string GetApiKey(CheckBox showCheck, PasswordBox passwordBox, TextBox plainBox)
        => showCheck.IsChecked == true ? plainBox.Text.Trim() : passwordBox.Password.Trim();

    private static void SetApiKey(PasswordBox passwordBox, TextBox plainBox, string? key)
    {
        key ??= string.Empty;
        passwordBox.Password = key;
        plainBox.Text = key;
    }

    private static string GetModel(ComboBox combo)
    {
        if (combo.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
            return selected.Trim();
        return (combo.Text ?? string.Empty).Trim();
    }

    private static bool TryGetInputs(string? baseUrlRaw, string apiKey, string model, out string baseUrl, out string key, out string modelName)
    {
        baseUrl = (baseUrlRaw ?? string.Empty).Trim();
        key = apiKey;
        modelName = model;

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            MessageBox.Show("请填写 Base URL。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            MessageBox.Show("请填写 API Key。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (string.IsNullOrWhiteSpace(modelName))
        {
            MessageBox.Show("请填写或选择模型名称。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private async Task FetchModelsAsync(string? baseUrlRaw, string apiKey, ComboBox modelCombo, Button fetchButton, string sourceLabel)
    {
        var baseUrl = (baseUrlRaw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show("获取模型前请先填写 Base URL 和 API Key。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        fetchButton.IsEnabled = false;
        var oldContent = fetchButton.Content;
        fetchButton.Content = "获取中...";
        AppendLog($"[{sourceLabel}] 请求模型列表: {ModelsApiService.BuildModelsEndpoint(baseUrl)}");

        try
        {
            var models = await _modelsApi.FetchModelsAsync(baseUrl, apiKey);
            var previous = GetModel(modelCombo);
            modelCombo.Items.Clear();
            foreach (var m in models)
                modelCombo.Items.Add(m);

            if (!string.IsNullOrWhiteSpace(previous))
            {
                var match = models.FirstOrDefault(x => string.Equals(x, previous, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    modelCombo.SelectedItem = match;
                else
                    modelCombo.Text = previous;
            }
            else if (models.Count > 0)
            {
                modelCombo.SelectedIndex = 0;
            }

            AppendLog($"[{sourceLabel}] 成功获取 {models.Count} 个模型。");
            if (models.Count == 0)
                MessageBox.Show("接口返回成功，但未解析到模型 ID。你可以手动输入模型名称。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"[{sourceLabel}] 获取模型失败: {ex.Message}");
            MessageBox.Show(ex.Message, "获取模型失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            fetchButton.Content = oldContent;
            fetchButton.IsEnabled = true;
        }
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (string.IsNullOrWhiteSpace(LogBox.Text))
            LogBox.Text = line;
        else
            LogBox.AppendText(Environment.NewLine + line);
        LogBox.ScrollToEnd();
    }
}
