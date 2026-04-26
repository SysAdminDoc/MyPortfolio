using System.Diagnostics;
using System.IO;
using MyPortfolio.Android.Models;
using MyPortfolio.Common;

namespace MyPortfolio.Android.Services;

/// <summary>
/// Download-only — the Android tab pulls the latest .apk to the user's local
/// download folder so they can sideload it onto a device themselves. We never
/// run apksigner / adb / aapt; the Windows host has no way to install an APK
/// natively, so attempting that would just be theatre.
/// </summary>
public sealed class ApkDownloadService
{
    private readonly SettingsService _settings;
    private readonly HttpDownloader _http;
    private DownloadedApksManifest _manifest;

    public ApkDownloadService(SettingsService settings, HttpDownloader http)
    {
        _settings = settings;
        _http = http;
        _manifest = settings.LoadAndroidManifest();
    }

    public IReadOnlyList<DownloadedApk> Downloaded => _manifest.Apks;

    public DownloadedApk? Find(string repoOwner, string repoName)
        => _manifest.Apks.FirstOrDefault(d =>
            d.RepoOwner.Equals(repoOwner, StringComparison.OrdinalIgnoreCase) &&
            d.RepoName.Equals(repoName, StringComparison.OrdinalIgnoreCase));

    public void Reload() => _manifest = _settings.LoadAndroidManifest();

    public DownloadedApk? EnsureManifestMetadata(DownloadedApk? apk, IProgress<string>? log = null)
    {
        if (apk is null || apk.HasManifestMetadata || !File.Exists(apk.FilePath)) return apk;

        var metadata = ReadManifestMetadata(apk.FilePath, log);
        if (metadata is null) return apk;

        ApplyMetadata(apk, metadata);
        _settings.SaveAndroidManifest(_manifest);
        return apk;
    }

