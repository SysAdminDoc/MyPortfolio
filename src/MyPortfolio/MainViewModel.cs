using System.Windows.Input;
using MyPortfolio.Android.ViewModels;
using MyPortfolio.Chrome.ViewModels;
using MyPortfolio.Common;
using MyPortfolio.Desktop.ViewModels;

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

    private string _statusText = "Ready.";
    private string _githubUserInput;
    private string _githubTokenInput;
    private string _extraOwnersInput;

    public DesktopTabViewModel DesktopTab { get; }
    public ChromeTabViewModel ChromeTab { get; }
    public AndroidTabViewModel AndroidTab { get; }

    public LogSink Log => _log;

    public ICommand SaveSettingsCommand { get; }
    public ICommand SaveAndRefreshAllCommand { get; }
    public ICommand RefreshAllCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand ClearHiddenReposCommand { get; }

    public MainViewModel()
    {
        _settingsService = new SettingsService();
        _http = new HttpDownloader();
        _factory = new GitHubClientFactory();
        _log = new LogSink();
        _settings = _settingsService.Load();

        _githubUserInput = _settings.GitHubUser;
        _githubTokenInput = _settings.GitHubToken ?? string.Empty;
        _extraOwnersInput = string.Join(", ", _settings.ExtraOwners);

        DesktopTab = new DesktopTabViewModel(_settingsService, _factory, _http, _log, () => _settings);
        ChromeTab = new ChromeTabViewModel(_settingsService, _factory, _http, _log, () => _settings);
        AndroidTab = new AndroidTabViewModel(_settingsService, _factory, _http, _log, () => _settings);

        SaveSettingsCommand = new RelayCommand(_ => SaveSettings());
        SaveAndRefreshAllCommand = new AsyncRelayCommand(async _ =>
        {
            if (SaveSettings()) await RefreshAllAsync();
        });
        RefreshAllCommand = new AsyncRelayCommand(_ => RefreshAllAsync());
        ClearLogCommand = new RelayCommand(_ => _log.Lines.Clear());
        ClearHiddenReposCommand = new AsyncRelayCommand(async _ =>
        {
            if (ClearHiddenRepos()) await RefreshAllAsync();
        }, _ => HasHiddenRepos);

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

    public string ExtraOwnersInput
    {
        get => _extraOwnersInput;
        set => SetField(ref _extraOwnersInput, value);
    }

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
        _settings.ExtraOwners = (ExtraOwnersInput ?? string.Empty)
            .Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.Equals(s, _settings.GitHubUser, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _settingsService.Save(_settings);
        StatusText = "Settings saved locally.";
        _log.Append("Shell", "Settings saved locally.");
        OnPropertyChanged(nameof(DesktopTopicFilter));
        OnPropertyChanged(nameof(ChromeTopicFilter));
        OnPropertyChanged(nameof(AndroidTopicFilter));
        return true;
    }

    private async Task RefreshAllAsync()
    {
        StatusText = "Refreshing all tabs...";
        await Task.WhenAll(DesktopTab.RefreshAsync(), ChromeTab.RefreshAsync(), AndroidTab.RefreshAsync());
        StatusText = "All tabs refreshed.";
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
}
