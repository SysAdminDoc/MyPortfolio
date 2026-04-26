using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MyPortfolio.Common;
using MyPortfolio.Desktop.Models;
using MyPortfolio.Desktop.Services;

namespace MyPortfolio.Desktop.ViewModels;

public sealed class AppCardViewModel : ViewModelBase
{
    private readonly InstallService _installer;
    private readonly HttpDownloader _http;
    private readonly SettingsService _settings;
    private readonly Func<AppSettings> _settingsAccessor;
    private readonly Action<string> _log;
    private readonly Action _refreshParent;

    private bool _busy;
    private string? _busyMessage;
    private string? _errorMessage;
    private InstalledApp? _installed;
    private BitmapImage? _icon;
    private bool _detailsExpanded;

    public AppInfo Info { get; }

    public AppCardViewModel(
        AppInfo info,
        InstallService installer,
        HttpDownloader http,
        SettingsService settings,
        Func<AppSettings> settingsAccessor,
        Action<string> log,
        Action refreshParent)
    {
        Info = info;
        _installer = installer;
        _http = http;
        _settings = settings;
        _settingsAccessor = settingsAccessor;
        _log = log;
        _refreshParent = refreshParent;
        _installed = installer.Find(info.RepoOwner, info.RepoName);

        InstallCommand = new AsyncRelayCommand(InstallAsync, _ => CanInstall);
        UninstallCommand = new AsyncRelayCommand(UninstallAsync, _ => IsInstalled && !Busy);
        RunCommand = new RelayCommand(_ => Run(), _ => CanRun);
        OpenRepoCommand = new RelayCommand(_ => OpenUrl(Info.RepoUrl));
        OpenInstallDirCommand = new RelayCommand(_ => OpenDir(), _ => CanOpenDir);
        ToggleDetailsCommand = new RelayCommand(_ => IsDetailsExpanded = !IsDetailsExpanded);
        OpenDetailsPathCommand = new RelayCommand(_ => OpenLocalPath(), _ => HasLocalPath);
        CopyLocalPathCommand = new RelayCommand(_ => CopyToClipboard(LocalPath, "Local path"), _ => HasLocalPath);
        CopyShaCommand = new RelayCommand(_ => CopyToClipboard(FullSha, "SHA-256"), _ => HasSha);
        _ = LoadIconAsync();
    }

    public string Title => Info.DisplayName;
    public string Version => Info.DisplayVersion;
    public string Description => Info.DisplayDescription;
    public string RepoUrl => Info.RepoUrl;
    public string Repo => $"{Info.RepoOwner}/{Info.RepoName}";
    public string KindLabel => Info.Kind.DisplayName();
    public string AssetSummary => Info.AssetUrl != null
        ? $"{Info.AssetName} • {Format.Bytes(Info.AssetSizeBytes)}"
        : "Add an MSI / EXE / ZIP release asset to enable install.";
    public string ReleaseSummary => Info.PublishedAt.HasValue
        ? $"Released {Info.PublishedAt.Value.LocalDateTime:MMM d, yyyy}"
        : "Release date unavailable";
    public string Stars => Info.Stars > 0 ? $"★ {Info.Stars}" : string.Empty;
    public bool HasAsset => !string.IsNullOrEmpty(Info.AssetUrl);
    public bool IsInstalled => _installed != null;
    public bool IsUpdateAvailable => IsInstalled
        && !string.Equals(_installed!.Version, Info.DisplayVersion, StringComparison.OrdinalIgnoreCase);
    public bool CanInstall => HasAsset && !Busy;
    public bool CanRun => IsInstalled && !Busy;
    public bool CanOpenDir => IsInstalled && !Busy
        && (_installed?.PortableRoot != null || _installed?.InstallLocation != null);
    public string InstallButtonLabel => IsInstalled
        ? (string.Equals(_installed!.Version, Info.DisplayVersion, StringComparison.OrdinalIgnoreCase)
            ? "Reinstall" : $"Update to {Info.DisplayVersion}")
        : (HasAsset ? "Install" : "Unavailable");
    public string StatusBadge => IsInstalled
        ? (IsUpdateAvailable ? "Update available" : "Installed")
        : (HasAsset ? "Ready to install" : "Release needed");
    public string InstalledDetail => IsInstalled
        ? $"Local v{_installed!.Version} • {_installed.Kind.DisplayName()}"
        : "Not installed locally";
    public bool IsDetailsExpanded
    {
        get => _detailsExpanded;
        set
        {
            if (SetField(ref _detailsExpanded, value))
                OnPropertyChanged(nameof(DetailsButtonLabel));
        }
    }
    public string DetailsButtonLabel => IsDetailsExpanded ? "Hide details" : "Details";
    public string LocalPath => _installed?.PortableRoot
        ?? _installed?.InstallLocation
        ?? _installed?.ExecutablePath
        ?? _installed?.SourceAssetPath
        ?? string.Empty;
    public bool HasLocalPath => !string.IsNullOrWhiteSpace(LocalPath);
    public string LocalPathText => HasLocalPath ? LocalPath : "Unavailable";
    public string ArtifactDetailsText => _installed?.AssetName is { Length: > 0 } asset
        ? $"{asset} • {Format.Bytes(_installed.AssetSizeBytes)}"
        : Info.AssetUrl is not null ? $"{Info.AssetName} • {Format.Bytes(Info.AssetSizeBytes)}" : "Unavailable";
    public string ReleaseDetailsText => $"Release: {Format.LocalDateTime(_installed?.ReleasePublishedAt ?? Info.PublishedAt)}";
    public string FullSha => _installed?.Sha256 ?? string.Empty;
    public bool HasSha => !string.IsNullOrWhiteSpace(FullSha);
    public string ShaDetailsText => $"SHA-256: {Format.ShortSha(FullSha)}";

