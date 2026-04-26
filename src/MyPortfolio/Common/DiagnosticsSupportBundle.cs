using System.Text;
using System.Text.RegularExpressions;

namespace MyPortfolio.Common;

public static partial class DiagnosticsSupportBundle
{
    private const string RedactedToken = "[redacted token]";

    public static string Build(
        string tabName,
        DiscoveryDiagnostics diagnostics,
        IEnumerable<string> recentLogLines,
        string? currentToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# MyPortfolio diagnostics - {tabName}");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Summary: {Safe(diagnostics.Summary, currentToken)}");

        if (!string.IsNullOrWhiteSpace(diagnostics.WarningText))
            sb.AppendLine($"Warnings: {Safe(diagnostics.WarningText, currentToken)}");

        if (!string.IsNullOrWhiteSpace(diagnostics.RateLimitText))
            sb.AppendLine($"Rate limit: {Safe(diagnostics.RateLimitText, currentToken)}");

        sb.AppendLine();
        sb.AppendLine("## Owners");
        if (diagnostics.Owners.Count == 0)
        {
            sb.AppendLine("- No owner diagnostics captured yet.");
        }
        else
        {
            foreach (var owner in diagnostics.Owners)
            {
                sb.AppendLine($"- {Safe(owner.Owner, currentToken)}: {owner.StatusText}");
                sb.AppendLine($"  - Catalog: {Safe(owner.MatchDetailText, currentToken)}");
                sb.AppendLine($"  - Skipped: {Safe(owner.SkipDetailText, currentToken)}");
                sb.AppendLine($"  - Cache: {Safe(owner.CacheDetailText, currentToken)}");
                sb.AppendLine($"  - Probes: {Safe(owner.ProbeDetailText, currentToken)}");
                if (owner.HasErrorMessage)
                    sb.AppendLine($"  - Error: {Safe(owner.ErrorMessage!, currentToken)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Recent Activity");
        var lines = recentLogLines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => Safe(line, currentToken))
            .ToList();

        if (lines.Count == 0)
        {
            sb.AppendLine("- No recent activity captured.");
        }
        else
        {
            foreach (var line in lines)
                sb.AppendLine($"- {line}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Safe(string value, string? currentToken)
    {
        var sanitized = value;
        if (!string.IsNullOrWhiteSpace(currentToken))
            sanitized = sanitized.Replace(currentToken.Trim(), RedactedToken, StringComparison.Ordinal);

        sanitized = GitHubTokenPattern().Replace(sanitized, RedactedToken);
        sanitized = TokenAssignmentPattern().Replace(sanitized, match => $"{match.Groups[1].Value}{RedactedToken}");
        return sanitized;
    }

    [GeneratedRegex(@"\b(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9_]{20,})\b")]
    private static partial Regex GitHubTokenPattern();

    [GeneratedRegex(@"(?i)\b((?:authorization:\s*(?:bearer|token)\s+|(?:access_)?token\s*[=:]\s*))([^\s,;]+)")]
    private static partial Regex TokenAssignmentPattern();
}
