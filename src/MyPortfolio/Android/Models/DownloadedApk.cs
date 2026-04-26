namespace MyPortfolio.Android.Models;

public sealed class DownloadedApk
{
    public required string RepoOwner { get; set; }
    public required string RepoName { get; set; }
    public required string Version { get; set; }
    public required string AssetName { get; set; }
    public required string FilePath { get; set; }
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public string? PackageName { get; set; }
    public string? VersionCode { get; set; }
    public string? VersionName { get; set; }
    public DateTimeOffset DownloadedAt { get; set; }
    public string Key => $"{RepoOwner}/{RepoName}";
    public bool HasManifestMetadata =>
        !string.IsNullOrWhiteSpace(PackageName) ||
        !string.IsNullOrWhiteSpace(VersionCode) ||
        !string.IsNullOrWhiteSpace(VersionName);
}

public sealed class DownloadedApksManifest
{
    public int Version { get; set; } = 2;
    public List<DownloadedApk> Apks { get; set; } = new();
}