    public BitmapImage? Icon { get => _icon; private set => SetField(ref _icon, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetField(ref _busy, value))
            {
                OnPropertyChanged(nameof(CanInstall));
                OnPropertyChanged(nameof(CanRun));
                OnPropertyChanged(nameof(CanOpenDir));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string? BusyMessage { get => _busyMessage; private set => SetField(ref _busyMessage, value); }
    public string? ErrorMessage { get => _errorMessage; private set => SetField(ref _errorMessage, value); }
    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public ICommand InstallCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand OpenRepoCommand { get; }
    public ICommand OpenInstallDirCommand { get; }
    public ICommand ToggleDetailsCommand { get; }
    public ICommand OpenDetailsPathCommand { get; }
    public ICommand CopyLocalPathCommand { get; }
    public ICommand CopyShaCommand { get; }

    private async Task InstallAsync(object? _)
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
            _installed = await _installer.InstallAsync(Info, _settingsAccessor(), logProgress, bytesProgress);
            BusyMessage = "Installed";
            RaiseAllChanged();
            _refreshParent();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _log($"! Install failed for {Repo}: {ex.Message}");
            OnPropertyChanged(nameof(HasError));
        }
        finally
        {
            Busy = false;
            BusyMessage = null;
        }
    }

    private async Task UninstallAsync(object? _)
    {
        if (_installed is null) return;
        var confirm = MessageBox.Show(
            $"Uninstall {Title} v{_installed.Version}?",
            "Uninstall app",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        Busy = true;
        ErrorMessage = null;
        try
        {
            BusyMessage = "Uninstalling...";
            var logProgress = new Progress<string>(_log);
            await _installer.UninstallAsync(_installed, logProgress);
            _installed = null;
            RaiseAllChanged();
            _refreshParent();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _log($"! Uninstall failed for {Repo}: {ex.Message}");
            OnPropertyChanged(nameof(HasError));
        }
        finally
        {
            Busy = false;
            BusyMessage = null;
        }
    }

    private void Run()
    {
        if (_installed is null) return;
        var logProgress = new Progress<string>(_log);
        _installer.TryRun(_installed, logProgress);
    }

    private void OpenDir()
    {
        var path = _installed?.PortableRoot ?? _installed?.InstallLocation;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { _log($"! open dir failed: {ex.Message}"); }
    }

    private void OpenLocalPath()
    {
        var path = LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { _log($"! open path failed: {ex.Message}"); }
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _log($"! open url failed: {ex.Message}"); }
    }

    private void CopyToClipboard(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Clipboard.SetText(text);
            _log($"{label} copied to clipboard for {Repo}.");
        }
        catch (Exception ex) { _log($"! copy failed: {ex.Message}"); }
    }

    private async Task LoadIconAsync()
    {
        if (string.IsNullOrEmpty(Info.IconUrl)) return;
        try
        {
            var cacheKey = $"desktop_{Info.RepoOwner}_{Info.RepoName}.png";
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
        catch { /* ignore — fall back to placeholder */ }
    }

    public void RaiseAllChanged()
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(InstallButtonLabel));
        OnPropertyChanged(nameof(StatusBadge));
        OnPropertyChanged(nameof(HasAsset));
        OnPropertyChanged(nameof(IsUpdateAvailable));
        OnPropertyChanged(nameof(CanOpenDir));
        OnPropertyChanged(nameof(InstalledDetail));
        OnPropertyChanged(nameof(LocalPath));
        OnPropertyChanged(nameof(HasLocalPath));
        OnPropertyChanged(nameof(LocalPathText));
        OnPropertyChanged(nameof(ArtifactDetailsText));
        OnPropertyChanged(nameof(ReleaseDetailsText));
        OnPropertyChanged(nameof(FullSha));
        OnPropertyChanged(nameof(HasSha));
        OnPropertyChanged(nameof(ShaDetailsText));
        OnPropertyChanged(nameof(HasError));
        CommandManager.InvalidateRequerySuggested();
    }
}
