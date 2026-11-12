namespace VeloDM.Core;

public sealed record Category(string Name, string Glyph, string[] Extensions);

public static class Categories
{
    public const string General = "General";
    public const string Torrents = "Torrents";

    public static readonly IReadOnlyList<Category> All =
    [
        new("Compressed", "", ["zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "cab", "lz", "arj"]),
        new("Documents", "", ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "odt", "ods", "epub", "csv", "md"]),
        new("Music", "", ["mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus", "aiff"]),
        new("Programs", "", ["exe", "msi", "msix", "appx", "apk", "dmg", "deb", "rpm", "iso", "img", "bin", "pkg", "jar"]),
        new("Video", "", ["mp4", "mkv", "avi", "mov", "wmv", "webm", "flv", "m4v", "mpg", "mpeg", "ts", "3gp"]),
        new(Torrents, "", []),
        new(General, "", []),
    ];

    public static string ForFileName(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0) return General;
        foreach (var c in All)
            if (Array.IndexOf(c.Extensions, ext) >= 0) return c.Name;
        return General;
    }

    public static string FolderFor(AppSettings settings, string category) =>
        settings.UseCategoryFolders && category != General
            ? Path.Combine(settings.DownloadFolder, category)
            : settings.DownloadFolder;
}
