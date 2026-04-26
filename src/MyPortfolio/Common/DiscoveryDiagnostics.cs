using Octokit;

namespace MyPortfolio.Common;

public sealed class CatalogDiscoveryResult<T>
{
    public List<T> Items { get; } = new();
    public DiscoveryDiagnostics Diagnostics { get; } = new();
}

public sealed class DiscoveryDiagnostics
{
    public List<OwnerDiscoveryResult> Owners { get; } = new();
    public GitHubRateLimitSnapshot? RateLimit { get; set; }

    public int OwnerCount => Owners.Count;
    public int SuccessfulOwners => Owners.Count(o => !o.Failed);
    public int FailedOwners => Owners.Count(o => o.Failed);
    public int RepositoryCount => Owners.Sum(o => o.RepositoriesReturned);
    public int MatchCount => Owners.Sum(o => o.MatchesFound);
    public int ProbeFailureCount => Owners.Sum(o => o.ProbeFailures);
    public int CacheHitCount => Owners.Sum(o => o.CacheHits);
    public int SkippedArchivedCount => Owners.Sum(o => o.SkippedArchived);
    public int SkippedHiddenCount => Owners.Sum(o => o.SkippedHidden);
    public int SkippedByTopicCount => Owners.Sum(o => o.SkippedByTopic);
    public int SkippedCount => SkippedArchivedCount + SkippedHiddenCount + SkippedByTopicCount;
    public bool HasDetails => OwnerCount > 0 || RateLimit is not null;
    public bool HasOwnerDetails => OwnerCount > 0;
    public bool HasWarnings => FailedOwners > 0 || ProbeFailureCount > 0 || RateLimit?.IsLow == true;

    public OwnerDiscoveryResult AddOwner(string owner)
    {
        var result = new OwnerDiscoveryResult(owner);
        Owners.Add(result);
        return result;
    }

    public string Summary
    {
        get
        {
            if (OwnerCount == 0) return string.Empty;
            if (OwnerCount == 1) return Owners[0].Summary;
            return $"{SuccessfulOwners}/{OwnerCount} owners loaded / {RepositoryCount} repos scanned / {MatchCount} match(es){SkipSuffix(SkippedCount)}{CacheSuffix(CacheHitCount)}";
        }
    }

    public string WarningText
    {
        get
        {
            var warnings = Owners
                .Where(o => o.HasWarning)
                .Select(o => o.WarningSummary)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            if (RateLimit?.IsLow == true)
                warnings.Add($"GitHub API quota is low: {RateLimit.Summary}");

            return string.Join("  ", warnings);
        }
    }

    public string RateLimitText => RateLimit?.Summary ?? string.Empty;

    private static string CacheSuffix(int cacheHits)
        => cacheHits > 0 ? $" / {cacheHits} cached" : string.Empty;

    private static string SkipSuffix(int skipped)
        => skipped > 0 ? $" / {skipped} skipped" : string.Empty;
}

public sealed class OwnerDiscoveryResult
{
    public OwnerDiscoveryResult(string owner) { Owner = owner; }

    public string Owner { get; }
    public int RepositoriesReturned { get; set; }
    public int MatchesFound { get; set; }
    public int SkippedArchived { get; set; }
    public int SkippedHidden { get; set; }
    public int SkippedByTopic { get; set; }
    public int ProbeFailures { get; set; }
    public int CacheHits { get; set; }
    public bool Failed { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool HasWarning => Failed || ProbeFailures > 0;
    public int SkippedCount => SkippedArchived + SkippedHidden + SkippedByTopic;
    public bool HasErrorMessage => Failed && !string.IsNullOrWhiteSpace(ErrorMessage);
    public string StatusText => Failed ? "Failed" : "Loaded";
    public string MatchDetailText => Failed
        ? "No catalog data loaded"
        : $"{MatchesFound:N0} match(es) from {RepositoriesReturned:N0} repo(s)";
    public string SkipDetailText => SkippedCount == 0
        ? "0 skipped"
        : $"{SkippedCount:N0} skipped: {SkipBreakdown}";
    public string CacheDetailText => $"{CacheHits:N0} cached";
    public string ProbeDetailText => $"{ProbeFailures:N0} probe issue(s)";

    public string Summary => Failed
        ? $"{Owner}: failed"
        : $"{Owner}: {MatchesFound} match(es) from {RepositoriesReturned} repo(s){SkipSuffix}{(CacheHits > 0 ? $" / {CacheHits} cached" : string.Empty)}";

    private string SkipBreakdown
    {
        get
        {
            var parts = new List<string>();
            if (SkippedArchived > 0) parts.Add($"{SkippedArchived:N0} archived");
            if (SkippedHidden > 0) parts.Add($"{SkippedHidden:N0} hidden");
            if (SkippedByTopic > 0) parts.Add($"{SkippedByTopic:N0} topic-filtered");
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }
    }

    private string SkipSuffix
    {
        get
        {
            var parts = new List<string>();
            if (SkippedArchived > 0) parts.Add($"{SkippedArchived:N0} archived");
            if (SkippedHidden > 0) parts.Add($"{SkippedHidden:N0} hidden");
            if (SkippedByTopic > 0) parts.Add($"{SkippedByTopic:N0} topic-filtered");
            return parts.Count == 0 ? string.Empty : $" / skipped {string.Join(", ", parts)}";
        }
    }

    public string WarningSummary
    {
        get
        {
            if (Failed) return $"{Owner}: {ErrorMessage ?? "discovery failed"}";
            return ProbeFailures > 0 ? $"{Owner}: {ProbeFailures} repo probe issue(s)" : string.Empty;
        }
    }

    public void Fail(string message)
    {
        Failed = true;
        ErrorMessage = message;
    }
}

public sealed class GitHubRateLimitSnapshot
{
    public GitHubRateLimitSnapshot(int limit, int remaining, DateTimeOffset resetUtc)
    {
        Limit = limit;
        Remaining = remaining;
        ResetUtc = resetUtc.ToUniversalTime();
    }

    public int Limit { get; }
    public int Remaining { get; }
    public DateTimeOffset ResetUtc { get; }
    public bool IsLow => Limit > 0 && (Remaining <= 25 || Remaining <= Math.Max(1, Limit / 10));
    public string Summary => $"GitHub API: {Remaining}/{Limit} requests left, resets {ResetUtc.ToLocalTime():h:mm tt}";

    public static GitHubRateLimitSnapshot? FromClient(GitHubClient client)
        => FromRateLimit(client.GetLastApiInfo()?.RateLimit);

    public static GitHubRateLimitSnapshot? FromRateLimit(RateLimit? rateLimit)
        => rateLimit is null ? null : new(rateLimit.Limit, rateLimit.Remaining, rateLimit.Reset);

    public static GitHubRateLimitSnapshot FromPrimaryLimit(RateLimitExceededException ex)
        => new(ex.Limit, ex.Remaining, ex.Reset);
}

public static class GitHubDiscoveryDiagnostics
{
    public static string RateLimitMessage(RateLimitExceededException ex)
        => $"GitHub primary rate limit reached; retry after {ex.Reset.ToLocalTime():h:mm tt}. Add a token or wait for the reset before refreshing again.";

    public static string SecondaryRateLimitMessage()
        => "GitHub secondary rate limit reached; wait at least one minute before refreshing again, then back off longer if it repeats.";
}
