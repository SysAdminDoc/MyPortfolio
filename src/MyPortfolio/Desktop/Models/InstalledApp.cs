namespace MyPortfolio.Desktop.Models;

public sealed class InstalledApp
{
    public required string RepoOwner { get; set; }
    public required string RepoName { get; set; }
    public required string Version { get; set; }
    public required ArtifactKind Kind { get; set; }
    public DateTimeOffset InstalledAt { get; set; }

    public string? AssetName { get; set; }
    public long AssetSizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public string? SourceAssetPath { get; set; }
    public DateTimeOffset? ReleasePublishedAt { get; set; }

    public string? PortableRoot { get; set; }
    public string? ShortcutPath { get; set; }
    public string? ExecutablePath { get; set; }
    public string? UninstallRegistryKey { get; set; }
    public string? UninstallCommand { get; set; }
    public string? InstallLocation { get; set; }
    public string? MsiProductCode { get; set; }

    public string Key => $"{RepoOwner}/{RepoName}";
}

public sealed class InstalledAppsManifest
{
    public int Version { get; set; } = 2;
    public List<InstalledApp> Apps { get; set; } = new();
}
