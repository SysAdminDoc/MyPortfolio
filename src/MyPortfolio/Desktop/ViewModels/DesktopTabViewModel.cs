using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Input;
using MyPortfolio.Common;
using MyPortfolio.Desktop.Services;

namespace MyPortfolio.Desktop.ViewModels;

public sealed class DesktopTabViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly DesktopGitHubService _github;
    private readonly InstallService _installer;
    private readonly HttpDownloader _http;
    private readonly LogSink _log;
    private readonly Func<AppSettings> _settingsAccessor;
    private bool _busy;
    private string _searchText = string.Empty;
    private bool _showInstalledOnly;

    public ObservableCollection<AppCardViewModel> Apps { get; } = new();
    public ICollectionView AppsView { get; }

    public ICommand RefreshCommand { get; }
    public ICommand OpenInstallDirCommand { get; }

    public DesktopTabViewModel(
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
        _github = new DesktopGitHubService(factory, http);
        _installer = new InstallService(settingsService, http);

        AppsView = CollectionViewSource.GetDefaultView(Apps);
        AppsView.Filter = FilterApp;
        AppsView.SortDescriptions.Add(new SortDescription(nameof(AppCardViewModel.Title), ListSortDirection.Ascending));

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !Busy);
        OpenInstallDirCommand = new RelayCommand(_ => OpenInstallDir());
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

    public int InstalledCount => _installer.Installed.Count;
    public int AvailableCount => Apps.Count;
    public int VisibleCount => AppsView.Cast<object>().Count();
    public string RefreshButtonLabel => Busy ? "Refreshing..." : "Refresh desktop apps";
    public string LastRefreshText => Format.LastRefresh(_settingsAccessor().DesktopLastRefreshUtc);
    public bool ShowEmptyState => !Busy && VisibleCount == 0;
    public string EmptyStateTitle => AvailableCount == 0
        ? "No desktop apps discovered yet"
        : ShowInstalledOnly ? "Nothing installed in this view"
        : !string.IsNullOrWhiteSpace(SearchText) ? "No matching apps"
        : "Nothing to show";
    public string EmptyStateMessage => AvailableCount == 0
        ? "Refresh to scan the configured GitHub account for repos with an MSI / EXE / ZIP release asset."
        : ShowInstalledOnly ? "Clear the installed-only filter or install an app from the full catalog."
        : !string.IsNullOrWhiteSpace(SearchText) ? "Try a different app name, repository, or description keyword."
        : "Adjust the filters or refresh the catalog.";

    private bool FilterApp(object obj)
    {
        if (obj is not AppCardViewModel vm) return false;
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
            _log.Append("Desktop", "Discovering desktop apps...");
            var infos = await _github.DiscoverAsync(_settingsAccessor(), _log.AsProgress("Desktop"));
            Apps.Clear();
            foreach (var info in infos)
            {
                Apps.Add(new AppCardViewModel(
                    info, _installer, _http, _settingsService, _settingsAccessor,
                    s => _log.Append("Desktop", s),
                    RefreshAfterChange));
            }
            RefreshView();
            RefreshMetrics();
            MarkRefreshed();
            _log.Append("Desktop", $"Found {Apps.Count} app(s) — {InstalledCount} installed.");
        }
        catch (Exception ex)
        {
            _log.Append("Desktop", $"! Refresh failed: {ex.Message}");
        }
        finally { Busy = false; }
    }

    private void RefreshAfterChange()
    {
        RefreshView();
        RefreshMetrics();
        CommandManager.InvalidateRequerySuggested();
    }

    private void OpenInstallDir()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settingsService.DesktopAppsRoot(_settingsAccessor())}\"") { UseShellExecute = true }); }
        catch (Exception ex) { _log.Append("Desktop", $"! {ex.Message}"); }
    }

    private void RefreshView()
    {
        AppsView.Refresh();
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateMessage));
    }

    private void RefreshMetrics()
    {
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(AvailableCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(LastRefreshText));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateMessage));
    }

    private void MarkRefreshed()
    {
        var cfg = _settingsAccessor();
        cfg.DesktopLastRefreshUtc = DateTimeOffset.UtcNow;
        _settingsService.Save(cfg);
        OnPropertyChanged(nameof(LastRefreshText));
    }
}
