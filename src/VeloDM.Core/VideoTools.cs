using System.IO.Compression;
using System.Security.Cryptography;

namespace VeloDM.Core;

/// <param name="JsRuntime">
/// A JavaScript runtime for yt-dlp in its own notation ("deno:C:\…\deno.exe"), or null when none
/// was found. YouTube needs one; most other sites do not.
/// </param>
public sealed record VideoToolPaths(string YtDlp, string FfmpegDirectory, string? JsRuntime = null);

/// <summary>
/// Streamed video (HLS, DASH, YouTube and similar) is downloaded by free helper programs:
/// yt-dlp, FFmpeg, and Deno (a JavaScript runtime yt-dlp needs for YouTube). They are not part
/// of VeloDM: the user downloads them once from Options, into the data folder, from the
/// projects' official release pages.
/// </summary>
public static class VideoTools
{
    public const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public const string YtDlpChecksumsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    public const string FfmpegUrl =
        "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    public const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

    private const string DenoExe = "deno.exe";
    private const string NodeExe = "node.exe";
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
            ? new VideoToolPaths(ytDlp, Path.GetDirectoryName(ffmpeg)!, FindJsRuntime(dataDirectory))
            : null;
    }

    // Deno is what yt-dlp prefers; a Node.js that is already installed also works.
    private static string? FindJsRuntime(string dataDirectory) =>
        Find(DenoExe, dataDirectory) is { } deno ? "deno:" + deno
        : Find(NodeExe, dataDirectory) is { } node ? "node:" + node
        : null;

    /// <summary>How long ago VeloDM's own copy of yt-dlp was downloaded; null when it uses a copy from elsewhere.</summary>
    public static TimeSpan? ManagedToolAge(string dataDirectory)
    {
        var path = Path.Combine(DirectoryFor(dataDirectory), YtDlpExe);
        try
        {
            return File.Exists(path) ? DateTime.UtcNow - File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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

    /// <summary>Downloads (or updates) the tools into the tools folder. Reports a step name and its percentage.</summary>
    public static async Task InstallAsync(string dataDirectory, IProgress<(string Step, double Percent)>? progress, CancellationToken ct)
    {
        var dir = DirectoryFor(dataDirectory);
        Directory.CreateDirectory(dir);

        var ytDlpTemp = Path.Combine(dir, YtDlpExe + ".download");
        var zipTemp = Path.Combine(dir, "ffmpeg.zip.download");
        var denoTemp = Path.Combine(dir, "deno.zip.download");
        Log.Info("Video tools: download started");
        try
        {
            await DownloadAsync(YtDlpUrl, ytDlpTemp, p => progress?.Report(("Downloading yt-dlp", p)), ct).ConfigureAwait(false);
            await VerifyYtDlpAsync(ytDlpTemp, ct).ConfigureAwait(false);
            File.Move(ytDlpTemp, Path.Combine(dir, YtDlpExe), overwrite: true);

            await DownloadAsync(FfmpegUrl, zipTemp, p => progress?.Report(("Downloading FFmpeg", p)), ct).ConfigureAwait(false);
            progress?.Report(("Unpacking FFmpeg", 100));
            ExtractPrograms(zipTemp, dir, required: FfmpegExe, FfmpegExe, FfprobeExe);

            try
            {
                await DownloadAsync(DenoUrl, denoTemp, p => progress?.Report(("Downloading Deno", p)), ct).ConfigureAwait(false);
                ExtractPrograms(denoTemp, dir, required: DenoExe, DenoExe);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                // Everything but YouTube works without it, and YouTube partly: not worth failing the install over.
                Log.Warn("Video tools: Deno could not be downloaded; YouTube may offer fewer formats", ex);
            }
            // The download date is what "how old are the tools" is measured from.
            File.SetLastWriteTimeUtc(Path.Combine(dir, YtDlpExe), DateTime.UtcNow);
            Log.Info("Video tools: installed in " + dir);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("Video tools: download failed", ex);
            throw;
        }
        finally
        {
            TryDelete(ytDlpTemp);
            TryDelete(zipTemp);
            TryDelete(denoTemp);
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

    /// <summary>
    /// Takes only the named programs out of a downloaded archive, each under its own name, so
    /// nothing else in the archive (and no path it suggests) is written.
    /// </summary>
    private static void ExtractPrograms(string zipPath, string directory, string required, params string[] wanted)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        bool found = false;
        foreach (var entry in zip.Entries)
        {
            var name = wanted.FirstOrDefault(w => w.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
            if (name is null) continue;
            entry.ExtractToFile(Path.Combine(directory, name), overwrite: true);
            found |= name == required;
        }
        if (!found) throw new InvalidDataException($"The download did not contain {required}.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
