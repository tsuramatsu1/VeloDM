using System.IO.Compression;
using System.Security.Cryptography;

namespace VeloDM.Core;

public sealed record VideoToolPaths(string YtDlp, string FfmpegDirectory);

/// <summary>
/// Streamed video (HLS, DASH, YouTube and similar) is downloaded by two free helper programs,
/// yt-dlp and FFmpeg. They are not part of VeloDM: the user downloads them once from Options,
/// into the data folder, from the projects' official release pages.
/// </summary>
public static class VideoTools
{
    public const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public const string YtDlpChecksumsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    public const string FfmpegUrl =
        "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    private const string YtDlpExe = "yt-dlp.exe";
    private const string FfmpegExe = "ffmpeg.exe";
    private const string FfprobeExe = "ffprobe.exe";

    public static string DirectoryFor(string dataDirectory) => Path.Combine(dataDirectory, "tools");

    /// <summary>Finds both tools: in VeloDM's tools folder, beside the program, or on the PATH.</summary>
    public static VideoToolPaths? Locate(string dataDirectory)
    {
        var ytDlp = Find(YtDlpExe, dataDirectory);
        var ffmpeg = Find(FfmpegExe, dataDirectory);
        return ytDlp is not null && ffmpeg is not null
            ? new VideoToolPaths(ytDlp, Path.GetDirectoryName(ffmpeg)!)
            : null;
    }

    private static string? Find(string fileName, string dataDirectory)
    {
        var candidates = new List<string>
        {
            Path.Combine(DirectoryFor(dataDirectory), fileName),
            Path.Combine(AppContext.BaseDirectory, "tools", fileName),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim().Trim('"'), fileName)); }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Downloads (or updates) both tools into the tools folder. Reports a step name and its percentage.</summary>
    public static async Task InstallAsync(string dataDirectory, IProgress<(string Step, double Percent)>? progress, CancellationToken ct)
    {
        var dir = DirectoryFor(dataDirectory);
        Directory.CreateDirectory(dir);

        var ytDlpTemp = Path.Combine(dir, YtDlpExe + ".download");
        var zipTemp = Path.Combine(dir, "ffmpeg.zip.download");
        try
        {
            await DownloadAsync(YtDlpUrl, ytDlpTemp, p => progress?.Report(("Downloading yt-dlp", p)), ct).ConfigureAwait(false);
            await VerifyYtDlpAsync(ytDlpTemp, ct).ConfigureAwait(false);
            File.Move(ytDlpTemp, Path.Combine(dir, YtDlpExe), overwrite: true);

            await DownloadAsync(FfmpegUrl, zipTemp, p => progress?.Report(("Downloading FFmpeg", p)), ct).ConfigureAwait(false);
            progress?.Report(("Unpacking FFmpeg", 100));
            ExtractFfmpeg(zipTemp, dir);
        }
        finally
        {
            TryDelete(ytDlpTemp);
            TryDelete(zipTemp);
        }
    }

    private static async Task DownloadAsync(string url, string target, Action<double> percent, CancellationToken ct)
    {
        using var request = HttpEngine.CreateRequest(url, null, null, null, null, null);
        using var response = await HttpEngine.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        HttpEngine.EnsureSuccess(response);
        long total = response.Content.Headers.ContentLength ?? -1;

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 16];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total > 0) percent(done * 100.0 / total);
        }
    }

    /// <summary>Checks the download against the checksum list published with the same release.</summary>
    private static async Task VerifyYtDlpAsync(string file, CancellationToken ct)
    {
        using var request = HttpEngine.CreateRequest(YtDlpChecksumsUrl, null, null, null, null, null);
        using var response = await HttpEngine.Client.SendAsync(request, ct).ConfigureAwait(false);
        HttpEngine.EnsureSuccess(response);
        var sums = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var expected = FindChecksum(sums, YtDlpExe)
                       ?? throw new InvalidDataException("The yt-dlp release does not list a checksum for yt-dlp.exe.");
        await using var stream = File.OpenRead(file);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded yt-dlp.exe does not match its published checksum, so it was discarded.");
    }

    /// <summary>Reads one entry from a GNU-style checksum list ("&lt;hex&gt;  &lt;file name&gt;" per line).</summary>
    public static string? FindChecksum(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    private static void ExtractFfmpeg(string zipPath, string directory)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        bool found = false;
        foreach (var entry in zip.Entries)
        {
            // Only the two programs are wanted, under their own names: nothing else in the archive is written.
            string? name = entry.Name.ToLowerInvariant() switch { FfmpegExe => FfmpegExe, FfprobeExe => FfprobeExe, _ => null };
            if (name is null) continue;
            entry.ExtractToFile(Path.Combine(directory, name), overwrite: true);
            found |= name == FfmpegExe;
        }
        if (!found) throw new InvalidDataException("The FFmpeg download did not contain ffmpeg.exe.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
