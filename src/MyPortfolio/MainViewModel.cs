using System.Collections.ObjectModel;
using System.Windows.Input;
using MyPortfolio.Android.ViewModels;
using MyPortfolio.Chrome.ViewModels;
using MyPortfolio.Common;
using MyPortfolio.Desktop.ViewModels;
using MyPortfolio.Themes;

namespace MyPortfolio;

/// <summary>
/// Top-level VM for the unified portfolio. Owns the shared services (settings,
/// HTTP, log, Octokit factory) and the three tab VMs. Settings drawer reads /
/// writes the single AppSettings instance shared across tabs.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly HttpDownloader _http;
    private readonly GitHubClientFactory _factory;
    private readonly LogSink _log;
    private AppSettings _settings;
    private CancellationTokenSource? _refreshAllCts;
    private bool _isRefreshingAll;

    private string _statusText = "Ready.";
    private string _githubUserInput;
    private string _githubTokenInput;
    private string _extraOwnerEntry = string.Empty;

    public DesktopTabViewModel DesktopTab { get; }
    public ChromeTabViewModel ChromeTab { get; }
    public AndroidTabViewModel AndroidTab { get; }

    public LogSink Log => _log;

    public ICommand SaveSettingsCommand { get; }
    public ICommand SaveAndRefreshAllCommand { get; }
    public ICommand RefreshAllCommand { get; }
    public ICommand CancelRefreshAllCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand ClearHiddenReposCommand { get; }
    public ICommand AddExtraOwnerCommand { get; }
    public ICommand RemoveExtraOwnerCommand { get; }
    public ICommand ClearExtraOwnersCommand { get; }

    public ObservableCollection<string> ExtraOwners { get; } = new();
    public IReadOnlyList<string> ThemeOptions => ThemeService.ThemeFlavors;
    public IReadOnlyList<string> AccentOptions => ThemeService.AccentNames;

    public MainViewModel()
    {
        _settingsService = new SettingsService();
        _http = new HttpDownloader();
        _factory = new GitHubClientFactory();
        _log = new LogSink();
        _settings = _settingsService.Load();
        ThemeService.Apply(_settings);

        _githubUserInput = _settings.GitHubUser;
        _githubTokenInput = _settings.GitHubToken ?? string.Empty;
        foreach (var owner in _settings.ExtraOwners.Distinct(StringComparer.OrdinalIgnoreCase))
            ExtraOwners.Add(owner);

        DesktopTab = new DesktopTabViewModel(_settingsService, _factory, _http, _log, () => _settings);
        ChromeTab = new ChromeTabViewModel(_settingsService, _factory, _http, _log, () => _settings);
        AndroidTab = new AndroidTabViewModel(_settingsService, _factory, _http, _log, () => _settings);

        SaveSettingsCommand = new RelayCommand(_ => SaveSettings());
        SaveAndRefreshAllCommand = new AsyncRelayCommand(async _ =>
        {
            if (SaveSettings()) await RefreshAllAsync();
        }, _ => !IsRefreshingAll);
        RefreshAllCommand = new AsyncRelayCommand(_ => RefreshAllAsync(), _ => !IsRefreshingAll);
        CancelRefreshAllCommand = new RelayCommand(_ => CancelRefreshAll(), _ => CanCancelRefreshAll);
        ClearLogCommand = new RelayCommand(_ => _log.Lines.Clear());
        ClearHiddenReposCommand = new AsyncRelayCommand(async _ =>
        {
            if (ClearHiddenRepos()) await RefreshAllAsync();
        }, _ => HasHiddenRepos && !IsRefreshingAll);
        AddExtraOwnerCommand = new RelayCommand(_ => AddExtraOwnersFromEntry(), _ => CanAddExtraOwner);
        RemoveExtraOwnerCommand = new RelayCommand(RemoveExtraOwner);
        ClearExtraOwnersCommand = new RelayCommand(_ => ClearExtraOwners(), _ => HasExtraOwners);

        var version = typeof(MainViewModel).Assembly.GetName().Version;
        _log.Append("Shell", $"MyPortfolio v{version} ready.");
        _log.Append("Shell", $"Run Refresh on each tab to discover apps for '{_settings.GitHubUser}'.");
    }

    public AppSettings CurrentSettings => _settings;

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string GitHubUserInput
    {
        get => _githubUserInput;
        set => SetField(ref _githubUserInput, value);
    }

    public string GitHubTokenInput
    {
        get => _githubTokenInput;
        set => SetField(ref _githubTokenInput, value);
    }

    public string ExtraOwnerEntry
    {
        get => _extraOwnerEntry;
        set
        {
            if (SetField(ref _extraOwnerEntry, value))
            {
                OnPropertyChanged(nameof(CanAddExtraOwner));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasExtraOwners => ExtraOwners.Count > 0;
    public bool CanAddExtraOwner => !string.IsNullOrWhiteSpace(ExtraOwnerEntry);
    public string ExtraOwnerSummary => HasExtraOwners
        ? $"{ExtraOwners.Count} extra owner(s) included in discovery."
        : "No extra owners added.";

    public bool IsRefreshingAll
    {
        get => _isRefreshingAll;
        private set
        {
            if (SetField(ref _isRefreshingAll, value))
            {
                OnPropertyChanged(nameof(CanCancelRefreshAll));
                OnPropertyChanged(nameof(RefreshAllButtonLabel));
                OnPropertyChanged(nameof(CancelRefreshAllButtonLabel));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool CanCancelRefreshAll => IsRefreshingAll && _refreshAllCts?.IsCancellationRequested == false;
    public string RefreshAllButtonLabel => IsRefreshingAll ? "Refreshing..." : "Refresh all";
    public string CancelRefreshAllButtonLabel => _refreshAllCts?.IsCancellationRequested == true ? "Canceling..." : "Cancel";

    public bool RefreshOnLaunch
    {
        get => _settings.RefreshOnLaunch;
        set { if (_settings.RefreshOnLaunch != value) { _settings.RefreshOnLaunch = value; OnPropertyChanged(); } }
    }

    public string ThemeFlavor
    {
        get => ThemeService.NormalizeTheme(_settings.ThemeFlavor);
        set
        {
            var normalized = ThemeService.NormalizeTheme(value);
            if (_settings.ThemeFlavor == normalized) return;
            _settings.ThemeFlavor = normalized;
            ApplyThemeSelection();
            OnPropertyChanged();
        }
    }

    public string AccentColor
    {
        get => ThemeService.NormalizeAccent(_settings.AccentColor);
        set
        {
            var normalized = ThemeService.NormalizeAccent(value);
            if (_settings.AccentColor == normalized) return;
            _settings.AccentColor = normalized;
            ApplyThemeSelection();
            OnPropertyChanged();
        }
    }

    public string ThemeSummary => $"{ThemeFlavor} theme with {AccentColor.ToLowerInvariant()} accent.";

    // Per-tab knobs surface as bound properties on the shared settings drawer.
    public bool DesktopUseTopicFilter
    {
        get => _settings.DesktopUseTopicFilter;
        set { if (_settings.DesktopUseTopicFilter != value) { _settings.DesktopUseTopicFilter = value; OnPropertyChanged(); } }
    }

    public string DesktopTopicFilter
    {
        get => _settings.DesktopTopicFilter;
        set { if (_settings.DesktopTopicFilter != value) { _settings.DesktopTopicFilter = value; OnPropertyChanged(); } }
    }

    public bool DesktopVerifyHashSidecar
    {
        get => _settings.DesktopVerifyHashSidecar;
        set { if (_settings.DesktopVerifyHashSidecar != value) { _settings.DesktopVerifyHashSidecar = value; OnPropertyChanged(); } }
    }

    public string DesktopInstallRootOverride
    {
        get => _settings.DesktopInstallRootOverride ?? string.Empty;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_settings.DesktopInstallRootOverride != trimmed) { _settings.DesktopInstallRootOverride = trimmed; OnPropertyChanged(); }
        }
    }

    public bool ChromeUseTopicFilter
    {
        get => _settings.ChromeUseTopicFilter;
        set { if (_settings.ChromeUseTopicFilter != value) { _settings.ChromeUseTopicFilter = value; OnPropertyChanged(); } }
    }

    public string ChromeTopicFilter
    {
        get => _settings.ChromeTopicFilter;
        set { if (_settings.ChromeTopicFilter != value) { _settings.ChromeTopicFilter = value; OnPropertyChanged(); } }
    }

    public bool AndroidUseTopicFilter
    {
        get => _settings.AndroidUseTopicFilter;
        set { if (_settings.AndroidUseTopicFilter != value) { _settings.AndroidUseTopicFilter = value; OnPropertyChanged(); } }
    }

    public string AndroidTopicFilter
    {
        get => _settings.AndroidTopicFilter;
        set { if (_settings.AndroidTopicFilter != value) { _settings.AndroidTopicFilter = value; OnPropertyChanged(); } }
    }

    public bool AndroidVerifyHashSidecar
    {
        get => _settings.AndroidVerifyHashSidecar;
        set { if (_settings.AndroidVerifyHashSidecar != value) { _settings.AndroidVerifyHashSidecar = value; OnPropertyChanged(); } }
    }

    public string AndroidDownloadFolderOverride
    {
        get => _settings.AndroidDownloadFolderOverride ?? string.Empty;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_settings.AndroidDownloadFolderOverride != trimmed) { _settings.AndroidDownloadFolderOverride = trimmed; OnPropertyChanged(); }
        }
    }

    public int HiddenRepoCount => _settings.HiddenRepos.Count;
    public bool HasHiddenRepos => HiddenRepoCount > 0;
    public string HiddenRepoSummary => HiddenRepoCount == 0
        ? "No repositories are hidden from discovery."
        : $"{HiddenRepoCount} hidden repo(s) excluded across all tabs.";

    private bool SaveSettings()
    {
        var user = GitHubUserInput.Trim();
        if (string.IsNullOrWhiteSpace(user))
        {
            StatusText = "Enter a GitHub user or organization before saving.";
            _log.Append("Shell", "! Settings were not saved: GitHub user / org is required.");
            return false;
        }

        _settings.GitHubUser = user;
        _settings.GitHubToken = string.IsNullOrWhiteSpace(GitHubTokenInput) ? null : GitHubTokenInput.Trim();
        ThemeService.Apply(_settings);
        AddExtraOwnersFromEntry(showStatus: false);
        RemovePrimaryOwnerFromExtras(user);
        _settings.ExtraOwners = ExtraOwners.ToList();

        _settingsService.Save(_settings);
        StatusText = "Settings saved locally.";
        _log.Append("Shell", "Settings saved locally.");
        RefreshExtraOwnerState();
        OnPropertyChanged(nameof(DesktopTopicFilter));
        OnPropertyChanged(nameof(ChromeTopicFilter));
        OnPropertyChanged(nameof(AndroidTopicFilter));
        OnPropertyChanged(nameof(ThemeFlavor));
        OnPropertyChanged(nameof(AccentColor));
        OnPropertyChanged(nameof(ThemeSummary));
        return true;
    }

    private async Task RefreshAllAsync()
    {
        if (IsRefreshingAll) return;
        using var cts = new CancellationTokenSource();
        _refreshAllCts = cts;
        IsRefreshingAll = true;
        StatusText = "Refreshing all tabs...";
        _log.Append("Shell", "Refresh all started.");
        try
        {
            StatusText = "Refreshing desktop apps...";
            await DesktopTab.RefreshAsync(cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            StatusText = "Refreshing Chrome extensions...";
            await ChromeTab.RefreshAsync(cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            StatusText = "Refreshing Android APKs...";
            await AndroidTab.RefreshAsync(cts.Token);
            StatusText = "All tabs refreshed.";
            _log.Append("Shell", "Refresh all completed.");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            StatusText = "Refresh canceled.";
            _log.Append("Shell", "Refresh all canceled. Partial results remain visible.");
        }
        finally
        {
            _refreshAllCts = null;
            IsRefreshingAll = false;
            OnPropertyChanged(nameof(CanCancelRefreshAll));
            OnPropertyChanged(nameof(CancelRefreshAllButtonLabel));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void CancelRefreshAll()
    {
        if (_refreshAllCts is null || _refreshAllCts.IsCancellationRequested) return;
        _refreshAllCts.Cancel();
        StatusText = "Canceling refresh...";
        _log.Append("Shell", "Cancel requested for refresh all.");
        OnPropertyChanged(nameof(CanCancelRefreshAll));
        OnPropertyChanged(nameof(CancelRefreshAllButtonLabel));
        CommandManager.InvalidateRequerySuggested();
    }

    public async Task RefreshOnLaunchIfEnabledAsync()
    {
        if (!_settings.RefreshOnLaunch) return;
        _log.Append("Shell", "Refresh on launch is enabled; refreshing all tabs.");
        try { await RefreshAllAsync(); }
        catch (Exception ex)
        {
            StatusText = "Refresh on launch failed.";
            _log.Append("Shell", $"! Refresh on launch failed: {ex.Message}");
        }
    }

    private bool ClearHiddenRepos()
    {
        if (_settings.HiddenRepos.Count == 0) return false;
        var count = _settings.HiddenRepos.Count;
        _settings.HiddenRepos.Clear();
        _settingsService.Save(_settings);
        OnPropertyChanged(nameof(HiddenRepoCount));
        OnPropertyChanged(nameof(HasHiddenRepos));
        OnPropertyChanged(nameof(HiddenRepoSummary));
        StatusText = $"Restored {count} hidden repo(s).";
        _log.Append("Shell", $"Restored {count} hidden repo(s) to discovery.");
        return true;
    }

    private void AddExtraOwnersFromEntry(bool showStatus = true)
    {
        var parsed = ParseExtraOwners(ExtraOwnerEntry).ToList();
        ExtraOwnerEntry = string.Empty;
        if (parsed.Count == 0)
        {
            if (showStatus) StatusText = "Enter an owner before adding.";
            return;
        }

        var added = 0;
        foreach (var owner in parsed)
        {
            if (string.Equals(owner, GitHubUserInput.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (ExtraOwners.Contains(owner, StringComparer.OrdinalIgnoreCase)) continue;
            ExtraOwners.Add(owner);
            added++;
        }

        RefreshExtraOwnerState();
        if (!showStatus) return;
        StatusText = added == 0
            ? "No new owners added."
            : $"Added {added} owner(s) to discovery.";
    }

    private static IEnumerable<string> ParseExtraOwners(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) yield break;
        foreach (var raw in input.Split(new[] { ',', ';', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var owner = NormalizeOwner(raw);
            if (!string.IsNullOrWhiteSpace(owner)) yield return owner;
        }
    }

    private static string NormalizeOwner(string owner)
    {
        var value = owner.Trim().TrimStart('@').Trim('/');
        const string githubPrefix = "github.com/";
        var githubIndex = value.IndexOf(githubPrefix, StringComparison.OrdinalIgnoreCase);
        if (githubIndex >= 0)
            value = value[(githubIndex + githubPrefix.Length)..];
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            value = value["https://".Length..];
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            value = value["http://".Length..];
        var slash = value.IndexOf('/');
        return slash >= 0 ? value[..slash] : value;
    }

    private void RemoveExtraOwner(object? owner)
    {
        if (owner is not string value) return;
        ExtraOwners.Remove(value);
        RefreshExtraOwnerState();
        StatusText = $"Removed {value} from discovery.";
    }

    private void ClearExtraOwners()
    {
        if (ExtraOwners.Count == 0) return;
        var count = ExtraOwners.Count;
        ExtraOwners.Clear();
        RefreshExtraOwnerState();
        StatusText = $"Removed {count} extra owner(s).";
    }

    private void RemovePrimaryOwnerFromExtras(string primaryOwner)
    {
        var duplicates = ExtraOwners
            .Where(owner => string.Equals(owner, primaryOwner, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var duplicate in duplicates)
            ExtraOwners.Remove(duplicate);
    }

    private void RefreshExtraOwnerState()
    {
        OnPropertyChanged(nameof(HasExtraOwners));
        OnPropertyChanged(nameof(ExtraOwnerSummary));
        OnPropertyChanged(nameof(CanAddExtraOwner));
        CommandManager.InvalidateRequerySuggested();
    }

    private void ApplyThemeSelection()
    {
        ThemeService.Apply(_settings);
        OnPropertyChanged(nameof(ThemeSummary));
    }
}
