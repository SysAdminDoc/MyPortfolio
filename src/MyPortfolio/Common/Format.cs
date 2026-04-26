namespace MyPortfolio.Common;

internal static class Format
{
    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "?";
        string[] u = ["B", "KB", "MB", "GB"];
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }

    public static string LastRefresh(DateTimeOffset? refreshedUtc)
    {
        if (refreshedUtc is null) return "Last refreshed: never";
        var local = refreshedUtc.Value.ToLocalTime();
        return $"Last refreshed: {local:MMM d, h:mm tt}";
    }

    public static string LocalDateTime(DateTimeOffset? timestamp)
    {
        if (timestamp is null) return "Unavailable";
        return timestamp.Value.ToLocalTime().ToString("MMM d, yyyy h:mm tt");
    }

    public static string ShortSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return "Unavailable";
        var clean = sha.Trim();
        return clean.Length <= 12 ? clean : clean[..12];
    }
}
