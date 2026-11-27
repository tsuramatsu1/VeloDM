namespace VeloDM.Core;

public sealed record Category(string Name, string Glyph, string[] Extensions);

public static class Categories
{
    public const string General = "General";
    public const string Torrents = "Torrents";
    private const string CustomGlyph = "";

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

    public static IReadOnlyList<Category> ForSettings(AppSettings settings)
    {
        var categories = All.ToList();
        foreach (var name in settings.CustomCategories ?? [])
        {
            if (IsValidName(name) && !categories.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                categories.Add(new Category(name, CustomGlyph, []));
        }
        return categories;
    }

    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.Length > 64
            || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/') || name.Contains('\\')
            || name.EndsWith('.') || name.EndsWith(' '))
            return false;

        if (new[] { "All Downloads", "Unfinished", "Finished", "Queue" }
            .Contains(name, StringComparer.OrdinalIgnoreCase))
            return false;

        var deviceName = name.Split('.')[0];
        return !new[] { "CON", "PRN", "AUX", "NUL" }
                .Contains(deviceName, StringComparer.OrdinalIgnoreCase)
            && !(deviceName.Length == 4
                 && (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                     || deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                 && deviceName[3] is >= '1' and <= '9');
    }

    public static string ResolveName(AppSettings settings, string name) =>
        ForSettings(settings).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Name
        ?? throw new ArgumentException("Choose a known download type.", nameof(name));

    public static string FolderFor(AppSettings settings, string category)
    {
        foreach (var folder in settings.CategoryFolders ?? [])
            if (folder.Key.Equals(category, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(folder.Value))
                return folder.Value;

        return settings.UseCategoryFolders && !category.Equals(General, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(settings.DownloadFolder, category)
            : settings.DownloadFolder;
    }
}
