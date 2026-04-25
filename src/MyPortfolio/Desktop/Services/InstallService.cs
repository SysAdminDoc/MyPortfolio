using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using MyPortfolio.Common;
using MyPortfolio.Desktop.Models;

namespace MyPortfolio.Desktop.Services;

public sealed class InstallService
{
    private readonly SettingsService _settings;
    private readonly HttpDownloader _http;
    private InstalledAppsManifest _manifest;

    public InstallService(SettingsService settings, HttpDownloader http)
    {
        _settings = settings;
        _http = http;
        _manifest = settings.LoadDesktopManifest();
    }

    public IReadOnlyList<InstalledApp> Installed => _manifest.Apps;

    public InstalledApp? Find(string repoOwner, string repoName)
        => _manifest.Apps.FirstOrDefault(e =>
            e.RepoOwner.Equals(repoOwner, StringComparison.OrdinalIgnoreCase) &&
            e.RepoName.Equals(repoName, StringComparison.OrdinalIgnoreCase));

    public void Reload() => _manifest = _settings.LoadDesktopManifest();

    public async Task<InstalledApp> InstallAsync(AppInfo info, AppSettings cfg, IProgress<string>? log, IProgress<long>? bytes, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.AssetUrl) || string.IsNullOrEmpty(info.AssetName))
            throw new InvalidOperationException("No release asset to install.");

        var safeVersion = info.DisplayVersion.Replace('/', '_').Replace('\\', '_');
        var stagingDir = Path.Combine(_settings.DesktopDownloadsDir, $"{info.RepoName}-{safeVersion}");
        Directory.CreateDirectory(stagingDir);
        var stagedFile = Path.Combine(stagingDir, info.AssetName!);

        log?.Report($"Downloading {info.AssetName} ({Format.Bytes(info.AssetSizeBytes)})...");
        await _http.DownloadToFileAsync(info.AssetUrl!, stagedFile, bytes, ct);

        if (cfg.DesktopVerifyHashSidecar)
        {
            if (string.IsNullOrEmpty(info.Sha256Url))
            {
                log?.Report("  ~ no .sha256.txt sidecar present in release; skipping verification.");
            }
            else
            {
                log?.Report("Verifying SHA-256 against sidecar...");
                var sidecarText = await _http.TryDownloadTextAsync(info.Sha256Url!, ct);
                if (sidecarText is null)
                    throw new InvalidOperationException("Hash sidecar download failed — refusing to install.");
                var result = await HashVerifier.VerifyAsync(stagedFile, sidecarText, ct);
                if (!result.Verified)
                {
                    log?.Report($"  ! {result.Detail} (expected {result.ExpectedHash}, actual {result.ActualHash})");
                    throw new InvalidOperationException($"Hash verification failed: {result.Detail}");
                }
                log?.Report($"  ✓ SHA-256 OK");
            }
        }

        var refinedKind = info.Kind == ArtifactKind.PortableZip || info.Kind == ArtifactKind.Msi
            ? info.Kind
            : AssetClassifier.RefineFromFile(stagedFile, info.Kind);
        if (refinedKind != info.Kind)
            log?.Report($"Asset refined to {refinedKind.DisplayName()} after byte scan.");

        InstalledApp record = refinedKind switch
        {
            ArtifactKind.Msi => await InstallMsiAsync(info, stagedFile, log, ct),
            ArtifactKind.Inno => await RunInstallerAsync(info, stagedFile, refinedKind, "/SILENT /NORESTART", log, ct),
            ArtifactKind.Nsis => await RunInstallerAsync(info, stagedFile, refinedKind, "/S", log, ct),
            ArtifactKind.GenericExe => await RunInstallerAsync(info, stagedFile, refinedKind, null, log, ct),
            ArtifactKind.PortableZip => await InstallPortableAsync(info, cfg, stagedFile, log, ct),
            _ => throw new InvalidOperationException($"Unsupported artifact kind: {refinedKind}")
        };

        _manifest.Apps.RemoveAll(e =>
            e.RepoOwner.Equals(info.RepoOwner, StringComparison.OrdinalIgnoreCase) &&
            e.RepoName.Equals(info.RepoName, StringComparison.OrdinalIgnoreCase));
        _manifest.Apps.Add(record);
        _settings.SaveDesktopManifest(_manifest);
        log?.Report($"Installed {info.DisplayName} v{info.DisplayVersion} ({record.Kind.DisplayName()}).");
        return record;
    }

    public async Task UninstallAsync(InstalledApp app, IProgress<string>? log, CancellationToken ct = default)
    {
        log?.Report($"Uninstalling {app.RepoName} v{app.Version} ({app.Kind.DisplayName()})...");
        switch (app.Kind)
        {
            case ArtifactKind.Msi: await UninstallMsiAsync(app, log, ct); break;
            case ArtifactKind.Inno:
            case ArtifactKind.Nsis:
            case ArtifactKind.GenericExe: await UninstallExeAsync(app, log, ct); break;
            case ArtifactKind.PortableZip: UninstallPortable(app, log); break;
        }

        _manifest.Apps.RemoveAll(e =>
            e.RepoOwner.Equals(app.RepoOwner, StringComparison.OrdinalIgnoreCase) &&
            e.RepoName.Equals(app.RepoName, StringComparison.OrdinalIgnoreCase));
        _settings.SaveDesktopManifest(_manifest);
        log?.Report($"Uninstall complete: {app.RepoOwner}/{app.RepoName}");
    }

    public bool TryRun(InstalledApp app, IProgress<string>? log)
    {
        try
        {
            string? exe = app.Kind == ArtifactKind.PortableZip
                ? (!string.IsNullOrEmpty(app.ExecutablePath) && File.Exists(app.ExecutablePath)
                    ? app.ExecutablePath
                    : !string.IsNullOrEmpty(app.PortableRoot) ? FindPrimaryExe(app.PortableRoot!) : null)
                : ResolveLaunchExe(app);

            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                log?.Report($"! Could not locate executable for {app.RepoName}.");
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
            });
            log?.Report($"Launched {app.RepoName}.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Report($"! Run failed: {ex.Message}");
            return false;
        }
    }

    private string? ResolveLaunchExe(InstalledApp app)
    {
        if (!string.IsNullOrEmpty(app.UninstallRegistryKey))
        {
            var entry = UninstallRegistry.FindBestMatch(app.RepoOwner, app.RepoName, app.Version);
            if (entry != null)
            {
                if (!string.IsNullOrEmpty(entry.IconPath))
                {
                    var iconPath = entry.IconPath.Trim('"');
                    var commaIdx = iconPath.LastIndexOf(',');
                    if (commaIdx > 0) iconPath = iconPath.Substring(0, commaIdx);
                    if (iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(iconPath))
                        return iconPath;
                }
                if (!string.IsNullOrEmpty(entry.InstallLocation) && Directory.Exists(entry.InstallLocation))
                    return FindPrimaryExe(entry.InstallLocation);
            }
        }
        if (!string.IsNullOrEmpty(app.InstallLocation) && Directory.Exists(app.InstallLocation))
            return FindPrimaryExe(app.InstallLocation);
        return null;
    }

    private async Task<InstalledApp> InstallMsiAsync(AppInfo info, string msiPath, IProgress<string>? log, CancellationToken ct)
    {
        var preSnapshot = SnapshotEntries();
        var logPath = Path.Combine(_settings.LogsDir, $"msi-{info.RepoName}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        log?.Report($"Running msiexec /i \"{Path.GetFileName(msiPath)}\" /qb /norestart");
        log?.Report($"  log: {logPath}");
        var psi = new ProcessStartInfo("msiexec.exe") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("/i");
        psi.ArgumentList.Add(msiPath);
        psi.ArgumentList.Add("/qb");
        psi.ArgumentList.Add("/norestart");
        psi.ArgumentList.Add("/L*v");
        psi.ArgumentList.Add(logPath);
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start msiexec.");
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0 && proc.ExitCode != 3010)
            throw new InvalidOperationException($"msiexec returned exit code {proc.ExitCode}. See {logPath}");

        var entry = DiffNewEntry(preSnapshot, info.RepoName);
        return new InstalledApp
        {
            RepoOwner = info.RepoOwner,
            RepoName = info.RepoName,
            Version = info.DisplayVersion,
            Kind = ArtifactKind.Msi,
            InstalledAt = DateTimeOffset.UtcNow,
            UninstallRegistryKey = entry?.SubKeyName,
            UninstallCommand = entry?.QuietUninstallString ?? entry?.UninstallString,
            InstallLocation = entry?.InstallLocation,
            MsiProductCode = entry?.IsMsi == true ? entry.SubKeyName : null
        };
    }

    private async Task UninstallMsiAsync(InstalledApp app, IProgress<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(app.MsiProductCode) && string.IsNullOrEmpty(app.UninstallCommand))
            throw new InvalidOperationException("MSI uninstall requires a ProductCode or UninstallString — neither is recorded.");

        var logPath = Path.Combine(_settings.LogsDir, $"msi-uninst-{app.RepoName}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var psi = new ProcessStartInfo("msiexec.exe") { UseShellExecute = false, CreateNoWindow = true };
        if (!string.IsNullOrEmpty(app.MsiProductCode))
        {
            psi.ArgumentList.Add("/x");
            psi.ArgumentList.Add(app.MsiProductCode!);
            psi.ArgumentList.Add("/qb");
            psi.ArgumentList.Add("/norestart");
            psi.ArgumentList.Add("/L*v");
            psi.ArgumentList.Add(logPath);
        }
        else
        {
            log?.Report("Falling back to recorded UninstallString.");
            await RunRawCommandAsync(app.UninstallCommand!, log, ct);
            return;
        }
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start msiexec.");
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0 && proc.ExitCode != 3010)
            throw new InvalidOperationException($"msiexec /x returned {proc.ExitCode}. See {logPath}");
    }

    private async Task<InstalledApp> RunInstallerAsync(AppInfo info, string exePath, ArtifactKind kind, string? silentArgs, IProgress<string>? log, CancellationToken ct)
    {
        var preSnapshot = SnapshotEntries();
        var psi = new ProcessStartInfo(exePath) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(silentArgs))
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            foreach (var part in silentArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                psi.ArgumentList.Add(part);
            log?.Report($"Running {Path.GetFileName(exePath)} {silentArgs}");
        }
        else
        {
            log?.Report($"Running interactive installer {Path.GetFileName(exePath)} (no silent mode known)");
        }
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start installer.");
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0 && proc.ExitCode != 3010)
            log?.Report($"  ~ installer exit code {proc.ExitCode} — proceeding with detection anyway.");

        var entry = DiffNewEntry(preSnapshot, info.RepoName);
        if (entry is null)
            log?.Report($"  ~ no new uninstall registry entry detected.");

        return new InstalledApp
        {
            RepoOwner = info.RepoOwner,
            RepoName = info.RepoName,
            Version = info.DisplayVersion,
            Kind = kind,
            InstalledAt = DateTimeOffset.UtcNow,
            UninstallRegistryKey = entry?.SubKeyName,
            UninstallCommand = entry?.QuietUninstallString ?? entry?.UninstallString,
            InstallLocation = entry?.InstallLocation
        };
    }

    private async Task UninstallExeAsync(InstalledApp app, IProgress<string>? log, CancellationToken ct)
    {
        var cmd = app.UninstallCommand;
        if (string.IsNullOrEmpty(cmd))
        {
            var entry = UninstallRegistry.FindBestMatch(app.RepoOwner, app.RepoName, app.Version);
            cmd = entry?.QuietUninstallString ?? entry?.UninstallString;
        }
        if (string.IsNullOrEmpty(cmd))
            throw new InvalidOperationException("No UninstallString could be located for this app.");

        if (app.Kind == ArtifactKind.Inno && !cmd.Contains("/SILENT", StringComparison.OrdinalIgnoreCase))
            cmd += " /SILENT /NORESTART";
        else if (app.Kind == ArtifactKind.Nsis && !cmd.Contains("/S", StringComparison.Ordinal))
            cmd += " /S";

        log?.Report($"Running: {cmd}");
        await RunRawCommandAsync(cmd, log, ct);
    }

    private static async Task RunRawCommandAsync(string commandLine, IProgress<string>? log, CancellationToken ct)
    {
        var (exe, args) = SplitCommand(commandLine);
        if (string.IsNullOrEmpty(exe))
            throw new InvalidOperationException("Could not parse command line.");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = args ?? ""
        };
        var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exe}");
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0 && proc.ExitCode != 3010)
            log?.Report($"  ~ exit code {proc.ExitCode}");
    }

    private static (string exe, string? args) SplitCommand(string commandLine)
    {
        commandLine = commandLine.Trim();
        if (string.IsNullOrEmpty(commandLine)) return ("", null);
        if (commandLine.StartsWith('"'))
        {
            var end = commandLine.IndexOf('"', 1);
            if (end < 0) return (commandLine.Trim('"'), null);
            var exe = commandLine.Substring(1, end - 1);
            var rest = commandLine.Length > end + 1 ? commandLine.Substring(end + 1).Trim() : null;
            return (exe, string.IsNullOrEmpty(rest) ? null : rest);
        }
        var idx = commandLine.IndexOf(' ');
        if (idx < 0) return (commandLine, null);
        return (commandLine.Substring(0, idx), commandLine.Substring(idx + 1).Trim());
    }

    private async Task<InstalledApp> InstallPortableAsync(AppInfo info, AppSettings cfg, string zipPath, IProgress<string>? log, CancellationToken ct)
    {
        var safeVersion = info.DisplayVersion.Replace('/', '_').Replace('\\', '_');
        var targetDir = Path.Combine(_settings.DesktopAppsRoot(cfg), info.RepoOwner, info.RepoName, safeVersion);
        if (Directory.Exists(targetDir))
        {
            log?.Report($"Removing previous extraction at {targetDir}");
            Directory.Delete(targetDir, recursive: true);
        }
        Directory.CreateDirectory(targetDir);
        log?.Report($"Extracting ZIP to {targetDir}");
        await Task.Run(() => ExtractZip(zipPath, targetDir), ct);

        var exe = FindPrimaryExe(targetDir)
            ?? throw new InvalidOperationException("No .exe found in the portable archive.");
        log?.Report($"Selected launcher: {Path.GetFileName(exe)}");

        var lnkPath = ShortcutService.ShortcutPathFor(info.RepoName);
        ShortcutService.Create(info.RepoName, exe, Path.GetDirectoryName(exe), info.DisplayDescription);
        log?.Report($"Start Menu shortcut: {lnkPath}");

        PruneOldPortableVersions(_settings.DesktopAppsRoot(cfg), info.RepoOwner, info.RepoName, safeVersion, log);

        return new InstalledApp
        {
            RepoOwner = info.RepoOwner,
            RepoName = info.RepoName,
            Version = info.DisplayVersion,
            Kind = ArtifactKind.PortableZip,
            InstalledAt = DateTimeOffset.UtcNow,
            PortableRoot = targetDir,
            ShortcutPath = lnkPath,
            ExecutablePath = exe
        };
    }

    private static void UninstallPortable(InstalledApp app, IProgress<string>? log)
    {
        if (!string.IsNullOrEmpty(app.PortableRoot))
        {
            var repoDir = Directory.GetParent(app.PortableRoot)?.FullName;
            try
            {
                if (!string.IsNullOrEmpty(repoDir) && Directory.Exists(repoDir))
                {
                    Directory.Delete(repoDir, recursive: true);
                    log?.Report($"Removed {repoDir}");
                }
            }
            catch (Exception ex) { log?.Report($"! Failed to remove {repoDir}: {ex.Message}"); }
        }
        ShortcutService.Remove(app.ShortcutPath);
    }

    private static void ExtractZip(string zipPath, string targetDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        string? wrapper = null;
        var rootEntries = zip.Entries
            .Select(e => e.FullName.Replace('\\', '/'))
            .Where(n => n.Length > 0)
            .ToList();
        if (rootEntries.Count > 0)
        {
            var firstSegments = rootEntries.Select(n => n.Split('/').First()).Distinct().ToList();
            if (firstSegments.Count == 1
                && rootEntries.All(n => n.StartsWith(firstSegments[0] + "/", StringComparison.Ordinal) || n == firstSegments[0]))
                wrapper = firstSegments[0];
        }

        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                continue;
            var rel = entry.FullName.Replace('\\', '/');
            if (wrapper != null && rel.StartsWith(wrapper + "/", StringComparison.Ordinal))
                rel = rel.Substring(wrapper.Length + 1);
            if (string.IsNullOrEmpty(rel)) continue;

            var dest = Path.GetFullPath(Path.Combine(targetDir, rel));
            if (!dest.StartsWith(Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refusing to extract path outside target: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var es = entry.Open();
            using var fs = File.Create(dest);
            es.CopyTo(fs);
        }
    }

    private static string? FindPrimaryExe(string root)
    {
        if (!Directory.Exists(root)) return null;
        var exes = Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .Where(fi => !fi.Name.StartsWith("unins", StringComparison.OrdinalIgnoreCase))
            .Where(fi => !fi.Name.Equals("vc_redist.exe", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(fi => fi.Length)
            .ToList();
        return exes.FirstOrDefault()?.FullName;
    }

    private static void PruneOldPortableVersions(string appsRoot, string owner, string repo, string keepVersion, IProgress<string>? log)
    {
        var repoDir = Path.Combine(appsRoot, owner, repo);
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

    private static HashSet<string> SnapshotEntries()
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in UninstallRegistry.ReadAll())
            s.Add(e.Hive + "::" + e.SubKeyName);
        return s;
    }

    private static UninstallEntry? DiffNewEntry(HashSet<string> preSnapshot, string repoNameHint)
    {
        var post = UninstallRegistry.ReadAll();
        var diff = post.Where(e => !preSnapshot.Contains(e.Hive + "::" + e.SubKeyName)).ToList();
        if (diff.Count == 0) return UninstallRegistry.FindBestMatch("", repoNameHint);
        if (diff.Count == 1) return diff[0];
        return diff.FirstOrDefault(e => e.DisplayName.Contains(repoNameHint, StringComparison.OrdinalIgnoreCase))
            ?? diff[0];
    }
}
