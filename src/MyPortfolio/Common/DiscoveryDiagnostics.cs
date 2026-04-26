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
    public bool HasDetails => OwnerCount > 0 || RateLimit is not null;
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
            return $"{SuccessfulOwners}/{OwnerCount} owners loaded / {RepositoryCount} repos scanned / {MatchCount} match(es)";
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
    public bool Failed { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool HasWarning => Failed || ProbeFailures > 0;

    public string Summary => Failed
        ? $"{Owner}: failed"
        : $"{Owner}: {MatchesFound} match(es) from {RepositoriesReturned} repo(s)";

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
        => $"GitHub primary rate limit reached; retry after {ex.Reset.ToLocalTime():h:mm tt}.";

    public static string SecondaryRateLimitMessage()
        => "GitHub secondary rate limit reached; pause before refreshing again.";
}
