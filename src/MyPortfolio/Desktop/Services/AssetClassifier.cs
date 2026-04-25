using System.Diagnostics;
using System.IO;
using System.Text;
using MyPortfolio.Desktop.Models;

namespace MyPortfolio.Desktop.Services;

/// <summary>
/// Classifies a release asset by file name and (when on disk) by signature/PE inspection.
/// MSI: extension. EXE installers split into Inno / NSIS / Generic — name hints first,
/// then optional content scan when the file has been downloaded.
/// </summary>
public static class AssetClassifier
{
    public static ArtifactKind ClassifyByName(string assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName)) return ArtifactKind.Unknown;
        var n = assetName.ToLowerInvariant();

        if (n.EndsWith(".msi")) return ArtifactKind.Msi;
        if (n.EndsWith(".zip")) return ArtifactKind.PortableZip;
        if (n.EndsWith(".exe"))
        {
            if (n.Contains("innosetup") || n.Contains("inno-setup")) return ArtifactKind.Inno;
            if (n.Contains("nsis")) return ArtifactKind.Nsis;
            if (n.Contains("setup") || n.Contains("installer") || n.EndsWith("-setup.exe") || n.EndsWith("-installer.exe"))
                return ArtifactKind.GenericExe;
            return ArtifactKind.GenericExe;
        }
        return ArtifactKind.Unknown;
    }

    public static ArtifactKind RefineFromFile(string path, ArtifactKind hint)
    {
        if (hint == ArtifactKind.Msi || hint == ArtifactKind.PortableZip) return hint;
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(path);
            var meta = string.Join(" | ",
                new[] { fvi.CompanyName, fvi.ProductName, fvi.FileDescription, fvi.OriginalFilename, fvi.Comments }
                    .Where(s => !string.IsNullOrEmpty(s)));
            var metaLower = meta.ToLowerInvariant();
            if (metaLower.Contains("inno setup")) return ArtifactKind.Inno;
            if (metaLower.Contains("nullsoft") || metaLower.Contains("nsis")) return ArtifactKind.Nsis;

            const int maxScan = 4 * 1024 * 1024;
            using var fs = File.OpenRead(path);
            int len = (int)Math.Min(fs.Length, maxScan);
            var buf = new byte[len];
            int read = fs.Read(buf, 0, len);
            if (Contains(buf, read, "Inno Setup Setup Data")) return ArtifactKind.Inno;
            if (Contains(buf, read, "Nullsoft Install System")) return ArtifactKind.Nsis;
            if (Contains(buf, read, "Nullsoft.NSIS")) return ArtifactKind.Nsis;
        }
        catch { /* fall through and return hint */ }

        return hint == ArtifactKind.Unknown ? ArtifactKind.GenericExe : hint;
    }

    private static bool Contains(byte[] haystack, int len, string needle)
    {
        var nb = Encoding.ASCII.GetBytes(needle);
        if (nb.Length == 0 || nb.Length > len) return false;
        int last = len - nb.Length;
        for (int i = 0; i <= last; i++)
        {
            int j = 0;
            while (j < nb.Length && haystack[i + j] == nb[j]) j++;
            if (j == nb.Length) return true;
        }
        return false;
    }
}
