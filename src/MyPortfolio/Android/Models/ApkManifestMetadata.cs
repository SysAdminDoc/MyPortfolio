namespace MyPortfolio.Android.Models;

public sealed class ApkManifestMetadata
{
    public string? PackageName { get; init; }
    public string? VersionCode { get; init; }
    public string? VersionName { get; init; }

    public bool HasAny =>
        !string.IsNullOrWhiteSpace(PackageName) ||
        !string.IsNullOrWhiteSpace(VersionCode) ||
        !string.IsNullOrWhiteSpace(VersionName);
}
