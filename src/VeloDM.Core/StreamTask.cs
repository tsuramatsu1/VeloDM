using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VeloDM.Core;

/// <summary>One line of yt-dlp progress, as formatted by <see cref="StreamTask.ProgressTemplate"/>.</summary>
public readonly record struct StreamProgress(string Status, long Downloaded, long Total, double Speed, string File)
{
    public static bool TryParse(string line, out StreamProgress progress)
    {
        progress = default;
        var parts = line.Split('|');
        if (parts.Length < 8 || parts[0] != StreamTask.ProgressTag) return false;
        long total = Number(parts[3]);
        if (total <= 0) total = Number(parts[4]); // an estimate, for streams made of many small pieces
        progress = new StreamProgress(parts[1], Math.Max(0, Number(parts[2])), total,
            Math.Max(0, Decimal(parts[5])), string.Join('|', parts[7..]));
        return true;
    }

    private static long Number(string text) => (long)Decimal(text);

    // Missing values are printed as "NA".
    private static double Decimal(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && !double.IsNaN(value) ? value : -1;
}

/// <summary>
/// Downloads streamed video by driving yt-dlp, which also merges separate picture and sound
/// with FFmpeg. Pausing stops the program; resuming starts it again and it carries on from
/// the parts already on disk.
/// </summary>
internal sealed partial class StreamTask : IDownloadJob
{
    // What follows the file's stem in yt-dlp's unfinished files: "clip.f137.mp4.part", "clip.mp4.ytdl",
    // "clip.f137.mp4.part-Frag12.part". Anything else that merely starts with the same name is not ours.
    [GeneratedRegex(@"^(\.f[^.]+)?\.[A-Za-z0-9]+\.(part(-Frag\d+(\.part)?)?|ytdl)$", RegexOptions.IgnoreCase)]
    private static partial Regex PartialFileSuffix();

    internal const string ProgressTag = "VDM";
    private const string SizeTag = "VDMSIZE";
    private const string TitleTag = "VDMTITLE";
    private const string FileTag = "VDMFILE";

    internal const string ProgressTemplate =
        "download:" + ProgressTag + "|%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|" +
        "%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(progress.filename)s";

    private readonly DownloadManager _manager;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _progressLock = new();

    private long _finishedFilesBytes; // bytes of the picture/sound files already complete
    private long _currentFileBytes;
    private long _currentFileTotal;
    private string? _currentFile;
    private long _announcedTotal = -1;
    private double _speed;
    private string? _finalPath;
    private string? _lastError;

    public DownloadItem Item { get; }
    public Task Completion { get; private set; } = Task.CompletedTask;
    public bool CompletionReported => false;

    public StreamTask(DownloadItem item, DownloadManager manager)
    {
        Item = item;
        _manager = manager;
    }

    public void Start() => Completion = Task.Run(() => RunAsync(_cts.Token));

    public void Cancel()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Sample(long timestamp)
    {
        Item.Speed = Item.Status == DownloadStatus.Downloading ? _speed : 0;
        long left = Item.TotalSize - Item.Downloaded;
        Item.Eta = left > 0 && _speed > 1 ? TimeSpan.FromSeconds(left / _speed) : null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            Item.ErrorMessage = null;
            Item.Phase = "Reading the video page…";
            Item.Status = DownloadStatus.Connecting;

            var tools = _manager.LocateVideoTools()
                        ?? throw new InvalidOperationException(
                            "The video tools are not installed. Open Options → Video and choose Download video tools.");
            if (string.IsNullOrWhiteSpace(Item.SaveDirectory))
                Item.SaveDirectory = Categories.FolderFor(_manager.Settings, Item.Category);
            Directory.CreateDirectory(Item.SaveDirectory);

            int exit = await RunToolAsync(tools, Item.Url, isManifest: false, ct).ConfigureAwait(false);
            // A page yt-dlp has no reader for can still be downloaded from the playlist the browser saw on it.
            if (exit != 0 && _finalPath is null && !string.IsNullOrWhiteSpace(Item.ManifestUrl)
                && (_lastError ?? "").Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase))
            {
                ResetProgress();
                exit = await RunToolAsync(tools, Item.ManifestUrl!, isManifest: true, ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();

            if (exit != 0 || _finalPath is null || !File.Exists(_finalPath))
                throw new IOException(_lastError ?? $"The video could not be downloaded (yt-dlp exit code {exit}).");

            Item.SaveDirectory = Path.GetDirectoryName(_finalPath)!;
            Item.FileName = Path.GetFileName(_finalPath);
            long size = new FileInfo(_finalPath).Length;
            Item.ReceivedBytes = size;
            Item.TotalSize = size;
            Item.InQueue = false;
            Item.Phase = null;
            Item.Status = DownloadStatus.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Item.Status = DownloadStatus.Paused;
        }
        catch (Exception ex)
        {
            Item.ErrorMessage = ex.Message + StaleToolsHint();
            Item.Status = DownloadStatus.Error;
        }
        finally
        {
            _speed = 0;
            Item.Phase = null;
            Item.Speed = 0;
            Item.Eta = null;
            Item.ActiveConnections = 0;
            Item.NotifyProgress();
            _cts.Dispose();
        }
    }

    internal static readonly TimeSpan StaleAfter = TimeSpan.FromDays(14);

    /// <summary>Video sites change often, so an old yt-dlp is the usual reason a video stops downloading.</summary>
    private string StaleToolsHint() =>
        VideoTools.ManagedToolAge(_manager.DataDirectory) is { } age && age > StaleAfter
            ? $" The video tools are {age.Days} days old; updating them often fixes this (open the download and choose Update video tools and retry)."
            : "";

    private void ResetProgress()
    {
        lock (_progressLock)
        {
            _finishedFilesBytes = _currentFileBytes = _currentFileTotal = 0;
            _currentFile = null;
            _announcedTotal = -1;
            _lastError = null;
        }
    }

    private async Task<int> RunToolAsync(VideoToolPaths tools, string address, bool isManifest, CancellationToken ct)
    {
        var start = new ProcessStartInfo(tools.YtDlp)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Item.SaveDirectory,
        };
        foreach (var argument in BuildArguments(Item, _manager.Settings, tools, address, isManifest))
            start.ArgumentList.Add(argument);
        ApplyToolEnvironment(start, _manager.Settings);

        Log.Info($"Video: yt-dlp started for {Log.Address(address)}{(isManifest ? " (playlist fallback)" : "")}, " +
                 $"JavaScript runtime: {tools.JsRuntime?.Split(':')[0] ?? "none"}");
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is { } line) OnOutput(line); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) OnError(line); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Item.ActiveConnections = 1;

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        process.WaitForExit(); // lets the last output lines be delivered
        Log.Info($"Video: yt-dlp finished with exit code {process.ExitCode} for {Log.Address(address)}");
        return process.ExitCode;
    }

    internal static List<string> BuildArguments(DownloadItem item, AppSettings settings, VideoToolPaths tools, string address,
                                                bool isManifest)
    {
        int height = settings.StreamMaxHeight;
        string format = height > 0
            ? $"bv*[height<={height}]+ba/b[height<={height}]/bv*+ba/b"
            : "bv*+ba/b";
        // An item added without a name takes the video's own title.
        string stem = string.IsNullOrWhiteSpace(item.FileName)
            ? "%(title).150B"
            : Path.GetFileNameWithoutExtension(item.FileName).Replace("%", "%%");

        var args = new List<string>
        {
            "--no-playlist", "--no-mtime", "--windows-filenames", "--no-simulate", "--progress", "--newline",
            "--color", "no_color", "--encoding", "utf-8",
            "--ffmpeg-location", tools.FfmpegDirectory,
            // HLS and DASH streams are many small pieces; fetched one at a time they are far slower than the line allows.
            "--concurrent-fragments", Math.Clamp(settings.DefaultConnections, 1, 16).ToString(CultureInfo.InvariantCulture),
            "-f", format,
            "--merge-output-format", "mp4",
            // "%" starts a field in an output template, so literal ones in the folder are doubled.
            "-o", Path.Combine(item.SaveDirectory.Replace("%", "%%"), stem + ".%(ext)s"),
            "--progress-template", ProgressTemplate,
            "--print", $"before_dl:{TitleTag}|%(title)s",
            "--print", $"before_dl:{SizeTag}|%(filesize,filesize_approx)s",
            "--print", $"after_move:{FileTag}|%(filepath)s",
        };

        AddConnectionArguments(args, tools, settings, item.Cookies);

        long limit = settings.SpeedLimitBytes;
        long own = item.SpeedLimitKBps * 1024L;
        if (own > 0 && (limit <= 0 || own < limit)) limit = own;
        if (limit > 0) args.AddRange(["--limit-rate", limit.ToString(CultureInfo.InvariantCulture)]);

        if (isManifest)
        {
            // A bare playlist is fetched like the page's player would fetch it.
            if (!string.IsNullOrWhiteSpace(item.Referrer)) args.AddRange(["--add-headers", "Referer:" + item.Referrer]);
            if (!string.IsNullOrWhiteSpace(item.UserAgent)) args.AddRange(["--add-headers", "User-Agent:" + item.UserAgent]);
        }

        args.Add("--");
        args.Add(address);
        return args;
    }

    /// <summary>What every yt-dlp run needs to reach the site the way the browser does.</summary>
    internal static void AddConnectionArguments(List<string> args, VideoToolPaths tools, AppSettings settings, string? cookies)
    {
        if (tools.JsRuntime is not null) args.AddRange(["--js-runtimes", tools.JsRuntime]);
        if (settings.ProxyMode == ProxyMode.None) args.AddRange(["--proxy", ""]);
        if (!string.IsNullOrWhiteSpace(cookies)) args.AddRange(["--add-headers", "Cookie:" + cookies]);
    }

    internal static void ApplyToolEnvironment(ProcessStartInfo start, AppSettings settings)
    {
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUTF8"] = "1";
        // The proxy goes through the environment so its password never appears on a command line.
        if (HttpEngine.ProxyUrlForTools(settings) is { Length: > 0 } proxy)
            start.Environment["HTTP_PROXY"] = start.Environment["HTTPS_PROXY"] = start.Environment["ALL_PROXY"] = proxy;
    }

    private void OnOutput(string line)
    {
        if (StreamProgress.TryParse(line, out var p))
        {
            lock (_progressLock)
            {
                if (_currentFile is not null && p.File != _currentFile)
                {
                    // Picture and sound arrive as separate files, one after the other.
                    _finishedFilesBytes += Math.Max(_currentFileBytes, _currentFileTotal);
                    _currentFileBytes = _currentFileTotal = 0;
                }
                _currentFile = p.File;
                _currentFileBytes = p.Downloaded;
                if (p.Total > 0) _currentFileTotal = p.Total;
                _speed = p.Status == "downloading" ? p.Speed : 0;

                long received = _finishedFilesBytes + _currentFileBytes;
                long total = Math.Max(_announcedTotal, _finishedFilesBytes + Math.Max(_currentFileTotal, _currentFileBytes));
                Item.ReceivedBytes = received;
                if (total > 0 && Item.TotalSize != total) Item.TotalSize = total;
            }
            if (Item.Status == DownloadStatus.Connecting) Item.Status = DownloadStatus.Downloading;
            Item.Phase = p.Status == "finished" ? "Finishing (joining picture and sound)…" : null;
            return;
        }

        if (TagValue(line, SizeTag) is { } size)
        {
            if (long.TryParse(size, NumberStyles.Float, CultureInfo.InvariantCulture, out long bytes) && bytes > 0)
                lock (_progressLock) _announcedTotal = bytes;
        }
        else if (TagValue(line, TitleTag) is { } title)
        {
            if (string.IsNullOrWhiteSpace(Item.FileName) && title.Length > 0 && title != "NA")
                Item.FileName = FileNameHelper.Sanitize(title) + ".mp4";
            Item.Phase = "Starting the video download…";
        }
        else if (TagValue(line, FileTag) is { } path && path.Length > 0)
        {
            _finalPath = path;
        }
    }

    private void OnError(string line)
    {
        if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            _lastError = line[6..].Trim();
        // yt-dlp's own warnings and errors are the best clue when a site stops working.
        if (line.Length > 0) Log.Warn("Video: yt-dlp: " + line);
    }

    private static string? TagValue(string line, string tag) =>
        line.StartsWith(tag + "|", StringComparison.Ordinal) ? line[(tag.Length + 1)..].Trim() : null;

    /// <summary>Deletes the partial files yt-dlp leaves for an unfinished download.</summary>
    internal static void DeletePartialFiles(DownloadItem item)
    {
        var stem = Path.GetFileNameWithoutExtension(item.FileName);
        if (string.IsNullOrWhiteSpace(stem) || !Directory.Exists(item.SaveDirectory)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(item.SaveDirectory))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) && PartialFileSuffix().IsMatch(name[stem.Length..]))
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
