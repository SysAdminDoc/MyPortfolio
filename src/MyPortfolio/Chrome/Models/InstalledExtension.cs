namespace MyPortfolio.Chrome.Models;

public sealed class InstalledExtension
{
    public required string RepoOwner { get; set; }
    public required string RepoName { get; set; }
    public required string Version { get; set; }
    public required string InstallPath { get; set; }
    public required string ManifestPath { get; set; }
    public DateTimeOffset InstalledAt { get; set; }
    public string? AssetName { get; set; }
    public long AssetSizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public DateTimeOffset? ReleasePublishedAt { get; set; }
    public string Key => $"{RepoOwner}/{RepoName}";
}

public sealed class InstalledExtensionsManifest
{
    public int Version { get; set; } = 2;
    public List<InstalledExtension> Extensions { get; set; } = new();
}
