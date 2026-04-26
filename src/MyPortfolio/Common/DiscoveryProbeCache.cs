using System.Security.Cryptography;
using System.Text;

namespace MyPortfolio.Common;

public static class DiscoveryCacheDefaults
{
    public static readonly TimeSpan BriefProbeTtl = TimeSpan.FromMinutes(5);
}

public sealed class DiscoveryProbeCache<T> where T : class
{
    private readonly TimeSpan _ttl;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public DiscoveryProbeCache(TimeSpan ttl) { _ttl = ttl; }

    public bool TryGet(string key, out T? value)
    {
        lock (_entries)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
                {
                    value = entry.Value;
                    return true;
                }

                _entries.Remove(key);
            }
        }

        value = null;
        return false;
    }

    public void Set(string key, T? value)
    {
        lock (_entries)
            _entries[key] = new Entry(value, DateTimeOffset.UtcNow.Add(_ttl));
    }

    public void ClearExpired()
    {
        lock (_entries)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in _entries.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToList())
                _entries.Remove(key);
        }
    }

    private sealed class Entry
    {
        public Entry(T? value, DateTimeOffset expiresAtUtc)
        {
            Value = value;
            ExpiresAtUtc = expiresAtUtc;
        }

        public T? Value { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
    }
}

public static class DiscoveryCacheKey
{
    public static string Scope(AppSettings cfg)
    {
        var owner = string.IsNullOrWhiteSpace(cfg.GitHubUser) ? "none" : cfg.GitHubUser.Trim().ToLowerInvariant();
        return $"{owner}|{TokenFingerprint(cfg.GitHubToken)}";
    }

    public static string Repo(string scope, string owner, string repo, string purpose)
        => $"{scope}|{purpose}|{owner.Trim().ToLowerInvariant()}/{repo.Trim().ToLowerInvariant()}";

    private static string TokenFingerprint(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return "anonymous";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
