namespace MyPortfolio.Common;

/// <summary>
/// One settings file for the whole portfolio app. GitHub user / token /
/// extra owners / hidden repos are shared; per-tab knobs (topic filter,
/// hash sidecar verification, install root, preferred browser) live here too.
/// </summary>
public sealed class AppSettings
{
    // Shared
    public string GitHubUser { get; set; } = "SysAdminDoc";
    public string? GitHubToken { get; set; }
    public List<string> ExtraOwners { get; set; } = new();
    public List<string> HiddenRepos { get; set; } = new();
    public bool RefreshOnLaunch { get; set; } = false;

    // Desktop tab
    public bool DesktopUseTopicFilter { get; set; } = false;
    public string DesktopTopicFilter { get; set; } = "windows-app";
    public bool DesktopVerifyHashSidecar { get; set; } = true;
    public string? DesktopInstallRootOverride { get; set; }
    public DateTimeOffset? DesktopLastRefreshUtc { get; set; }

    // Chrome tab
    public bool ChromeUseTopicFilter { get; set; } = false;
    public string ChromeTopicFilter { get; set; } = "chrome-extension";
    public string? PreferredBrowserPath { get; set; }
    public bool LaunchBrowserAfterInstall { get; set; } = false;
    public DateTimeOffset? ChromeLastRefreshUtc { get; set; }

    // Android tab — download only, never installs
    public bool AndroidUseTopicFilter { get; set; } = false;
    public string AndroidTopicFilter { get; set; } = "android-app";
    public string? AndroidDownloadFolderOverride { get; set; }
    public bool AndroidVerifyHashSidecar { get; set; } = true;
    public DateTimeOffset? AndroidLastRefreshUtc { get; set; }
}
