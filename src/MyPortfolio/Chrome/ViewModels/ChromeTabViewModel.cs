using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using MyPortfolio.Common;
using MyPortfolio.Chrome.Models;
using MyPortfolio.Chrome.Services;

namespace MyPortfolio.Chrome.ViewModels;

public sealed class ChromeTabViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly ChromeGitHubService _github;
    private readonly ExtensionService _extensions;
    private readonly BrowserLauncher _launcher;
    private readonly HttpDownloader _http;
    private readonly LogSink _log;
    private readonly Func<AppSettings> _settingsAccessor;
    private bool _busy;
    private BrowserInfo? _selectedBrowser;
    private string _searchText = string.Empty;
    private bool _showInstalledOnly;

    public ObservableCollection<ExtensionCardViewModel> Extensions { get; } = new();
    public ICollectionView ExtensionsView { get; }
    public ObservableCollection<BrowserInfo> Browsers { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand LaunchBrowserCommand { get; }
    public ICommand OpenInstallDirCommand { get; }

    public ChromeTabViewModel(
        SettingsService settingsService,
        GitHubClientFactory factory,
        HttpDownloader http,
        LogSink log,
        Func<AppSettings> settingsAccessor)
    {
        _settingsService = settingsService;
        _http = http;
        _log = log;
        _settingsAccessor = settingsAccessor;
        _github = new ChromeGitHubService(factory, http);
        _extensions = new ExtensionService(settingsService, http);
        _launcher = new BrowserLauncher(_extensions);

        ExtensionsView = CollectionViewSource.GetDefaultView(Extensions);
        ExtensionsView.Filter = FilterExtension;
        ExtensionsView.SortDescriptions.Add(new SortDescription(nameof(ExtensionCardViewModel.Title), ListSortDirection.Ascending));

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !Busy);
        LaunchBrowserCommand = new RelayCommand(_ => LaunchBrowser(), _ => CanLaunchBrowser);
        OpenInstallDirCommand = new RelayCommand(_ => OpenInstallDir());

        DetectBrowsers();
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetField(ref _busy, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(RefreshButtonLabel));
                OnPropertyChanged(nameof(CanLaunchBrowser));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (SetField(ref _searchText, value)) RefreshView(); }
    }

    public bool ShowInstalledOnly
    {
        get => _showInstalledOnly;
        set { if (SetField(ref _showInstalledOnly, value)) RefreshView(); }
    }

    public BrowserInfo? SelectedBrowser
    {
        get => _selectedBrowser;
        set
        {
            if (SetField(ref _selectedBrowser, value))
            {
                if (value != null)
                {
                    var cfg = _settingsAccessor();
                    cfg.PreferredBrowserPath = value.ExecutablePath;
                    _settingsService.Save(cfg);
                }
                OnPropertyChanged(nameof(CanLaunchBrowser));
                OnPropertyChanged(nameof(BrowserSummary));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public int InstalledCount => _extensions.Installed.Count;
    public int AvailableCount => Extensions.Count;
    public int VisibleCount => ExtensionsView.Cast<object>().Count();
    public bool HasInstalledExtensions => InstalledCount > 0;
    public bool CanLaunchBrowser => !Busy && SelectedBrowser != null && HasInstalledExtensions;
    public string RefreshButtonLabel => Busy ? "Refreshing..." : "Refresh extensions";
    public string BrowserSummary => Browsers.Count == 0
        ? "No supported Chromium browser detected."
        : $"{Browsers.Count} browser(s) detected.";
    public bool ShowEmptyState => !Busy && VisibleCount == 0;
    public string EmptyStateTitle => AvailableCount == 0
        ? "No extensions discovered yet"
        : ShowInstalledOnly ? "No installed extensions match this view"
        : !string.IsNullOrWhiteSpace(SearchText) ? "No matching extensions"
        : "Nothing to show";
    public string EmptyStateMessage => AvailableCount == 0
        ? "Refresh to scan the configured GitHub account for repos with a manifest.json or release ZIP/CRX."
        : ShowInstalledOnly ? "Clear the installed-only filter or install an extension from the full catalog."
        : !string.IsNullOrWhiteSpace(SearchText) ? "Try a different extension name, repository, or description keyword."
        : "Adjust the filters or refresh the catalog.";

    private bool FilterExtension(object obj)
    {
        if (obj is not ExtensionCardViewModel vm) return false;
        if (ShowInstalledOnly && !vm.IsInstalled) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var q = SearchText.Trim();
        return vm.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.Repo.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    public async Task RefreshAsync()
    {
        Busy = true;
        try
        {
            _log.Append("Chrome", "Discovering Chrome extensions...");
            var infos = await _github.DiscoverAsync(_settingsAccessor(), _log.AsProgress("Chrome"));
            Extensions.Clear();
            foreach (var info in infos)
            {
                Extensions.Add(new ExtensionCardViewModel(
                    info, _extensions, _http, _settingsService,
                    s => _log.Append("Chrome", s),
                    RefreshAfterChange,
                    HideExtension));
            }
            RefreshView();
            RefreshMetrics();
            _log.Append("Chrome", $"Found {Extensions.Count} extension(s) — {InstalledCount} installed.");
        }
        catch (Exception ex) { _log.Append("Chrome", $"! Refresh failed: {ex.Message}"); }
        finally { Busy = false; }
    }

    private void RefreshAfterChange()
    {
        RefreshView();
        RefreshMetrics();
        CommandManager.InvalidateRequerySuggested();
    }

    private void DetectBrowsers()
    {
        Browsers.Clear();
        foreach (var b in _launcher.Detect()) Browsers.Add(b);
        var cfg = _settingsAccessor();
        if (!string.IsNullOrEmpty(cfg.PreferredBrowserPath))
            SelectedBrowser = Browsers.FirstOrDefault(b =>
                string.Equals(b.ExecutablePath, cfg.PreferredBrowserPath, StringComparison.OrdinalIgnoreCase));
        SelectedBrowser ??= Browsers.FirstOrDefault();
        _log.Append("Chrome", Browsers.Count == 0
            ? "! No supported browsers detected."
            : $"Detected browsers: {string.Join(", ", Browsers.Select(b => b.DisplayName))}");
        OnPropertyChanged(nameof(BrowserSummary));
        OnPropertyChanged(nameof(CanLaunchBrowser));
    }

    private void HideExtension(ExtensionCardViewModel extension)
    {
        var confirm = MessageBox.Show(
            $"Hide {extension.Repo} from future discovery?",
            "Hide repository", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var cfg = _settingsAccessor();
        if (!cfg.HiddenRepos.Contains(extension.Repo, StringComparer.OrdinalIgnoreCase))
        {
            cfg.HiddenRepos.Add(extension.Repo);
            cfg.HiddenRepos.Sort(StringComparer.OrdinalIgnoreCase);
            _settingsService.Save(cfg);
        }
        Extensions.Remove(extension);
        RefreshView();
        RefreshMetrics();
        _log.Append("Chrome", $"Hidden {extension.Repo} from discovery.");
    }

    private void LaunchBrowser()
    {
        if (SelectedBrowser is null) return;
        var set = _extensions.Installed.ToList();
        if (set.Count == 0)
        {
            _log.Append("Chrome", "No extensions installed yet — install one before launching.");
            return;
        }
        try
        {
            _launcher.Launch(SelectedBrowser, set);
            _log.Append("Chrome", $"Launched {SelectedBrowser.DisplayName} with {set.Count} extension(s) loaded.");
        }
        catch (Exception ex) { _log.Append("Chrome", $"! Launch failed: {ex.Message}"); }
    }

    private void OpenInstallDir()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settingsService.ChromeExtensionsRoot}\"") { UseShellExecute = true }); }
        catch (Exception ex) { _log.Append("Chrome", $"! {ex.Message}"); }
    }

    private void RefreshView()
    {
        ExtensionsView.Refresh();
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateMessage));
    }

    private void RefreshMetrics()
    {
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(AvailableCount));
        OnPropertyChanged(nameof(HasInstalledExtensions));
        OnPropertyChanged(nameof(CanLaunchBrowser));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateMessage));
    }
}
