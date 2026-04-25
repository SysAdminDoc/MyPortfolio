using System.IO;
using System.Text.Json;
using MyPortfolio.Desktop.Models;
using MyPortfolio.Chrome.Models;

namespace MyPortfolio.Common;

public sealed class SettingsService
{
    public string SettingsDir { get; }
    public string SettingsPath { get; }

    // Desktop tab paths
    public string DesktopAppsRootDefault { get; }
    public string DesktopDownloadsDir { get; }
    public string DesktopManifestPath { get; }

    // Chrome tab paths
    public string ChromeExtensionsRoot { get; }
    public string ChromeManifestPath { get; }

    // Android tab paths (download-only)
    public string AndroidDownloadRootDefault { get; }
    public string AndroidManifestPath { get; }

    // Shared
    public string CacheDir { get; }
    public string LogsDir { get; }
    public string IconCacheDir { get; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        SettingsDir = Path.Combine(appData, "MyPortfolio");
        SettingsPath = Path.Combine(SettingsDir, "settings.json");

        DesktopAppsRootDefault = Path.Combine(localAppData, "MyPortfolio", "desktop", "apps");
        DesktopDownloadsDir = Path.Combine(localAppData, "MyPortfolio", "desktop", "downloads");
        DesktopManifestPath = Path.Combine(SettingsDir, "desktop-installed.json");

        ChromeExtensionsRoot = Path.Combine(localAppData, "MyPortfolio", "chrome", "extensions");
        ChromeManifestPath = Path.Combine(SettingsDir, "chrome-installed.json");

        // Android downloads land under the user's Downloads folder by default —
        // the user explicitly asked for "download the apk's to the PC". Saving them
        // somewhere they actually look beats burying them in LocalAppData.
        AndroidDownloadRootDefault = Path.Combine(userProfile, "Downloads", "MyPortfolio", "Android");
        AndroidManifestPath = Path.Combine(SettingsDir, "android-downloads.json");

        CacheDir = Path.Combine(localAppData, "MyPortfolio", "cache");
        LogsDir = Path.Combine(localAppData, "MyPortfolio", "logs");
        IconCacheDir = Path.Combine(CacheDir, "icons");

        Directory.CreateDirectory(SettingsDir);
        Directory.CreateDirectory(DesktopAppsRootDefault);
        Directory.CreateDirectory(DesktopDownloadsDir);
        Directory.CreateDirectory(ChromeExtensionsRoot);
        Directory.CreateDirectory(AndroidDownloadRootDefault);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(IconCacheDir);
    }

    public string DesktopAppsRoot(AppSettings cfg)
    {
        var root = string.IsNullOrWhiteSpace(cfg.DesktopInstallRootOverride) ? DesktopAppsRootDefault : cfg.DesktopInstallRootOverride!;
        Directory.CreateDirectory(root);
        return root;
    }

    public string AndroidDownloadRoot(AppSettings cfg)
    {
        var root = string.IsNullOrWhiteSpace(cfg.AndroidDownloadFolderOverride) ? AndroidDownloadRootDefault : cfg.AndroidDownloadFolderOverride!;
        Directory.CreateDirectory(root);
        return root;
    }

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOpts);
        File.WriteAllText(SettingsPath, json);
    }

    public InstalledAppsManifest LoadDesktopManifest()
    {
        if (!File.Exists(DesktopManifestPath)) return new InstalledAppsManifest();
        try
        {
            return JsonSerializer.Deserialize<InstalledAppsManifest>(File.ReadAllText(DesktopManifestPath), JsonOpts) ?? new InstalledAppsManifest();
        }
        catch { return new InstalledAppsManifest(); }
    }

    public void SaveDesktopManifest(InstalledAppsManifest manifest)
        => File.WriteAllText(DesktopManifestPath, JsonSerializer.Serialize(manifest, JsonOpts));

    public InstalledExtensionsManifest LoadChromeManifest()
    {
        if (!File.Exists(ChromeManifestPath)) return new InstalledExtensionsManifest();
        try
        {
            return JsonSerializer.Deserialize<InstalledExtensionsManifest>(File.ReadAllText(ChromeManifestPath), JsonOpts) ?? new InstalledExtensionsManifest();
        }
        catch { return new InstalledExtensionsManifest(); }
    }

    public void SaveChromeManifest(InstalledExtensionsManifest manifest)
        => File.WriteAllText(ChromeManifestPath, JsonSerializer.Serialize(manifest, JsonOpts));

    public Android.Models.DownloadedApksManifest LoadAndroidManifest()
    {
        if (!File.Exists(AndroidManifestPath)) return new Android.Models.DownloadedApksManifest();
        try
        {
            return JsonSerializer.Deserialize<Android.Models.DownloadedApksManifest>(File.ReadAllText(AndroidManifestPath), JsonOpts) ?? new Android.Models.DownloadedApksManifest();
        }
        catch { return new Android.Models.DownloadedApksManifest(); }
    }

    public void SaveAndroidManifest(Android.Models.DownloadedApksManifest manifest)
        => File.WriteAllText(AndroidManifestPath, JsonSerializer.Serialize(manifest, JsonOpts));
}
