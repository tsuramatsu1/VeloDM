namespace VeloDM.Core;

public static class FileNameHelper
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "download";
        var chars = name.Trim().Trim('"', '\'').ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(Invalid, chars[i]) >= 0) chars[i] = '_';
        var result = new string(chars).Trim().TrimEnd('.');
        if (result.Length > 200)
        {
            var ext = Path.GetExtension(result);
            result = result[..(200 - ext.Length)] + ext;
        }
        return result.Length == 0 ? "download" : result;
    }

    public static string FromUrl(Uri uri)
    {
        var last = uri.Segments.Length > 0 ? uri.Segments[^1] : "";
        var name = Uri.UnescapeDataString(last).Trim('/');
        return Sanitize(string.IsNullOrEmpty(name) ? uri.Host : name);
    }

    /// <summary>
    /// Returns <paramref name="fileName"/> or "name (n).ext" so the final file (and, unless
    /// <paramref name="ignoreTempFiles"/>, its temp file) does not exist yet.
    /// </summary>
    public static string MakeUnique(string directory, string fileName, bool ignoreTempFiles = false)
    {
        bool Taken(string n) => File.Exists(Path.Combine(directory, n))
                                || (!ignoreTempFiles && File.Exists(Path.Combine(directory, n + ".vdmpart")));
        if (!Taken(fileName)) return fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!Taken(candidate)) return candidate;
        }
    }

    private static readonly Dictionary<string, string> MimeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/zip"] = ".zip",
        ["application/x-zip-compressed"] = ".zip",
        ["application/x-7z-compressed"] = ".7z",
        ["application/vnd.rar"] = ".rar",
        ["application/x-rar-compressed"] = ".rar",
        ["application/gzip"] = ".gz",
        ["application/pdf"] = ".pdf",
        ["application/x-msdownload"] = ".exe",
        ["application/x-msi"] = ".msi",
        ["application/vnd.android.package-archive"] = ".apk",
        ["application/x-iso9660-image"] = ".iso",
        ["video/mp4"] = ".mp4",
        ["video/webm"] = ".webm",
        ["video/x-matroska"] = ".mkv",
        ["audio/mpeg"] = ".mp3",
        ["audio/flac"] = ".flac",
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["text/html"] = ".html",
        ["text/plain"] = ".txt",
    };

    public static string? ExtensionForMime(string? mime) =>
        mime is not null && MimeExtensions.TryGetValue(mime, out var ext) ? ext : null;
}
