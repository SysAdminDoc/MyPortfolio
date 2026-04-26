using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
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
    private bool _showDiscoveryDetails;
    private DiscoveryDiagnostics _diagnostics = new();
    private DiscoveryProgress? _refreshProgress;
    private int _refreshProgressVersion;

    public ObservableCollection<AppCardViewModel> Apps { get; } = new();
    public ICollectionView AppsView { get; }

    public ICommand RefreshCommand { get; }
    public ICommand OpenInstallDirCommand { get; }
    public ICommand ToggleDiscoveryDetailsCommand { get; }
    public ICommand CopyDiagnosticsCommand { get; }
    public ICommand SaveDiagnosticsCommand { get; }

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
        ToggleDiscoveryDetailsCommand = new RelayCommand(_ => ToggleDiscoveryDetails(), _ => HasOwnerDiagnostics);
        CopyDiagnosticsCommand = new RelayCommand(_ => CopyDiagnostics(), _ => HasDiscoveryDiagnostics);
        SaveDiagnosticsCommand = new RelayCommand(_ => SaveDiagnostics(), _ => HasDiscoveryDiagnostics);
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
        if (obj is not AppCardViewModel vm) return false;
        if (ShowInstalledOnly && !vm.IsInstalled) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var q = SearchText.Trim();
        return vm.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.Repo.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    public Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    public async Task RefreshAsync(CancellationToken ct)
    {
        Busy = true;
        var refreshProgressVersion = ++_refreshProgressVersion;
        try
        {
            _log.Append("Desktop", "Discovering desktop apps...");
            var result = await _github.DiscoverAsync(
                _settingsAccessor(),
                _log.AsProgress("Desktop"),
                ct,
                new Progress<DiscoveryProgress>(progress => UpdateRefreshProgress(progress, refreshProgressVersion)));
            ct.ThrowIfCancellationRequested();
            ApplyDiagnostics(result.Diagnostics);
            Apps.Clear();
            foreach (var info in result.Items)
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
            if (_diagnostics.HasWarnings) _log.Append("Desktop", $"! {_diagnostics.WarningText}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.Append("Desktop", "Refresh canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _log.Append("Desktop", $"! Refresh failed: {ex.Message}");
        }
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
                "Desktop apps",
                _diagnostics,
                _log.RecentLines(40),
                _settingsAccessor().GitHubToken);
            Clipboard.SetText(bundle);
            _log.Append("Desktop", "Copied diagnostics bundle to clipboard.");
        }
        catch (Exception ex)
        {
            _log.Append("Desktop", $"! Copy diagnostics failed: {ex.Message}");
        }
    }

    private void SaveDiagnostics()
    {
        try
        {
            var path = DiagnosticsSupportBundle.SaveToFile(
                "Desktop apps",
                _diagnostics,
                _log.RecentLines(40),
                _settingsAccessor().GitHubToken);
            _log.Append("Desktop", $"Saved diagnostics bundle to {path}.");
            try { DiagnosticsSupportBundle.RevealFile(path); }
            catch (Exception ex) { _log.Append("Desktop", $"! Saved diagnostics but could not reveal it: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            _log.Append("Desktop", $"! Save diagnostics failed: {ex.Message}");
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
        cfg.DesktopLastRefreshUtc = DateTimeOffset.UtcNow;
        _settingsService.Save(cfg);
        OnPropertyChanged(nameof(LastRefreshText));
    }
}
