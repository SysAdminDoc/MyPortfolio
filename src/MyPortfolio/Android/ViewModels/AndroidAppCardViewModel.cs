using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MyPortfolio.Android.Models;
using MyPortfolio.Android.Services;
using MyPortfolio.Common;

namespace MyPortfolio.Android.ViewModels;

public sealed class AndroidAppCardViewModel : ViewModelBase
{
    private readonly ApkDownloadService _downloader;
    private readonly HttpDownloader _http;
    private readonly SettingsService _settings;
    private readonly Func<AppSettings> _settingsAccessor;
    private readonly Action<string> _log;
    private readonly Action _refreshParent;

    private bool _busy;
    private string? _busyMessage;
    private string? _errorMessage;
    private DownloadedApk? _downloaded;
    private BitmapImage? _icon;

    public AndroidAppInfo Info { get; }

    public AndroidAppCardViewModel(
        AndroidAppInfo info,
        ApkDownloadService downloader,
        HttpDownloader http,
        SettingsService settings,
        Func<AppSettings> settingsAccessor,
        Action<string> log,
        Action refreshParent)
    {
        Info = info;
        _downloader = downloader;
        _http = http;
        _settings = settings;
        _settingsAccessor = settingsAccessor;
        _log = log;
        _refreshParent = refreshParent;
        _downloaded = downloader.Find(info.RepoOwner, info.RepoName);

        DownloadCommand = new AsyncRelayCommand(DownloadAsync, _ => CanDownload);
        RemoveCommand = new RelayCommand(_ => Remove(), _ => IsDownloaded && !Busy);
        RevealCommand = new RelayCommand(_ => Reveal(), _ => IsDownloaded && !Busy);
        OpenRepoCommand = new RelayCommand(_ => OpenUrl(Info.RepoUrl));
        _downloaded = _downloader.EnsureManifestMetadata(_downloaded);
        _ = LoadIconAsync();
    }

    public string Title => Info.DisplayName;
    public string Version => Info.DisplayVersion;
    public string Description => Info.DisplayDescription;
    public string RepoUrl => Info.RepoUrl;
    public string Repo => $"{Info.RepoOwner}/{Info.RepoName}";
    public string AssetSummary => Info.AssetUrl != null
        ? $"{Info.AssetName} • {Format.Bytes(Info.AssetSizeBytes)}"
        : "Add an .apk release asset to enable download.";
    public string ReleaseSummary => Info.PublishedAt.HasValue
        ? $"Released {Info.PublishedAt.Value.LocalDateTime:MMM d, yyyy}"
        : "Release date unavailable";
    public string Stars => Info.Stars > 0 ? $"★ {Info.Stars}" : string.Empty;
    public bool HasAsset => !string.IsNullOrEmpty(Info.AssetUrl);
    public bool IsDownloaded => _downloaded != null && File.Exists(_downloaded.FilePath);
    public bool IsUpdateAvailable => IsDownloaded
        && !string.Equals(_downloaded!.Version, Info.DisplayVersion, StringComparison.OrdinalIgnoreCase);
    public bool CanDownload => HasAsset && !Busy;
    public string DownloadButtonLabel => IsDownloaded
        ? (string.Equals(_downloaded!.Version, Info.DisplayVersion, StringComparison.OrdinalIgnoreCase)
            ? "Re-download" : $"Update to {Info.DisplayVersion}")
        : (HasAsset ? "Download APK" : "Unavailable");
    public string StatusBadge => IsDownloaded
        ? (IsUpdateAvailable ? "Update available" : "Downloaded")
        : (HasAsset ? "Ready to download" : "Release needed");
    public string DownloadedDetail => _downloaded == null
        ? "Not downloaded yet"
        : !File.Exists(_downloaded.FilePath)
            ? $"Local file missing — re-download to refresh"
            : $"Local v{_downloaded.Version} • {Format.Bytes(_downloaded.SizeBytes)}";
    public string PackageName => _downloaded?.PackageName ?? string.Empty;
    public string ApkVersionCode => _downloaded?.VersionCode ?? string.Empty;
    public string ApkVersionName => _downloaded?.VersionName ?? string.Empty;
    public string PackageNameText => string.IsNullOrWhiteSpace(PackageName) ? "Package unavailable" : PackageName;
    public string ApkVersionCodeText => string.IsNullOrWhiteSpace(ApkVersionCode) ? "Code unavailable" : $"Code {ApkVersionCode}";
    public string ApkVersionNameText => string.IsNullOrWhiteSpace(ApkVersionName) ? "Name unavailable" : $"Name {ApkVersionName}";

