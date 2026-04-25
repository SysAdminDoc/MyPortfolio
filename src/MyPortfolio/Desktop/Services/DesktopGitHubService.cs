using MyPortfolio.Common;
using MyPortfolio.Desktop.Models;
using Octokit;

namespace MyPortfolio.Desktop.Services;

public sealed class DesktopGitHubService
{
    private readonly GitHubClientFactory _factory;
    private readonly HttpDownloader _http;

    public DesktopGitHubService(GitHubClientFactory factory, HttpDownloader http)
    {
        _factory = factory;
        _http = http;
    }

    /// <summary>
    /// Discover desktop-app candidates: every repo whose latest release ships an MSI,
    /// EXE installer, or portable ZIP that the AssetClassifier accepts.
    /// </summary>
    public async Task<List<AppInfo>> DiscoverAsync(AppSettings cfg, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var client = _factory.Get(cfg);
        var owners = OwnerList(cfg);
        var found = new List<AppInfo>();
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

                if (cfg.DesktopUseTopicFilter && !string.IsNullOrWhiteSpace(cfg.DesktopTopicFilter))
                {
                    var topics = await SafeGetTopics(client, repo);
                    if (topics is null || !topics.Any(t => t.Equals(cfg.DesktopTopicFilter, StringComparison.OrdinalIgnoreCase)))
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

    private async Task<AppInfo?> ProbeRepoAsync(GitHubClient client, Repository repo, IProgress<string>? log, CancellationToken ct)
    {
        Release? release = null;
        try { release = await client.Repository.Release.GetLatest(repo.Owner.Login, repo.Name); }
        catch (NotFoundException) { return null; }
        catch (Exception ex) { log?.Report($"  ! release {repo.Name}: {ex.Message}"); return null; }

        if (release == null || release.Assets.Count == 0) return null;

        var classified = release.Assets
            .Select(a => (Asset: a, Kind: AssetClassifier.ClassifyByName(a.Name)))
            .Where(t => t.Kind != ArtifactKind.Unknown)
            .OrderByDescending(t => t.Kind.Priority())
            .ThenByDescending(t => t.Asset.Size)
            .ToList();

        var best = classified.FirstOrDefault();
        if (best.Asset == null) return null;

        var sidecar = release.Assets.FirstOrDefault(a =>
            a.Name.Equals($"{best.Asset.Name}.sha256.txt", StringComparison.OrdinalIgnoreCase)
            || a.Name.Equals($"{best.Asset.Name}.sha256", StringComparison.OrdinalIgnoreCase));

        return new AppInfo
        {
            RepoOwner = repo.Owner.Login,
            RepoName = repo.Name,
            RepoUrl = repo.HtmlUrl,
            RepoDescription = repo.Description,
            Stars = repo.StargazersCount,
            LatestVersion = release.TagName,
            AssetUrl = best.Asset.BrowserDownloadUrl,
            AssetName = best.Asset.Name,
            AssetSizeBytes = best.Asset.Size,
            Kind = best.Kind,
            Sha256Url = sidecar?.BrowserDownloadUrl,
            PublishedAt = release.PublishedAt,
            IconUrl = $"https://raw.githubusercontent.com/{repo.Owner.Login}/{repo.Name}/HEAD/logo.png"
        };
    }
}
