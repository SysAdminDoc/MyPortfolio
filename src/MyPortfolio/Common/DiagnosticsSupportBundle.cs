using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MyPortfolio.Common;

public static partial class DiagnosticsSupportBundle
{
    private const string RedactedToken = "[redacted token]";

    public static string SaveToFile(
        string tabName,
        DiscoveryDiagnostics diagnostics,
        IEnumerable<string> recentLogLines,
        string? currentToken)
    {
        var bundle = Build(tabName, diagnostics, recentLogLines, currentToken);
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPortfolio",
            "diagnostics");
        Directory.CreateDirectory(directory);

        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        var fileName = $"MyPortfolio-diagnostics-{SafeFileSegment(tabName)}-{timestamp}.txt";
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, bundle, Encoding.UTF8);
        return path;
    }

    public static void RevealFile(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
        {
            UseShellExecute = true
        });
    }

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

    private static string SafeFileSegment(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch is '-' or '_')
                sb.Append('-');
        }

        var normalized = DashPattern().Replace(sb.ToString(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "catalog" : normalized;
    }

    [GeneratedRegex(@"\b(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9_]{20,})\b")]
    private static partial Regex GitHubTokenPattern();

    [GeneratedRegex(@"(?i)\b((?:authorization:\s*(?:bearer|token)\s+|(?:access_)?token\s*[=:]\s*))([^\s,;]+)")]
    private static partial Regex TokenAssignmentPattern();

    [GeneratedRegex("-{2,}")]
    private static partial Regex DashPattern();
}
