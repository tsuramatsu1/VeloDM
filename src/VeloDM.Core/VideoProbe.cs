using System.Diagnostics;
using System.Text;

namespace VeloDM.Core;

public sealed record VideoInfo(string Title, string? Duration);

/// <summary>
/// Asks yt-dlp what video a page holds, without downloading it, so the Add dialog can show the
/// title before the download starts.
/// </summary>
public static class VideoProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <exception cref="InvalidOperationException">yt-dlp could not read the page; the message is its own error.</exception>
    public static async Task<VideoInfo> GetAsync(VideoToolPaths tools, AppSettings settings, string address, string? cookies,
                                                 CancellationToken ct)
    {
        var start = new ProcessStartInfo(tools.YtDlp)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var args = new List<string>
        {
            "--no-playlist", "--skip-download", "--no-warnings", "--color", "no_color", "--encoding", "utf-8",
            "--print", "%(title)s", "--print", "%(duration_string)s",
        };
        StreamTask.AddConnectionArguments(args, tools, settings, cookies);
        args.Add("--");
        args.Add(address);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        StreamTask.ApplyToolEnvironment(start, settings);

        using var process = new Process { StartInfo = start };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The video page took too long to answer.");
        }

        var lines = (await output.ConfigureAwait(false)).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (process.ExitCode != 0 || lines.Length == 0 || lines[0] == "NA")
        {
            var error = (await errors.ConfigureAwait(false)).Split('\n', StringSplitOptions.TrimEntries)
                .LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase));
            throw new InvalidOperationException(error is null ? "The page did not name a video." : error[6..].Trim());
        }
        return new VideoInfo(lines[0], lines.Length > 1 && lines[1] != "NA" ? lines[1] : null);
    }
}
