using System.IO;
using System.Net.Http;

namespace MyPortfolio.Common;

/// <summary>
/// Single shared HttpClient for asset / icon downloads. Both file-stream and
/// in-memory variants are supported because the Chrome extension installer
/// needs the bytes to inspect the manifest before extracting, while the
/// Desktop installer streams large MSIs straight to disk.
/// </summary>
public sealed class HttpDownloader
{
    private readonly HttpClient _http;

    public HttpDownloader()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MyPortfolio/0.1.0");
    }

    public async Task DownloadToFileAsync(string url, string destination, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await stream.ReadAsync(buf, ct)) > 0)
        {
            await fs.WriteAsync(buf.AsMemory(0, read), ct);
            readTotal += read;
            progress?.Report(readTotal);
        }
    }

    public async Task<byte[]> DownloadBytesAsync(string url, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await stream.ReadAsync(buf, ct)) > 0)
        {
            await ms.WriteAsync(buf.AsMemory(0, read), ct);
            readTotal += read;
            progress?.Report(readTotal);
        }
        return ms.ToArray();
    }

    public async Task<string?> TryDownloadTextAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch { return null; }
    }

    public async Task<byte[]?> TryDownloadBytesAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch { return null; }
    }
}
