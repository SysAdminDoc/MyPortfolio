using MyPortfolio.Android.Models;
using MyPortfolio.Common;
using Octokit;

namespace MyPortfolio.Android.Services;

public sealed class AndroidGitHubService
{
    private readonly GitHubClientFactory _factory;

    public AndroidGitHubService(GitHubClientFactory factory) { _factory = factory; }

    /// <summary>
    /// Discover Android apps: every repo whose latest release contains an .apk asset.
    /// We never install on Windows — the Android tab is strictly a "download .apk to PC"
    /// surface so the user can pull builds across to a device themselves.
    /// </summary>
    public async Task<List<AndroidAppInfo>> DiscoverAsync(AppSettings cfg, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var client = _factory.Get(cfg);
        var owners = OwnerList(cfg);
        var found = new List<AndroidAppInfo>();
        foreach (var owner in owners)
        {
            log?.Report($"Listing repos for {owner}...");
            IReadOnlyList<Repository> repos;
            try { repos = await client.Repository.GetAllForUser(owner); }
            catch (Exception ex) { log?.Report($"  ! {owner}: {ex.Message}"); continue; }

            log?.Report($"  {repos.Count} repos returned");
            foreach (var repo in repos)
            {
                ct.ThrowIfCancellationRequested();
                if (repo.Archived) continue;
                if (cfg.HiddenRepos.Contains($"{repo.Owner.Login}/{repo.Name}", StringComparer.OrdinalIgnoreCase)) continue;

                if (cfg.AndroidUseTopicFilter && !string.IsNullOrWhiteSpace(cfg.AndroidTopicFilter))
                {
                    var topics = await SafeGetTopics(client, repo);
                    if (topics is null || !topics.Any(t => t.Equals(cfg.AndroidTopicFilter, StringComparison.OrdinalIgnoreCase)))
                        continue;
                }

                var info = await ProbeRepoAsync(client, repo, log, ct);
                if (info != null) found.Add(info);
            }
        }
        return found;
    }

    private static List<string> OwnerList(AppSettings cfg)
    {
        var owners = new List<string>();
        if (!string.IsNullOrWhiteSpace(cfg.GitHubUser)) owners.Add(cfg.GitHubUser.Trim());
        owners.AddRange(cfg.ExtraOwners.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()));
        return owners.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<List<string>?> SafeGetTopics(GitHubClient client, Repository repo)
    {
        try
        {
            var topics = await client.Repository.GetAllTopics(repo.Id);
            return topics?.Names?.ToList();
        }
        catch { return null; }
    }

    private async Task<AndroidAppInfo?> ProbeRepoAsync(GitHubClient client, Repository repo, IProgress<string>? log, CancellationToken ct)
    {
        Release? release = null;
        try { release = await client.Repository.Release.GetLatest(repo.Owner.Login, repo.Name); }
        catch (NotFoundException) { return null; }
        catch (Exception ex) { log?.Report($"  ! release {repo.Name}: {ex.Message}"); return null; }

        if (release == null || release.Assets.Count == 0) return null;

        // Pick the largest .apk asset. Many releases ship debug + release; bigger is usually
        // the signed release build. AAB and source-zip aren't installable so we skip them.
        var apk = release.Assets
            .Where(a => a.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.Size)
            .FirstOrDefault();

        if (apk == null) return null;

        var sidecar = release.Assets.FirstOrDefault(a =>
            a.Name.Equals($"{apk.Name}.sha256.txt", StringComparison.OrdinalIgnoreCase)
            || a.Name.Equals($"{apk.Name}.sha256", StringComparison.OrdinalIgnoreCase));

        return new AndroidAppInfo
        {
            RepoOwner = repo.Owner.Login,
            RepoName = repo.Name,
            RepoUrl = repo.HtmlUrl,
            RepoDescription = repo.Description,
            Stars = repo.StargazersCount,
            LatestVersion = release.TagName,
            AssetUrl = apk.BrowserDownloadUrl,
            AssetName = apk.Name,
            AssetSizeBytes = apk.Size,
            Sha256Url = sidecar?.BrowserDownloadUrl,
            PublishedAt = release.PublishedAt,
            IconUrl = $"https://raw.githubusercontent.com/{repo.Owner.Login}/{repo.Name}/HEAD/logo.png"
        };
    }
}
