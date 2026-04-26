using System.IO;
using System.IO.Compression;
using System.Text.Json;
using MyPortfolio.Common;
using MyPortfolio.Chrome.Models;
using Octokit;

namespace MyPortfolio.Chrome.Services;

public sealed class ChromeGitHubService
{
    private readonly GitHubClientFactory _factory;
    private readonly HttpDownloader _http;
    private readonly DiscoveryProbeCache<ExtensionInfo> _probeCache = new(DiscoveryCacheDefaults.BriefProbeTtl);
    private readonly DiscoveryProbeCache<List<string>> _topicCache = new(DiscoveryCacheDefaults.BriefProbeTtl);

    public ChromeGitHubService(GitHubClientFactory factory, HttpDownloader http)
    {
        _factory = factory;
        _http = http;
    }

    public async Task<CatalogDiscoveryResult<ExtensionInfo>> DiscoverAsync(
        AppSettings cfg,
        IProgress<string>? log = null,
        CancellationToken ct = default,
        IProgress<DiscoveryProgress>? progress = null)
    {
        var client = _factory.Get(cfg);
        var owners = OwnerList(cfg);
        var cacheScope = DiscoveryCacheKey.Scope(cfg);
        var result = new CatalogDiscoveryResult<ExtensionInfo>();
        var stopDiscovery = false;
        _probeCache.ClearExpired();
        _topicCache.ClearExpired();

        foreach (var owner in owners)
        {
            var ownerResult = result.Diagnostics.AddOwner(owner);
            progress?.Report(new DiscoveryProgress("Listing owners", result.Diagnostics.OwnerCount, owners.Count, owner));
            log?.Report($"Listing repos for {owner}...");
            IReadOnlyList<Repository> repos;
            try
            {
                repos = await client.Repository.GetAllForUser(owner, new ApiOptions { PageSize = 100 });
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
            var repoIndex = 0;
            foreach (var repo in repos)
            {
                ct.ThrowIfCancellationRequested();
                repoIndex++;
                progress?.Report(new DiscoveryProgress($"Scanning {owner}", repoIndex, repos.Count, repo.Name));
                if (repo.Archived) { ownerResult.SkippedArchived++; continue; }
                if (cfg.HiddenRepos.Contains($"{repo.Owner.Login}/{repo.Name}", StringComparer.OrdinalIgnoreCase))
                {
                    ownerResult.SkippedHidden++;
                    continue;
                }

                try
                {
                    if (cfg.ChromeUseTopicFilter && !string.IsNullOrWhiteSpace(cfg.ChromeTopicFilter))
                    {
                        var topics = await SafeGetTopics(client, repo, cacheScope, ownerResult);
                        if (topics is null || !topics.Any(t => t.Equals(cfg.ChromeTopicFilter, StringComparison.OrdinalIgnoreCase)))
                        {
                            ownerResult.SkippedByTopic++;
                            continue;
                        }
                    }

                    var info = await ProbeRepoAsync(client, repo, log, cacheScope, ownerResult, ct);
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
        progress?.Report(new DiscoveryProgress("Finalizing catalog", result.Items.Count, result.Items.Count, $"{result.Items.Count} match(es)"));
        return result;
    }

    private static List<string> OwnerList(AppSettings cfg)
    {
        var owners = new List<string>();
        if (!string.IsNullOrWhiteSpace(cfg.GitHubUser)) owners.Add(cfg.GitHubUser.Trim());
        owners.AddRange(cfg.ExtraOwners.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()));
        return owners.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<List<string>?> SafeGetTopics(GitHubClient client, Repository repo, string cacheScope, OwnerDiscoveryResult ownerResult)
    {
        var cacheKey = DiscoveryCacheKey.Repo(cacheScope, repo.Owner.Login, repo.Name, "topics");
        if (_topicCache.TryGet(cacheKey, out var cachedTopics))
        {
            ownerResult.CacheHits++;
            return cachedTopics;
        }

        try
        {
            var topics = await client.Repository.GetAllTopics(repo.Id);
            var names = topics?.Names?.ToList();
            if (names != null) _topicCache.Set(cacheKey, names);
            return names;
        }
        catch (RateLimitExceededException) { throw; }
        catch (SecondaryRateLimitExceededException) { throw; }
        catch { return null; }
    }

    private async Task<ExtensionInfo?> ProbeRepoAsync(GitHubClient client, Repository repo, IProgress<string>? log, string cacheScope, OwnerDiscoveryResult ownerResult, CancellationToken ct)
    {
        var cacheKey = DiscoveryCacheKey.Repo(cacheScope, repo.Owner.Login, repo.Name, "chrome-probe");
        if (_probeCache.TryGet(cacheKey, out var cachedInfo))
        {
            ownerResult.CacheHits++;
            return cachedInfo;
        }

        Release? release = null;
        try { release = await client.Repository.Release.GetLatest(repo.Owner.Login, repo.Name); }
        catch (NotFoundException) { /* no releases */ }
        catch (RateLimitExceededException) { throw; }
        catch (SecondaryRateLimitExceededException) { throw; }
        catch (Exception) { throw; }

        ReleaseAsset? asset = null;
        if (release != null)
        {
            asset = release.Assets
                .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                            a.Name.EndsWith(".crx", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(a => a.Size)
                .FirstOrDefault();
        }

        var hasManifest = asset != null || await RepoHasManifestAsync(client, repo, ct);
        if (!hasManifest)
        {
            _probeCache.Set(cacheKey, null);
            return null;
        }

        var info = new ExtensionInfo
        {
            RepoOwner = repo.Owner.Login,
            RepoName = repo.Name,
            RepoUrl = repo.HtmlUrl,
            RepoDescription = repo.Description,
            Stars = repo.StargazersCount,
            LatestVersion = release?.TagName,
            AssetUrl = asset?.BrowserDownloadUrl,
            AssetName = asset?.Name,
            AssetSizeBytes = asset?.Size ?? 0,
            PublishedAt = release?.PublishedAt
        };

        try
        {
            var manifestJson = await TryReadManifestAsync(client, repo, asset, ct);
            if (manifestJson != null) Enrich(info, manifestJson);
        }
        catch (RateLimitExceededException) { throw; }
        catch (SecondaryRateLimitExceededException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { log?.Report($"  ~ manifest probe failed for {repo.Name}: {ex.Message}"); }

        _probeCache.Set(cacheKey, info);
        return info;
    }

    private static readonly string[] CommonManifestPaths = ["manifest.json", "extension/manifest.json", "src/manifest.json", "dist/manifest.json", "public/manifest.json"];

    private static async Task<bool> RepoHasManifestAsync(GitHubClient client, Repository repo, CancellationToken ct)
    {
        foreach (var path in CommonManifestPaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var contents = await client.Repository.Content.GetAllContents(repo.Owner.Login, repo.Name, path);
                if (contents.Count > 0) return true;
            }
            catch (NotFoundException) { /* try next */ }
            catch (RateLimitExceededException) { throw; }
            catch (SecondaryRateLimitExceededException) { throw; }
            catch { return false; }
        }
        return false;
    }

    private async Task<JsonDocument?> TryReadManifestAsync(GitHubClient client, Repository repo, ReleaseAsset? asset, CancellationToken ct)
    {
        if (asset != null && asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await _http.DownloadBytesAsync(asset.BrowserDownloadUrl, null, ct);
            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase));
            if (entry != null)
            {
                using var es = entry.Open();
                using var reader = new StreamReader(es);
                var json = await reader.ReadToEndAsync(ct);
                return JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
        }

        foreach (var path in CommonManifestPaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var contents = await client.Repository.Content.GetAllContents(repo.Owner.Login, repo.Name, path);
                var c = contents.FirstOrDefault();
                if (c?.Content != null)
                    return JsonDocument.Parse(c.Content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (NotFoundException) { /* try next */ }
            catch (RateLimitExceededException) { throw; }
            catch (SecondaryRateLimitExceededException) { throw; }
            catch { return null; }
        }
        return null;
    }

    private static void Enrich(ExtensionInfo info, JsonDocument doc)
    {
        var root = doc.RootElement;
        if (root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            info.ManifestName = name.GetString();
        if (root.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.String)
            info.ManifestVersion = ver.GetString();
        if (root.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
            info.ManifestDescription = desc.GetString();

        if (root.TryGetProperty("icons", out var icons) && icons.ValueKind == JsonValueKind.Object)
        {
            string? bestPath = null;
            int bestSize = 0;
            foreach (var prop in icons.EnumerateObject())
            {
                if (int.TryParse(prop.Name, out var size) && size > bestSize && prop.Value.ValueKind == JsonValueKind.String)
                {
                    bestSize = size;
                    bestPath = prop.Value.GetString();
                }
            }
            if (bestPath != null)
                info.IconUrl = $"https://raw.githubusercontent.com/{info.RepoOwner}/{info.RepoName}/HEAD/{bestPath.TrimStart('/')}";
        }
    }
}
