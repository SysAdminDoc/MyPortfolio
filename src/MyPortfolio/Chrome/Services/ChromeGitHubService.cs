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

    public ChromeGitHubService(GitHubClientFactory factory, HttpDownloader http)
    {
        _factory = factory;
        _http = http;
    }

    public async Task<List<ExtensionInfo>> DiscoverAsync(AppSettings cfg, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var client = _factory.Get(cfg);
        var owners = OwnerList(cfg);
        var found = new List<ExtensionInfo>();
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

                if (cfg.ChromeUseTopicFilter && !string.IsNullOrWhiteSpace(cfg.ChromeTopicFilter))
                {
                    var topics = await SafeGetTopics(client, repo);
                    if (topics is null || !topics.Any(t => t.Equals(cfg.ChromeTopicFilter, StringComparison.OrdinalIgnoreCase)))
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

    private async Task<ExtensionInfo?> ProbeRepoAsync(GitHubClient client, Repository repo, IProgress<string>? log, CancellationToken ct)
    {
        Release? release = null;
        try { release = await client.Repository.Release.GetLatest(repo.Owner.Login, repo.Name); }
        catch (NotFoundException) { /* no releases */ }
        catch (Exception ex) { log?.Report($"  ! release {repo.Name}: {ex.Message}"); }

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
        if (!hasManifest) return null;

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
        catch (Exception ex) { log?.Report($"  ~ manifest probe failed for {repo.Name}: {ex.Message}"); }

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
