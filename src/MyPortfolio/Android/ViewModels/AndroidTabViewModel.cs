using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Input;
using MyPortfolio.Android.Services;
using MyPortfolio.Common;

namespace MyPortfolio.Android.ViewModels;

public sealed class AndroidTabViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly AndroidGitHubService _github;
    private readonly ApkDownloadService _downloader;
    private readonly HttpDownloader _http;
    private readonly LogSink _log;
    private readonly Func<AppSettings> _settingsAccessor;
    private bool _busy;
    private string _searchText = string.Empty;
    private bool _showDownloadedOnly;

    public ObservableCollection<AndroidAppCardViewModel> Apps { get; } = new();
    public ICollectionView AppsView { get; }

    public ICommand RefreshCommand { get; }
    public ICommand OpenDownloadFolderCommand { get; }

    public AndroidTabViewModel(
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
        _github = new AndroidGitHubService(factory);
        _downloader = new ApkDownloadService(settingsService, http);

        AppsView = CollectionViewSource.GetDefaultView(Apps);
        AppsView.Filter = FilterApp;
        AppsView.SortDescriptions.Add(new SortDescription(nameof(AndroidAppCardViewModel.Title), ListSortDirection.Ascending));

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !Busy);
        OpenDownloadFolderCommand = new RelayCommand(_ => OpenDownloadFolder());
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

    public bool ShowDownloadedOnly
    {
        get => _showDownloadedOnly;
        set { if (SetField(ref _showDownloadedOnly, value)) RefreshView(); }
    }

    public int DownloadedCount => _downloader.Downloaded.Count;
    public int AvailableCount => Apps.Count;
    public int VisibleCount => AppsView.Cast<object>().Count();
    public string RefreshButtonLabel => Busy ? "Refreshing..." : "Refresh Android apps";
    public string LastRefreshText => Format.LastRefresh(_settingsAccessor().AndroidLastRefreshUtc);
    public bool ShowEmptyState => !Busy && VisibleCount == 0;
    public string EmptyStateTitle => AvailableCount == 0
        ? "No Android apps discovered yet"
        : ShowDownloadedOnly ? "Nothing downloaded in this view"
        : !string.IsNullOrWhiteSpace(SearchText) ? "No matching Android apps"
        : "Nothing to show";
    public string EmptyStateMessage => AvailableCount == 0
        ? "Refresh to scan the configured GitHub account for repos with an .apk in their latest release."
        : ShowDownloadedOnly ? "Clear the downloaded-only filter or download an APK from the full catalog."
        : !string.IsNullOrWhiteSpace(SearchText) ? "Try a different app name, repository, or description keyword."
        : "Adjust the filters or refresh the catalog.";
    public string DownloadFolder => _settingsService.AndroidDownloadRoot(_settingsAccessor());

    private bool FilterApp(object obj)
    {
        if (obj is not AndroidAppCardViewModel vm) return false;
        if (ShowDownloadedOnly && !vm.IsDownloaded) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var q = SearchText.Trim();
        return vm.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.Repo.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.PackageName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.ApkVersionCode.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.ApkVersionName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    public async Task RefreshAsync()
    {
        Busy = true;
        try
        {
            _log.Append("Android", "Discovering Android APK releases...");
            var infos = await _github.DiscoverAsync(_settingsAccessor(), _log.AsProgress("Android"));
            Apps.Clear();
            foreach (var info in infos)
            {
                Apps.Add(new AndroidAppCardViewModel(
                    info, _downloader, _http, _settingsService, _settingsAccessor,
                    s => _log.Append("Android", s),
                    RefreshAfterChange));
            }
            RefreshView();
            RefreshMetrics();
            MarkRefreshed();
            _log.Append("Android", $"Found {Apps.Count} Android app(s) — {DownloadedCount} downloaded.");
        }
        catch (Exception ex) { _log.Append("Android", $"! Refresh failed: {ex.Message}"); }
        finally { Busy = false; }
    }

    private void RefreshAfterChange()
    {
        RefreshView();
        RefreshMetrics();
        CommandManager.InvalidateRequerySuggested();
    }

    private void OpenDownloadFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DownloadFolder}\"") { UseShellExecute = true }); }
        catch (Exception ex) { _log.Append("Android", $"! {ex.Message}"); }
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
        OnPropertyChanged(nameof(DownloadedCount));
        OnPropertyChanged(nameof(AvailableCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(LastRefreshText));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateMessage));
        OnPropertyChanged(nameof(DownloadFolder));
    }

    private void MarkRefreshed()
    {
        var cfg = _settingsAccessor();
        cfg.AndroidLastRefreshUtc = DateTimeOffset.UtcNow;
        _settingsService.Save(cfg);
        OnPropertyChanged(nameof(LastRefreshText));
    }
}
