using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using MyPortfolio.Android.Models;

namespace MyPortfolio.Android.Services;

public static class ApkManifestReader
{
    private const long MaxManifestBytes = 4 * 1024 * 1024;
    private static readonly XNamespace AndroidNamespace = "http://schemas.android.com/apk/res/android";

    public static ApkManifestMetadata? TryRead(string apkPath, out string? error)
    {
        error = null;
        try
        {
            using var fs = File.OpenRead(apkPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("AndroidManifest.xml", StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                error = "AndroidManifest.xml was not found in the APK.";
                return null;
            }

            if (entry.Length <= 0 || entry.Length > MaxManifestBytes)
            {
                error = "AndroidManifest.xml is empty or unexpectedly large.";
                return null;
            }

            using var manifest = entry.Open();
            using var ms = new MemoryStream((int)entry.Length);
            manifest.CopyTo(ms);
            ms.Position = 0;

            var document = LooksLikeTextXml(ms)
                ? LoadPlainXml(ms)
                : LoadBinaryXml(ms);

            return FromDocument(document);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static bool LooksLikeTextXml(MemoryStream stream)
    {
        stream.Position = 0;
        Span<byte> prefix = stackalloc byte[4];
        var read = stream.Read(prefix);
        stream.Position = 0;

        if (read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
            return read >= 4 && prefix[3] == (byte)'<';

        for (var i = 0; i < read; i++)
        {
            var b = prefix[i];
            if (b is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') continue;
            return b == (byte)'<';
        }

        return false;
    }

    private static XDocument LoadPlainXml(MemoryStream stream)
    {
        stream.Position = 0;
        return XDocument.Load(stream, LoadOptions.None);
    }

    private static XDocument LoadBinaryXml(MemoryStream stream)
    {
        stream.Position = 0;
        using var reader = new global::AndroidXml.AndroidXmlReader(stream);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static ApkManifestMetadata? FromDocument(XDocument document)
    {
        var root = document.Root;
        if (root is null || !root.Name.LocalName.Equals("manifest", StringComparison.OrdinalIgnoreCase))
            return null;

        var metadata = new ApkManifestMetadata
        {
            PackageName = Normalize(root.Attribute("package")?.Value),
            VersionCode = Normalize(AttributeValue(root, "versionCode")),
            VersionName = Normalize(AttributeValue(root, "versionName"))
        };

        return metadata.HasAny ? metadata : null;
    }

    private static string? AttributeValue(XElement element, string localName)
        => element.Attribute(AndroidNamespace + localName)?.Value
           ?? element.Attributes().FirstOrDefault(a =>
               a.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
