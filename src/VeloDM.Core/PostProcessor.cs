using System.Diagnostics;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace VeloDM.Core;

public sealed record PostProcessResult(string? Note, bool ThreatFound);

/// <summary>What VeloDM does with a file once it has finished downloading: virus scan, then unpack.</summary>
public static class PostProcessor
{
    private static readonly string[] ArchiveExtensions = [".zip", ".rar", ".7z", ".tar", ".tgz", ".gz", ".bz2", ".xz"];
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(30);

    public static bool IsArchive(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return Array.IndexOf(ArchiveExtensions, ext) >= 0;
    }

    /// <summary>
    /// Runs the enabled steps. <paramref name="phase"/> receives a short description of the step in
    /// progress. Failures are reported in the note; they never fail the download.
    /// </summary>
    public static async Task<PostProcessResult> RunAsync(DownloadItem item, AppSettings settings, Action<string?> phase,
                                                         CancellationToken ct)
    {
        var notes = new List<string>();
        bool threat = false;
        var path = item.FullPath;
        if (!File.Exists(path) && !Directory.Exists(path)) return new PostProcessResult(null, false);

        try
        {
            if (settings.ScanAfterDownload)
            {
                phase("Scanning for viruses…");
                var scan = await ScanAsync(path, settings.VirusScannerCommand, ct).ConfigureAwait(false);
                threat = scan.ThreatFound;
                notes.Add(scan.Summary);
            }

            // An archive the scanner flagged is left packed.
            if (settings.ExtractArchives && !threat && File.Exists(path) && IsArchive(path) && !IsLaterVolume(path))
            {
                phase("Extracting…");
                try
                {
                    var folder = await Task.Run(() => Extract(path, ct), ct).ConfigureAwait(false);
                    notes.Add("Extracted to " + Path.GetFileName(folder));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    notes.Add("Could not extract: " + ex.Message);
                }
            }
        }
        finally
        {
            phase(null);
        }
        return new PostProcessResult(notes.Count > 0 ? string.Join(" · ", notes) : null, threat);
    }

    // "name.part2.rar" and later belong to a set that is unpacked from its first volume.
    private static bool IsLaterVolume(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        int i = stem.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && int.TryParse(stem[(i + 5)..], out int n) && n > 1;
    }

    /// <summary>Unpacks into a new folder beside the archive, named after it, and returns that folder.</summary>
    public static string Extract(string archivePath, CancellationToken ct = default)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(archivePath))!;
        var stem = Path.GetFileNameWithoutExtension(archivePath);
        if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        var target = Path.Combine(parent, stem);
        for (int i = 1; Directory.Exists(target) || File.Exists(target); i++)
            target = Path.Combine(parent, $"{stem} ({i})");

        Directory.CreateDirectory(target);
        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            if (archive.IsEncrypted) throw new InvalidDataException("the archive is password-protected.");
            // Entries that would land outside the target folder are refused by the library.
            var options = new ExtractionOptions { ExtractFullPath = true, Overwrite = true };
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!entry.IsDirectory) entry.WriteToDirectory(target, options);
            }
            return target;
        }
        catch
        {
            try { Directory.Delete(target, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    // ---- virus scan ------------------------------------------------------------------------

    public sealed record ScanResult(bool ThreatFound, string Summary);

    public static string? FindDefender()
    {
        // Updated Defender platforms live under ProgramData; the Program Files copy is the fallback.
        var platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows Defender", "Platform");
        try
        {
            if (Directory.Exists(platform))
            {
                var newest = Directory.GetDirectories(platform)
                    .Select(d => Path.Combine(d, "MpCmdRun.exe"))
                    .Where(File.Exists)
                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (newest is not null) return newest;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    /// <summary>
    /// Splits a scanner command line into program and arguments, putting the quoted file path
    /// where <c>{file}</c> is (or at the end when it is absent).
    /// </summary>
    public static (string Program, string Arguments) ParseScannerCommand(string command, string file)
    {
        command = command.Trim();
        string program, rest;
        if (command.StartsWith('"'))
        {
            int close = command.IndexOf('"', 1);
            if (close < 0) throw new FormatException("The virus scanner command has an unmatched quote.");
            program = command[1..close];
            rest = command[(close + 1)..].Trim();
        }
        else
        {
            int space = command.IndexOf(' ');
            program = space < 0 ? command : command[..space];
            rest = space < 0 ? "" : command[(space + 1)..].Trim();
        }
        var quoted = "\"" + file + "\"";
        rest = rest.Contains("{file}", StringComparison.OrdinalIgnoreCase)
            ? rest.Replace("\"{file}\"", quoted, StringComparison.OrdinalIgnoreCase).Replace("{file}", quoted, StringComparison.OrdinalIgnoreCase)
            : (rest + " " + quoted).Trim();
        return (program, rest);
    }

    public static async Task<ScanResult> ScanAsync(string path, string? customCommand, CancellationToken ct)
    {
        bool defender = string.IsNullOrWhiteSpace(customCommand);
        string program, arguments;
        try
        {
            if (defender)
            {
                program = FindDefender() ?? throw new FileNotFoundException("Windows Defender was not found.");
                // Report only: Defender's own real-time protection decides what to do with a threat.
                arguments = $"-Scan -ScanType 3 -File \"{path}\" -DisableRemediation";
            }
            else
            {
                (program, arguments) = ParseScannerCommand(customCommand!, path);
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(program, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.Start();
            // Drain both pipes so a chatty scanner cannot block on a full buffer.
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ScanTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                ct.ThrowIfCancellationRequested();
                return new ScanResult(false, "Virus scan timed out");
            }
            await Task.WhenAll(output, errors).ConfigureAwait(false);

            // Defender: 0 = clean, 2 = threat found. Other scanners: anything but 0 is treated as a finding.
            return process.ExitCode switch
            {
                0 => new ScanResult(false, "No threats found"),
                2 when defender => new ScanResult(true, "THREAT FOUND by Windows Defender"),
                var code when defender => new ScanResult(false, $"Virus scan failed (code {code})"),
                var code => new ScanResult(true, $"Virus scanner reported a problem (code {code})"),
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or FormatException
                                       or InvalidOperationException)
        {
            return new ScanResult(false, "Virus scan could not run: " + ex.Message);
        }
    }
}
