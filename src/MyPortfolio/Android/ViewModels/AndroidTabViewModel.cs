using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
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
    private bool _showDiscoveryDetails;
    private DiscoveryDiagnostics _diagnostics = new();
    private DiscoveryProgress? _refreshProgress;
    private int _refreshProgressVersion;

    public ObservableCollection<AndroidAppCardViewModel> Apps { get; } = new();
    public ICollectionView AppsView { get; }

    public ICommand RefreshCommand { get; }
    public ICommand OpenDownloadFolderCommand { get; }
    public ICommand ToggleDiscoveryDetailsCommand { get; }
    public ICommand CopyDiagnosticsCommand { get; }

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
        ToggleDiscoveryDetailsCommand = new RelayCommand(_ => ToggleDiscoveryDetails(), _ => HasOwnerDiagnostics);
        CopyDiagnosticsCommand = new RelayCommand(_ => CopyDiagnostics(), _ => HasDiscoveryDiagnostics);
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
                OnPropertyChanged(nameof(HasRefreshProgress));
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
    public bool HasDiscoveryDiagnostics => _diagnostics.HasDetails;
    public string DiscoverySummary => _diagnostics.Summary;
    public bool HasDiscoveryWarning => _diagnostics.HasWarnings;
    public string DiscoveryWarningText => _diagnostics.WarningText;
    public bool HasRateLimitText => !string.IsNullOrWhiteSpace(RateLimitText);
    public string RateLimitText => _diagnostics.RateLimitText;
    public bool CanCopyDiagnostics => HasDiscoveryDiagnostics;
    public IReadOnlyList<OwnerDiscoveryResult> OwnerDiagnostics => _diagnostics.Owners;
    public bool HasOwnerDiagnostics => _diagnostics.HasOwnerDetails;
    public bool ShowDiscoveryDetails => _showDiscoveryDetails && HasOwnerDiagnostics;
    public string DiscoveryDetailsButtonText => ShowDiscoveryDetails
        ? "Hide owner details"
        : $"Owner details ({_diagnostics.OwnerCount:N0})";
    public bool HasRefreshProgress => Busy && _refreshProgress is not null;
    public string RefreshProgressText => _refreshProgress?.Text ?? "Preparing refresh";
    public int RefreshProgressValue => _refreshProgress?.Current ?? 0;
    public int RefreshProgressMaximum => Math.Max(1, _refreshProgress?.Total ?? 1);
    public bool IsRefreshProgressIndeterminate => _refreshProgress?.IsDeterminate != true;

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

    public Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    public async Task RefreshAsync(CancellationToken ct)
    {
        Busy = true;
        var refreshProgressVersion = ++_refreshProgressVersion;
        try
        {
            _log.Append("Android", "Discovering Android APK releases...");
            var result = await _github.DiscoverAsync(
                _settingsAccessor(),
                _log.AsProgress("Android"),
                ct,
                new Progress<DiscoveryProgress>(progress => UpdateRefreshProgress(progress, refreshProgressVersion)));
            ct.ThrowIfCancellationRequested();
            ApplyDiagnostics(result.Diagnostics);
            Apps.Clear();
            foreach (var info in result.Items)
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
            if (_diagnostics.HasWarnings) _log.Append("Android", $"! {_diagnostics.WarningText}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.Append("Android", "Refresh canceled.");
            throw;
        }
        catch (Exception ex) { _log.Append("Android", $"! Refresh failed: {ex.Message}"); }
        finally
        {
            _refreshProgressVersion++;
            Busy = false;
            ClearRefreshProgress();
        }
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
        RefreshDiagnostics();
    }

    private void ApplyDiagnostics(DiscoveryDiagnostics diagnostics)
    {
        _diagnostics = diagnostics;
        if (!_diagnostics.HasOwnerDetails) _showDiscoveryDetails = false;
        RefreshDiagnostics();
    }

    private void RefreshDiagnostics()
    {
        OnPropertyChanged(nameof(HasDiscoveryDiagnostics));
        OnPropertyChanged(nameof(DiscoverySummary));
        OnPropertyChanged(nameof(HasDiscoveryWarning));
        OnPropertyChanged(nameof(DiscoveryWarningText));
        OnPropertyChanged(nameof(HasRateLimitText));
        OnPropertyChanged(nameof(RateLimitText));
        OnPropertyChanged(nameof(CanCopyDiagnostics));
        OnPropertyChanged(nameof(OwnerDiagnostics));
        OnPropertyChanged(nameof(HasOwnerDiagnostics));
        OnPropertyChanged(nameof(ShowDiscoveryDetails));
        OnPropertyChanged(nameof(DiscoveryDetailsButtonText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void ToggleDiscoveryDetails()
    {
        if (!HasOwnerDiagnostics) return;
        _showDiscoveryDetails = !ShowDiscoveryDetails;
        OnPropertyChanged(nameof(ShowDiscoveryDetails));
        OnPropertyChanged(nameof(DiscoveryDetailsButtonText));
    }

    private void CopyDiagnostics()
    {
        try
        {
            var bundle = DiagnosticsSupportBundle.Build(
                "Android APKs",
                _diagnostics,
                _log.RecentLines(40),
                _settingsAccessor().GitHubToken);
            Clipboard.SetText(bundle);
            _log.Append("Android", "Copied diagnostics bundle to clipboard.");
        }
        catch (Exception ex)
        {
            _log.Append("Android", $"! Copy diagnostics failed: {ex.Message}");
        }
    }

    private void UpdateRefreshProgress(DiscoveryProgress progress, int version)
    {
        if (version != _refreshProgressVersion || !Busy) return;
        _refreshProgress = progress;
        RefreshProgressProperties();
    }

    private void ClearRefreshProgress()
    {
        _refreshProgress = null;
        RefreshProgressProperties();
    }

    private void RefreshProgressProperties()
    {
        OnPropertyChanged(nameof(HasRefreshProgress));
        OnPropertyChanged(nameof(RefreshProgressText));
        OnPropertyChanged(nameof(RefreshProgressValue));
        OnPropertyChanged(nameof(RefreshProgressMaximum));
        OnPropertyChanged(nameof(IsRefreshProgressIndeterminate));
    }

    private void MarkRefreshed()
    {
        var cfg = _settingsAccessor();
        cfg.AndroidLastRefreshUtc = DateTimeOffset.UtcNow;
        _settingsService.Save(cfg);
        OnPropertyChanged(nameof(LastRefreshText));
    }
}