    public BitmapImage? Icon { get => _icon; private set => SetField(ref _icon, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetField(ref _busy, value))
            {
                OnPropertyChanged(nameof(CanDownload));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string? BusyMessage { get => _busyMessage; private set => SetField(ref _busyMessage, value); }
    public string? ErrorMessage { get => _errorMessage; private set => SetField(ref _errorMessage, value); }
    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public ICommand DownloadCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand OpenRepoCommand { get; }

    private async Task DownloadAsync(object? _)
    {
        if (!HasAsset) return;
        Busy = true;
        ErrorMessage = null;
        try
        {
            BusyMessage = "Preparing download...";
            var bytesProgress = new Progress<long>(b =>
            {
                if (Info.AssetSizeBytes > 0)
                {
                    var pct = (int)Math.Min(100, b * 100L / Info.AssetSizeBytes);
                    BusyMessage = $"Downloading {pct}%";
                }
                else BusyMessage = $"Downloading {Format.Bytes(b)}";
            });
            var logProgress = new Progress<string>(_log);
            _downloaded = await _downloader.DownloadAsync(Info, _settingsAccessor(), logProgress, bytesProgress);
            BusyMessage = "Saved";
            RaiseAllChanged();
            _refreshParent();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _log($"! Download failed for {Repo}: {ex.Message}");
            OnPropertyChanged(nameof(HasError));
        }
        finally
        {
            Busy = false;
            BusyMessage = null;
        }
    }

    private void Remove()
    {
        if (_downloaded == null) return;
        var confirm = MessageBox.Show(
            $"Remove the local APK for {Title}?\n\nThe GitHub repository and release assets are not changed.",
            "Remove APK", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        Busy = true;
        try
        {
            BusyMessage = "Removing local APK...";
            var logProgress = new Progress<string>(_log);
            _downloader.Remove(Info.RepoOwner, Info.RepoName, logProgress);
            _downloaded = null;
            RaiseAllChanged();
            _refreshParent();
        }
        finally
        {
            Busy = false;
            BusyMessage = null;
        }
    }

    private void Reveal()
    {
        if (_downloaded == null) return;
        var logProgress = new Progress<string>(_log);
        _downloader.RevealInExplorer(_downloaded, logProgress);
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _log($"! open url failed: {ex.Message}"); }
    }

    private async Task LoadIconAsync()
    {
        if (string.IsNullOrEmpty(Info.IconUrl)) return;
        try
        {
            var cacheKey = $"android_{Info.RepoOwner}_{Info.RepoName}.png";
            var cachePath = Path.Combine(_settings.IconCacheDir, cacheKey);
            byte[]? bytes = null;
            if (File.Exists(cachePath)) bytes = await File.ReadAllBytesAsync(cachePath);
            else
            {
                bytes = await _http.TryDownloadBytesAsync(Info.IconUrl!);
                if (bytes != null) await File.WriteAllBytesAsync(cachePath, bytes);
            }
            if (bytes == null || bytes.Length == 0) return;

            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            Icon = bmp;
        }
        catch { /* fall back to placeholder */ }
    }

    public void RaiseAllChanged()
    {
        OnPropertyChanged(nameof(IsDownloaded));
        OnPropertyChanged(nameof(IsUpdateAvailable));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(DownloadButtonLabel));
        OnPropertyChanged(nameof(StatusBadge));
        OnPropertyChanged(nameof(DownloadedDetail));
        OnPropertyChanged(nameof(PackageName));
        OnPropertyChanged(nameof(ApkVersionCode));
        OnPropertyChanged(nameof(ApkVersionName));
        OnPropertyChanged(nameof(PackageNameText));
        OnPropertyChanged(nameof(ApkVersionCodeText));
        OnPropertyChanged(nameof(ApkVersionNameText));
        OnPropertyChanged(nameof(HasError));
    }
}
