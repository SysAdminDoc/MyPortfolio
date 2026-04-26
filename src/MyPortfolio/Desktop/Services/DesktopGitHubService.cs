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
    public async Task<CatalogDiscoveryResult<AppInfo>> DiscoverAsync(AppSettings cfg, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var client = _factory.Get(cfg);
        var owners = OwnerList(cfg);
        var result = new CatalogDiscoveryResult<AppInfo>();
        var stopDiscovery = false;

        foreach (var owner in owners)
        {
            var ownerResult = result.Diagnostics.AddOwner(owner);
            log?.Report($"Listing repos for {owner}...");
            IReadOnlyList<Repository> repos;
            try
            {
                repos = await client.Repository.GetAllForUser(owner);
                result.Diagnostics.RateLimit = GitHubRateLimitSnapshot.FromClient(client) ?? result.Diagnostics.RateLimit;
            }
            catch (RateLimitExceededException ex)
            {
                result.Diagnostics.RateLimit = GitHubRateLimitSnapshot.FromPrimaryLimit(ex);
                ownerResult.Fail(GitHubDiscoveryDiagnostics.RateLimitMessage(ex));
                log?.Report($"  ! {owner}: {ownerResult.ErrorMessage}");
                break;
            }
            catch (SecondaryRateLimitExceededException)
            {
                ownerResult.Fail(GitHubDiscoveryDiagnostics.SecondaryRateLimitMessage());
                log?.Report($"  ! {owner}: {ownerResult.ErrorMessage}");
                break;
            }
            catch (Exception ex)
            {
                ownerResult.Fail(ex.Message);
                log?.Report($"  ! {owner}: {ex.Message}");
                continue;
            }

            ownerResult.RepositoriesReturned = repos.Count;
            log?.Report($"  {repos.Count} repos returned");
            foreach (var repo in repos)
            {
                ct.ThrowIfCancellationRequested();
                if (repo.Archived) { ownerResult.SkippedArchived++; continue; }
                if (cfg.HiddenRepos.Contains($"{repo.Owner.Login}/{repo.Name}", StringComparer.OrdinalIgnoreCase))
                {
                    ownerResult.SkippedHidden++;
                    continue;
                }

                try
                {
                    if (cfg.DesktopUseTopicFilter && !string.IsNullOrWhiteSpace(cfg.DesktopTopicFilter))
                    {
                        var topics = await SafeGetTopics(client, repo);
                        if (topics is null || !topics.Any(t => t.Equals(cfg.DesktopTopicFilter, StringComparison.OrdinalIgnoreCase)))
                        {
                            ownerResult.SkippedByTopic++;
                            continue;
                        }
                    }

                    var info = await ProbeRepoAsync(client, repo, log, ct);
                    result.Diagnostics.RateLimit = GitHubRateLimitSnapshot.FromClient(client) ?? result.Diagnostics.RateLimit;
                    if (info != null)
                    {
                        result.Items.Add(info);
                        ownerResult.MatchesFound++;
                    }
                }
                catch (RateLimitExceededException ex)
                {
                    result.Diagnostics.RateLimit = GitHubRateLimitSnapshot.FromPrimaryLimit(ex);
                    ownerResult.Fail(GitHubDiscoveryDiagnostics.RateLimitMessage(ex));
                    log?.Report($"  ! {owner}: {ownerResult.ErrorMessage}");
                    stopDiscovery = true;
                    break;
                }
                catch (SecondaryRateLimitExceededException)
                {
                    ownerResult.Fail(GitHubDiscoveryDiagnostics.SecondaryRateLimitMessage());
                    log?.Report($"  ! {owner}: {ownerResult.ErrorMessage}");
                    stopDiscovery = true;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    ownerResult.ProbeFailures++;
                    log?.Report($"  ! probe {repo.Name}: {ex.Message}");
                }
            }
            if (stopDiscovery) break;
        }

        result.Diagnostics.RateLimit ??= GitHubRateLimitSnapshot.FromClient(client);
        return result;
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
        catch (RateLimitExceededException) { throw; }
        catch (SecondaryRateLimitExceededException) { throw; }
        catch { return null; }
    }

    private async Task<AppInfo?> ProbeRepoAsync(GitHubClient client, Repository repo, IProgress<string>? log, CancellationToken ct)
    {
        Release? release = null;
        try { release = await client.Repository.Release.GetLatest(repo.Owner.Login, repo.Name); }
        catch (NotFoundException) { return null; }
        catch (RateLimitExceededException) { throw; }
        catch (SecondaryRateLimitExceededException) { throw; }
        catch (Exception) { throw; }

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
