using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ApiConfigTool.Services;

namespace ApiConfigTool;

public partial class MainWindow : Window
{
    private readonly ModelsApiService _modelsApi = new();
    private readonly CodexConfigService _codex;
    private readonly ClaudeConfigService _claude;
    private readonly BalanceMonitor _balances;
    private readonly DispatcherTimer _balanceTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _refreshingBalances;
    private bool _closed;
    private readonly EditorSession _codexSession = new();
    private readonly EditorSession _claudeSession = new();
    private bool _isCodex = true;
    private bool _refreshing;
    private bool _ready;
    private bool _changingMode;
    private CancellationTokenSource? _fetch;
    private string? _configText;
    private string? _previewBaseline;
    private bool _sourceMode;
    private bool _expandedSource;
    private IProfileConfigService Service => _isCodex ? _codex : _claude;
    private EditorSession Session => _isCodex ? _codexSession : _claudeSession;
    private string AppName => _isCodex ? "Codex" : "Claude Code";
    internal Func<ApiProfile, bool> ConfirmProfileDeletion { get; set; }

    public sealed class ProfileCard(ApiProfile profile, bool isActive, BalanceSnapshot balance) : INotifyPropertyChanged
    {
        public ApiProfile Profile { get; } = profile;
        public bool IsActive { get; } = isActive;
        public bool CanActivate => !IsActive;
        private BalanceSnapshot _balance = balance;
        public string BalanceSummary => _balance.Summary;
        public string BalanceDetail => _balance.Detail;
        public string BalanceTooltip => _balance.Tooltip;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void UpdateBalance(BalanceSnapshot value)
        {
            _balance = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BalanceSummary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BalanceDetail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BalanceTooltip)));
        }
    }
    private sealed record Draft(string Name, string Url, string Key, string Model, string? ConfigText,
        bool SourceMode = false, string? SourceText = null, string? PreviewBaseline = null);
    private sealed class EditorSession
    {
        public string? Id;
        public bool Initialized;
        public List<ProfileCard> Cards = new();
        public readonly Dictionary<string, Draft> Drafts = new();
    }

    public MainWindow() : this(new CodexConfigService(), new ClaudeConfigService()) { }
    public MainWindow(CodexConfigService codex, ClaudeConfigService claude, BalanceMonitor? balances = null)
    {
        _codex = codex;
        _claude = claude;
        _balances = balances ?? new BalanceMonitor();
        ConfirmProfileDeletion = profile => MessageBox.Show(this,
            $"确定删除配置「{profile.Name}」吗？\n\n删除后，已写入本机的 {AppName} 配置仍会保留。",
            "删除配置", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        InitializeComponent();
        _balances.Changed += Balances_Changed;
        SizeChanged += (_, _) => ApplyLayout();
        Loaded += async (_, _) =>
        {
            ApplyLayout();
            SetApplicationLabels();
            Run(() => RefreshProfiles(selectInitial: true));
            _ready = true;
            _balanceTimer.Start();
            await RefreshBalancesAsync();
        };
        _balanceTimer.Tick += async (_, _) => { if (AutoBalanceCheck.IsChecked == true) await RefreshBalancesAsync(); };
        Activated += (_, _) => { if (_ready) Run(() => RefreshProfiles()); };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _closed = true;
            _balanceTimer.Stop();
            _fetch?.Cancel();
            _lifetime.Cancel();
            _balances.Changed -= Balances_Changed;
            _balances.Dispose();
        };
    }

    private void ApplyLayout()
    {
        var compact = ActualWidth < 980;
        SidebarColumn.Width = new GridLength(compact ? 76 : 174);
        foreach (var element in new FrameworkElement[] { BrandLabel, AppsLabel, CodexNavLabel, ClaudeNavLabel, SidebarFooter })
            element.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        MainLayout.Margin = compact ? new Thickness(20, 20, 20, 14) : new Thickness(28, 24, 28, 16);
        ProfileListColumn.Width = new GridLength(_expandedSource ? 0 : compact ? 220 : 300);
        ProfilesPanel.Visibility = _expandedSource ? Visibility.Collapsed : Visibility.Visible;
        ProfilesPanel.Margin = new Thickness(0, 0, compact ? 12 : 20, 0);
        EditorPanel.Padding = new Thickness(compact ? 18 : 24);
    }

    private void Navigation_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        RememberDraft();
        _isCodex = CodexNav.IsChecked == true;
        SetApplicationLabels();
        // Clear the old app's cards before loading, even if the new store is corrupt.
        _refreshing = true;
        ProfileList.ItemsSource = null;
        _refreshing = false;
        var selectedId = Session.Id;
        ShowEditor(null);
        Session.Id = selectedId;
        Run(() => RefreshProfiles(selectInitial: true));
    }

    private void SetApplicationLabels()
    {
        PageTitle.Text = AppName;
        PageSubtitle.Text = "管理 API 配置，选择当前使用的服务。";
        GptPanel.Visibility = _isCodex ? Visibility.Visible : Visibility.Collapsed;
        ConfigPathHint.Text = Service.ConfigPath;
        ConfigPathHint.ToolTip = $"配置库：{Service.StatePath}";
        SourceFileLabel.Text = _isCodex ? "config.toml" : "settings.json";
    }

    private void RefreshProfiles(string? selectId = null, bool selectInitial = false)
    {
        var state = Service.LoadProfiles();
        Session.Cards = state.Profiles.Select(p => new ProfileCard(p, p.Id == state.ActiveId, _balances.Get(p))).ToList();
        _refreshing = true;
        try
        {
            ProfileList.ItemsSource = Session.Cards;
            var id = selectId ?? (selectInitial && !Session.Initialized ? state.ActiveId ?? Session.Cards.FirstOrDefault()?.Profile.Id : Session.Id);
            ProfileList.SelectedItem = Session.Cards.FirstOrDefault(p => p.Profile.Id == id);
        }
        finally { _refreshing = false; }
        ProfileCountText.Text = state.Profiles.Count.ToString();
        EmptyProfiles.Visibility = state.Profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var gpt = state.Mode == CodexConfigurationMode.GptAccount;
        GptButton.IsEnabled = !gpt;
        GptButton.Content = gpt ? "已选择 GPT 账号" : "切换到 GPT 账号";
        CodexModeText.Text = gpt ? "使用 Codex 官方登录流程管理账号。" : state.ActiveId is null
            ? "当前 API 与已保存配置不同，可重新启用或导入。" : "当前使用 API 配置，可随时切回账号。";
        if (selectId is not null || selectInitial)
            ShowEditor((ProfileList.SelectedItem as ProfileCard)?.Profile);
        Session.Initialized = true;
        if (_ready && AutoBalanceCheck.IsChecked == true) _ = RefreshBalancesAsync();
    }

    private void Balances_Changed(object? sender, EventArgs e)
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            foreach (var card in _codexSession.Cards.Concat(_claudeSession.Cards)) card.UpdateBalance(_balances.Get(card.Profile));
            RefreshBalancesButton.IsEnabled = !_balances.IsRunning;
        });
    }

    private async Task RefreshBalancesAsync(bool force = false)
    {
        if (_closed || _refreshingBalances) return;
        _refreshingBalances = true;
        RefreshBalancesButton.IsEnabled = false;
        try
        {
            var profiles = new List<ApiProfile>();
            // A malformed file for one app does not prevent the other app's balances.
            foreach (var service in new IProfileConfigService[] { _codex, _claude })
            {
                try { profiles.AddRange(service.LoadProfiles().Profiles); }
                catch { /* The configuration UI reports its own read errors. */ }
            }
            await _balances.RefreshAsync(profiles, force, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { if (!_closed) Report("余额刷新失败，请稍后重试。", true); }
        finally { _refreshingBalances = false; if (!_closed) RefreshBalancesButton.IsEnabled = true; }
    }

    private async void RefreshBalances_Click(object sender, RoutedEventArgs e) => await RefreshBalancesAsync(force: true);
    private async void AutoBalance_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (AutoBalanceCheck.IsChecked == true) await RefreshBalancesAsync();
    }

    private Draft CurrentDraft() => new(ProfileNameBox.Text, BaseUrlBox.Text, GetKey(), ModelCombo.Text,
        _configText, _sourceMode, _sourceMode ? SourceBox.Text : null, _previewBaseline);
    private void RememberDraft() => Session.Drafts[Session.Id ?? ""] = CurrentDraft();
    private static Draft FromProfile(ApiProfile? p) => p is null ? new("", "", "", "", null) : new(p.Name, p.BaseUrl, p.ApiKey, p.Model, p.ConfigText);

    private void ShowEditor(ApiProfile? profile)
    {
        _fetch?.Cancel();
        Session.Id = profile?.Id;
        var draft = Session.Drafts.GetValueOrDefault(Session.Id ?? "") ?? FromProfile(profile);
        ProfileNameBox.Text = draft.Name;
        BaseUrlBox.Text = draft.Url;
        ShowKeyCheck.IsChecked = false;
        SetKey(draft.Key);
        ModelCombo.Items.Clear();
        ModelCombo.Text = draft.Model;
        _configText = draft.ConfigText;
        _previewBaseline = draft.PreviewBaseline;
        SourceBox.Text = draft.SourceText ?? "";
        SourceBox.IsUndoEnabled = false; SourceBox.IsUndoEnabled = true;
        SetEditorMode(draft.SourceMode);
        EditorTitle.Text = profile is null ? "新增配置" : "编辑配置";
    }

    private void SetEditorMode(bool source)
    {
        _changingMode = true;
        _sourceMode = source;
        SourceModeButton.IsChecked = source;
        BasicModeButton.IsChecked = !source;
        SourceEditor.Visibility = source ? Visibility.Visible : Visibility.Collapsed;
        BasicEditor.Visibility = source ? Visibility.Collapsed : Visibility.Visible;
        NameField.Visibility = source ? Visibility.Collapsed : Visibility.Visible;
        EditorDescription.Visibility = source ? Visibility.Collapsed : Visibility.Visible;
        if (!source) _expandedSource = false;
        ExpandSourceButton.Content = _expandedSource ? "收起编辑" : "展开编辑";
        ApplyLayout();
        CustomSourceHint.Visibility = _configText is null ? Visibility.Collapsed : Visibility.Visible;
        _changingMode = false;
    }

    private ApiProfile FormProfile()
    {
        var name = ProfileNameBox.Text.Trim();
        if (name.Length == 0 && Uri.TryCreate(ModelsApiService.EnsureScheme(BaseUrlBox.Text), UriKind.Absolute, out var uri)) name = uri.Host;
        return new(Session.Id ?? "", name, BaseUrlBox.Text.Trim(), GetKey(), ModelCombo.Text.Trim(), _configText);
    }

    private void ExpandSource_Click(object sender, RoutedEventArgs e)
    {
        _expandedSource = !_expandedSource;
        ExpandSourceButton.Content = _expandedSource ? "收起编辑" : "展开编辑";
        ApplyLayout();
    }
    private void EditorMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _changingMode) return;
        var target = SourceModeButton.IsChecked == true;
        if (target == _sourceMode) return;
        try
        {
            if (target)
            {
                var p = FormProfile();
                // Preview works before fields are complete; validation happens on save.
                if (!string.IsNullOrWhiteSpace(p.BaseUrl))
                    p = p with { BaseUrl = _isCodex ? ModelsApiService.NormalizeCodexBaseUrl(p.BaseUrl) : ProfileConfiguration.ValidateUrl(p.BaseUrl) };
                SourceBox.Text = Service.PreviewConfig(p);
                SourceBox.IsUndoEnabled = false; SourceBox.IsUndoEnabled = true;
                _previewBaseline = SourceBox.Text;
            }
            else if (SourceBox.Text != _previewBaseline)
            {
                var parsed = Service.ParseConfig(SourceBox.Text, Session.Id, ProfileNameBox.Text);
                _configText = SourceBox.Text;
                BaseUrlBox.Text = parsed.BaseUrl;
                SetKey(parsed.ApiKey);
                ModelCombo.Text = parsed.Model;
            }
            SetEditorMode(target);
        }
        catch (Exception ex) { SetEditorMode(_sourceMode); Report(ex.Message, true); }
    }

    private void ValidateSource_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        Service.ParseConfig(SourceBox.Text, Session.Id, ProfileNameBox.Text);
        Report($"{SourceFileLabel.Text} 校验通过。尚未写入本机配置。");
    });

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || ProfileList.SelectedItem is not ProfileCard card) return;
        RememberDraft();
        ShowEditor(card.Profile);
    }
    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        RememberDraft();
        _refreshing = true;
        ProfileList.SelectedItem = null;
        _refreshing = false;
        ShowEditor(null);
        ProfileNameBox.Focus();
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e) => SaveProfile(false);
    private void SaveActivate_Click(object sender, RoutedEventArgs e) => SaveProfile(true);
    private void SaveProfile(bool activate) => Run(() =>
    {
        var draft = CurrentDraft();
        string? source = null;
        if (_sourceMode)
        {
            var parsed = Service.ParseConfig(SourceBox.Text, Session.Id, draft.Name);
            draft = draft with { Url = parsed.BaseUrl, Key = parsed.ApiKey, Model = parsed.Model };
            if (_configText is not null || SourceBox.Text != _previewBaseline) source = SourceBox.Text;
        }
        else if (_configText is not null)
            source = Service.PreviewConfig(FormProfile());
        var profile = Service.SaveProfile(Session.Id, draft.Name, draft.Url, draft.Key, draft.Model, source);
        var wasSource = _sourceMode;
        Session.Drafts.Remove(Session.Id ?? "");
        RefreshProfiles(profile.Id);
        if (wasSource)
        {
            SourceBox.Text = Service.PreviewConfig(profile);
            SourceBox.IsUndoEnabled = false; SourceBox.IsUndoEnabled = true;
            _previewBaseline = SourceBox.Text;
            SetEditorMode(true);
        }
        if (activate)
        {
            try { Service.ActivateProfile(profile.Id); }
            catch (Exception ex) { Report("配置已保存，但启用失败：" + ex.Message, true); return; }
            RefreshProfiles();
        }
        Report(activate ? $"已启用「{profile.Name}」。重新启动 {AppName} 会话后生效。" : $"已保存「{profile.Name}」。点击启用后生效。");
    });

    private void ActivateProfile_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (sender is not Button { Tag: string id }) return;
        Service.ActivateProfile(id);
        RefreshProfiles();
        Report($"已启用所选配置。重新启动 {AppName} 会话后生效。");
        e.Handled = true;
    });
    private void DeleteProfile_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        e.Handled = true;
        if (sender is not Button { Tag: string id }) return;
        var profile = Session.Cards.FirstOrDefault(card => card.Profile.Id == id)?.Profile;
        if (profile is null || !ConfirmProfileDeletion(profile)) return;
        Service.DeleteProfile(id);
        Session.Drafts.Remove(id);
        if (Session.Id == id)
        {
            Session.Id = null;
            Session.Initialized = false;
            RefreshProfiles(selectInitial: true);
        }
        else RefreshProfiles();
        Report($"已删除「{profile.Name}」，本机配置保持不变。");
    });
    private void ImportProfile_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        RememberDraft();
        var profile = Service.ImportCurrent();
        Session.Drafts.Remove(profile.Id);
        RefreshProfiles(profile.Id);
        Report($"已导入本机 {AppName} 配置，包含完整源码。");
    });
    private void SwitchGpt_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        _codex.SwitchToGptAccount();
        RefreshProfiles();
        Report("已切换到 GPT 账号。尚未登录时，请使用 Codex 自带的登录流程。");
    });

    private void ShowKey_Changed(object sender, RoutedEventArgs e)
    {
        if (ApiKeyBox is null || ApiKeyPlainBox is null) return;
        if (ShowKeyCheck.IsChecked == true) { ApiKeyPlainBox.Text = ApiKeyBox.Password; ApiKeyBox.Visibility = Visibility.Collapsed; ApiKeyPlainBox.Visibility = Visibility.Visible; }
        else { ApiKeyBox.Password = ApiKeyPlainBox.Text; ApiKeyPlainBox.Visibility = Visibility.Collapsed; ApiKeyBox.Visibility = Visibility.Visible; }
    }
    private string GetKey() => (ShowKeyCheck.IsChecked == true ? ApiKeyPlainBox.Text : ApiKeyBox.Password).Trim();
    private void SetKey(string key) { ApiKeyBox.Password = key; ApiKeyPlainBox.Text = key; }
    private void ModelInput_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Open from the editable area as well as the arrow, without consuming text editing input.
        if (sender is FrameworkElement { TemplatedParent: ComboBox combo } && combo.HasItems)
            combo.IsDropDownOpen = true;
    }

    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        var url = BaseUrlBox.Text.Trim();
        var key = GetKey();
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
        { Report("获取模型前，请先填写 Base URL 和 API Key。", true); return; }
        _fetch?.Cancel();
        using var request = new CancellationTokenSource();
        _fetch = request;
        FetchButton.IsEnabled = false;
        FetchButton.Content = "获取中…";
        try
        {
            var models = await _modelsApi.FetchModelsAsync(url, key, request.Token);
            if (request.IsCancellationRequested || BaseUrlBox.Text.Trim() != url || GetKey() != key) return;
            var current = ModelCombo.Text;
            ModelCombo.Items.Clear();
            foreach (var model in models) ModelCombo.Items.Add(model);
            ModelCombo.Text = string.IsNullOrWhiteSpace(current) ? models.FirstOrDefault() ?? "" : current;
            Report(models.Count == 0 ? "接口未返回模型，可直接输入模型名称。" : $"已获取 {models.Count} 个模型，也可手动输入。");
        }
        catch (OperationCanceledException) { if (!request.IsCancellationRequested) Report("获取模型超时，请重试。", true); }
        catch (Exception ex) { if (!request.IsCancellationRequested) Report(ex.Message.Replace(key, "***"), true); }
        finally
        {
            if (_fetch == request) { _fetch = null; FetchButton.Content = "获取模型列表"; FetchButton.IsEnabled = true; }
        }
    }
    private void Run(Action action) { try { action(); } catch (Exception ex) { Report(ex.Message, true); } }
    private void Report(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(error ? "#B91C1C" : "#52525B"));
        StatusBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(error ? "#FEF2F2" : "#F5F5F7"));
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        RememberDraft();
        var changed = new[] { _codexSession, _claudeSession }.Any(session => session.Drafts.Any(item =>
        {
            var original = FromProfile(session.Cards.FirstOrDefault(p => p.Profile.Id == item.Key)?.Profile);
            var draft = item.Value;
            return draft.Name != original.Name || draft.Url != original.Url || draft.Key != original.Key || draft.Model != original.Model ||
                draft.ConfigText != original.ConfigText || (draft.SourceMode && draft.SourceText != draft.PreviewBaseline);
        }));
        if (changed && MessageBox.Show(this, "有尚未保存的配置修改，仍要关闭吗？", "未保存的修改", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            e.Cancel = true;
    }
}