    public async Task<DownloadedApk> DownloadAsync(AndroidAppInfo info, AppSettings cfg, IProgress<string>? log, IProgress<long>? bytes, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.AssetUrl) || string.IsNullOrEmpty(info.AssetName))
            throw new InvalidOperationException("No release asset to download.");

        var safeVersion = info.DisplayVersion.Replace('/', '_').Replace('\\', '_');
        var targetDir = Path.Combine(_settings.AndroidDownloadRoot(cfg), info.RepoOwner, info.RepoName, safeVersion);
        Directory.CreateDirectory(targetDir);

        var destination = Path.Combine(targetDir, info.AssetName!);

        log?.Report($"Downloading {info.AssetName} ({Format.Bytes(info.AssetSizeBytes)}) to {destination}");
        await _http.DownloadToFileAsync(info.AssetUrl!, destination, bytes, ct);

        string? sha = null;
        if (cfg.AndroidVerifyHashSidecar && !string.IsNullOrEmpty(info.Sha256Url))
        {
            log?.Report("Verifying SHA-256 against sidecar...");
            var sidecarText = await _http.TryDownloadTextAsync(info.Sha256Url!, ct);
            if (sidecarText is null)
            {
                // Hash check should not abort the download for the Android tab — the .apk
                // is already on disk. Surface the issue so the user can decide.
                log?.Report("  ! Hash sidecar download failed — keeping the APK but not verifying.");
            }
            else
            {
                var result = await HashVerifier.VerifyAsync(destination, sidecarText, ct);
                if (!result.Verified)
                {
                    log?.Report($"  ! {result.Detail} (expected {result.ExpectedHash}, actual {result.ActualHash})");
                }
                else
                {
                    log?.Report("  ✓ SHA-256 OK");
                    sha = result.ActualHash;
                }
            }
        }
        if (sha is null)
        {
            try { sha = await HashVerifier.ComputeSha256Async(destination, ct); }
            catch { /* hash on disk is a nicety — don't fail the row if it errors */ }
        }

        var metadata = ReadManifestMetadata(destination, log);

        var record = new DownloadedApk
        {
            RepoOwner = info.RepoOwner,
            RepoName = info.RepoName,
            Version = info.DisplayVersion,
            AssetName = info.AssetName!,
            FilePath = destination,
            SizeBytes = info.AssetSizeBytes,
            Sha256 = sha,
            DownloadedAt = DateTimeOffset.UtcNow,
            PublishedAt = info.PublishedAt
        };
        if (metadata is not null) ApplyMetadata(record, metadata);

        // Replace any prior row for this repo, then prune older versions on disk.
        _manifest.Apks.RemoveAll(d =>
            d.RepoOwner.Equals(info.RepoOwner, StringComparison.OrdinalIgnoreCase) &&
            d.RepoName.Equals(info.RepoName, StringComparison.OrdinalIgnoreCase));
        _manifest.Apks.Add(record);
        _settings.SaveAndroidManifest(_manifest);
        PruneOldVersions(_settings.AndroidDownloadRoot(cfg), info.RepoOwner, info.RepoName, safeVersion, log);

        log?.Report($"Saved {info.AssetName} for {info.DisplayName} v{info.DisplayVersion}.");
        return record;
    }

    public void Remove(string repoOwner, string repoName, IProgress<string>? log)
    {
        var entry = Find(repoOwner, repoName);
        if (entry == null) return;

        var versionDir = Path.GetDirectoryName(entry.FilePath);
        var repoDir = string.IsNullOrEmpty(versionDir) ? null : Directory.GetParent(versionDir!)?.FullName;
        try
        {
            if (!string.IsNullOrEmpty(repoDir) && Directory.Exists(repoDir))
            {
                Directory.Delete(repoDir, recursive: true);
                log?.Report($"Removed {repoDir}");
            }
        }
        catch (Exception ex) { log?.Report($"! Failed to remove {repoDir}: {ex.Message}"); }

        _manifest.Apks.RemoveAll(d =>
            d.RepoOwner.Equals(repoOwner, StringComparison.OrdinalIgnoreCase) &&
            d.RepoName.Equals(repoName, StringComparison.OrdinalIgnoreCase));
        _settings.SaveAndroidManifest(_manifest);
    }

    public bool RevealInExplorer(DownloadedApk apk, IProgress<string>? log)
    {
        try
        {
            if (!File.Exists(apk.FilePath))
            {
                log?.Report($"! APK no longer on disk: {apk.FilePath}");
                return false;
            }
            // /select highlights the file inside its containing folder.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{apk.FilePath}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            log?.Report($"! Reveal failed: {ex.Message}");
            return false;
        }
    }

    private static void PruneOldVersions(string downloadRoot, string owner, string repo, string keepVersion, IProgress<string>? log)
    {
        var repoDir = Path.Combine(downloadRoot, owner, repo);
        if (!Directory.Exists(repoDir)) return;
        foreach (var dir in Directory.EnumerateDirectories(repoDir))
        {
            var name = Path.GetFileName(dir);
            if (name.Equals(keepVersion, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                log?.Report($"Pruned old version: {name}");
            }
            catch (Exception ex) { log?.Report($"! Could not prune {dir}: {ex.Message}"); }
        }
    }

    private static ApkManifestMetadata? ReadManifestMetadata(string apkPath, IProgress<string>? log)
    {
        var metadata = ApkManifestReader.TryRead(apkPath, out var error);
        if (metadata is not null)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(metadata.PackageName)) parts.Add(metadata.PackageName!);
            if (!string.IsNullOrWhiteSpace(metadata.VersionName)) parts.Add($"version {metadata.VersionName}");
            if (!string.IsNullOrWhiteSpace(metadata.VersionCode)) parts.Add($"code {metadata.VersionCode}");
            log?.Report($"Read APK manifest: {string.Join(" / ", parts)}");
            return metadata;
        }

        if (!string.IsNullOrWhiteSpace(error))
            log?.Report($"  ~ APK manifest metadata unavailable: {error}");
        return null;
    }

    private static void ApplyMetadata(DownloadedApk apk, ApkManifestMetadata metadata)
    {
        apk.PackageName = metadata.PackageName;
        apk.VersionCode = metadata.VersionCode;
        apk.VersionName = metadata.VersionName;
    }
}
