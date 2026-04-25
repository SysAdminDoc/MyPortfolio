using Octokit;

namespace MyPortfolio.Common;

/// <summary>
/// Cached Octokit client. Rebuilt lazily when the auth token changes so that
/// updating the PAT in Settings doesn't require restarting the app.
/// </summary>
public sealed class GitHubClientFactory
{
    private GitHubClient? _client;
    private string? _activeToken;

    public GitHubClient Get(AppSettings cfg)
    {
        if (_client != null && _activeToken == cfg.GitHubToken) return _client;
        var product = new ProductHeaderValue("MyPortfolio", "0.1.0");
        var c = new GitHubClient(product);
        if (!string.IsNullOrWhiteSpace(cfg.GitHubToken))
            c.Credentials = new Credentials(cfg.GitHubToken);
        _client = c;
        _activeToken = cfg.GitHubToken;
        return c;
    }
}
