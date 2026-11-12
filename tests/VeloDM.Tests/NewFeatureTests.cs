using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using VeloDM.Core;

namespace VeloDM.Tests;

/// <summary>Per-download speed limit, expired-link refresh, after-download steps, proxy, checksums and the update check.</summary>
public sealed class NewFeatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "VeloDM.Tests", Guid.NewGuid().ToString("N"));
    private readonly DownloadManager _manager;

    public NewFeatureTests()
    {
        Directory.CreateDirectory(_dir);
        _manager = new DownloadManager(Path.Combine(_dir, "data"));
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private DownloadItem NewItem(string url, string name = "payload.bin") => new()
    {
        Url = url,
        FileName = name,
        SaveDirectory = _dir,
        MaxConnections = 4,
    };

    private static async Task WaitForAsync(DownloadItem item, Func<DownloadItem, bool> condition, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition(item))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Status {item.Status}, {item.Downloaded} bytes, error: {item.ErrorMessage}");
            await Task.Delay(25);
        }
    }

    private static Task WaitForEndAsync(DownloadItem item) =>
        WaitForAsync(item, i => i.Status is DownloadStatus.Completed or DownloadStatus.Error);

    private void Configure(Action<AppSettings> change)
    {
        var settings = _manager.Settings.Clone();
        change(settings);
        _manager.ApplySettings(settings);
    }

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        return memory.ToArray();
    }

    // ---- per-download speed limit -------------------------------------------------------

    [Fact]
    public async Task Per_download_speed_limit_slows_only_that_download()
    {
        using var server = new TestServer(1024 * 1024) { ChunkDelayMs = 0 };
        var limited = NewItem(server.BaseUrl + "file", "limited.bin");
        limited.SpeedLimitKBps = 256;
        var free = NewItem(server.BaseUrl + "file", "free.bin");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        _manager.Add(limited, startNow: true);
        _manager.Add(free, startNow: true);
        await WaitForEndAsync(free);
        double freeSeconds = clock.Elapsed.TotalSeconds;
        await WaitForEndAsync(limited);
        double limitedSeconds = clock.Elapsed.TotalSeconds;

        Assert.True(limited.Status == DownloadStatus.Completed, limited.ErrorMessage);
        // 1 MB at 256 KB/s with one second of burst allowance: about three seconds.
        Assert.True(limitedSeconds > 2.0, $"limited download took {limitedSeconds:0.00}s");
        Assert.True(freeSeconds < limitedSeconds, $"free {freeSeconds:0.00}s, limited {limitedSeconds:0.00}s");
        Assert.Equal(server.Data, File.ReadAllBytes(limited.FullPath));
    }

    // ---- expired links --------------------------------------------------------------------

    [Fact]
    public async Task Expired_link_can_be_refreshed_and_resumes_from_partial_data()
    {
        using var server = new TestServer(6 * 1024 * 1024) { ChunkDelayMs = 5 };
        var item = NewItem(server.BaseUrl + "file?token=old");

        _manager.Add(item, startNow: true);
        await WaitForAsync(item, i => i.Downloaded > 1024 * 1024);
        _manager.Pause(item);
        await WaitForAsync(item, i => i.Status == DownloadStatus.Paused && !_manager.IsRunning(i));
        long partial = item.Downloaded;

        server.ExpiredToken = "token=old";
        _manager.Start(item);
        await WaitForEndAsync(item);
        Assert.Equal(DownloadStatus.Error, item.Status);
        Assert.Contains("Refresh download address", item.ErrorMessage);
        Assert.Equal(partial, item.Downloaded);

        server.ChunkDelayMs = 0;
        await _manager.UpdateAddressAsync(item, server.BaseUrl + "file?token=new", null, null, null);
        Assert.Equal(DownloadStatus.Paused, item.Status);
        _manager.Start(item);
        await WaitForAsync(item, i => i.Status != DownloadStatus.Paused);
        Assert.True(item.Downloaded >= partial, "the refreshed download started over");
        await WaitForEndAsync(item);

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        Assert.Equal(server.Data, File.ReadAllBytes(item.FullPath));
    }

    // ---- after-download steps -----------------------------------------------------------

    [Fact]
    public async Task Finished_archive_is_extracted_into_its_own_folder()
    {
        using var server = new TestServer(Zip(("readme.txt", "hello"), ("docs/guide.txt", "nested")));
        Configure(s => s.ExtractArchives = true);
        var processed = new TaskCompletionSource<PostProcessResult>();
        _manager.PostProcessed += (_, result) => processed.TrySetResult(result);

        var item = NewItem(server.BaseUrl + "file", "bundle.zip");
        _manager.Add(item, startNow: true);
        await WaitForEndAsync(item);
        var result = await processed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.ThreatFound);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_dir, "bundle", "readme.txt")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(_dir, "bundle", "docs", "guide.txt")));
        Assert.Contains("Extracted to bundle", item.Note);
        Assert.Contains("Extracted to bundle", item.StatusText);
        Assert.True(File.Exists(item.FullPath), "the archive itself is kept");
    }

    [Fact]
    public void Archive_entry_cannot_escape_the_extraction_folder()
    {
        var archive = Path.Combine(_dir, "trap.zip");
        File.WriteAllBytes(archive, Zip(("../escaped.txt", "outside"), ("inside.txt", "ok")));

        Assert.ThrowsAny<Exception>(() => PostProcessor.Extract(archive));

        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "escaped.txt")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "trap")), "a failed extraction leaves no folder behind");
    }

    [Fact]
    public async Task Custom_virus_scanner_result_is_recorded()
    {
        var file = Path.Combine(_dir, "sample file.bin");
        File.WriteAllText(file, "data");
        // A stand-in for a scanner's verdict: exit code 0 when the file exists, 1 when it does not.
        var scanner = $"\"{Environment.GetEnvironmentVariable("ComSpec")}\" /c if exist {{file}} (exit 0) else (exit 1)";

        var clean = await PostProcessor.ScanAsync(file, scanner, CancellationToken.None);
        var flagged = await PostProcessor.ScanAsync(file + ".missing", scanner, CancellationToken.None);
        var missing = await PostProcessor.ScanAsync(file, "\"C:\\no such folder\\scanner.exe\" {file}", CancellationToken.None);

        Assert.Equal(new PostProcessor.ScanResult(false, "No threats found"), clean);
        Assert.True(flagged.ThreatFound);
        Assert.False(missing.ThreatFound);
        Assert.StartsWith("Virus scan could not run", missing.Summary);
    }

    [Theory]
    [InlineData("scan.exe", "scan.exe", "\"C:\\a b\\f.zip\"")]
    [InlineData("scan.exe --quiet {file} --log", "scan.exe", "--quiet \"C:\\a b\\f.zip\" --log")]
    [InlineData("\"C:\\Program Files\\AV\\scan.exe\" /f \"{file}\"", "C:\\Program Files\\AV\\scan.exe", "/f \"C:\\a b\\f.zip\"")]
    public void Scanner_command_line_is_split_and_the_file_is_quoted(string command, string program, string arguments) =>
        Assert.Equal((program, arguments), PostProcessor.ParseScannerCommand(command, "C:\\a b\\f.zip"));

    // ---- proxy ---------------------------------------------------------------------------

    [Fact]
    public void Proxy_setting_decides_how_requests_are_routed()
    {
        var target = new Uri("https://example.com/file.zip");
        var settings = new AppSettings { ProxyMode = ProxyMode.None };
        Assert.Null(HttpEngine.BuildProxy(settings));
        Assert.Equal("", HttpEngine.ProxyUrlForTools(settings));

        settings.ProxyMode = ProxyMode.Custom;
        settings.ProxyAddress = "socks5://10.0.0.5:1080";
        settings.ProxyUser = "me";
        settings.ProxyPassword = "p@ss word";
        var proxy = HttpEngine.BuildProxy(settings)!;
        Assert.Equal(new Uri("socks5://10.0.0.5:1080"), proxy.GetProxy(target));
        Assert.False(proxy.IsBypassed(target));
        Assert.True(proxy.IsBypassed(new Uri("http://localhost:9615/ping")));
        Assert.Equal("p@ss word", ((NetworkCredential)proxy.Credentials!).Password);
        Assert.Equal("socks5://me:p%40ss%20word@10.0.0.5:1080", HttpEngine.ProxyUrlForTools(settings));

        // The password is stored encrypted and survives a save/load round trip.
        Assert.DoesNotContain("p@ss", settings.ProxyPasswordProtected);
        Assert.Equal("p@ss word", settings.Clone().ProxyPassword);

        settings.ProxyMode = ProxyMode.System;
        Assert.Null(HttpEngine.ProxyUrlForTools(settings));
        Assert.True(HttpEngine.IsValidProxyAddress("http://proxy.local:8080"));
        Assert.False(HttpEngine.IsValidProxyAddress("proxy.local:8080"));
        Assert.False(HttpEngine.IsValidProxyAddress("ftp://proxy.local"));
    }

    // ---- checksums -----------------------------------------------------------------------

    [Fact]
    public async Task Checksum_is_computed_and_compared_loosely()
    {
        var file = Path.Combine(_dir, "sum.bin");
        var data = new byte[3 * 1024 * 1024 + 17];
        new Random(7).NextBytes(data);
        File.WriteAllBytes(file, data);
        var expected = Convert.ToHexString(SHA256.HashData(data));
        double lastProgress = 0;

        var actual = await Checksums.ComputeAsync(file, "SHA-256", new Progress<double>(p => lastProgress = p), CancellationToken.None);

        Assert.Equal(expected.ToLowerInvariant(), actual);
        Assert.True(Checksums.Matches(actual, "  SHA256:" + expected + " "));
        Assert.False(Checksums.Matches(actual, expected[..^1] + (expected[^1] == '0' ? "1" : "0")));
        Assert.Equal("SHA-256", Checksums.AlgorithmFor(expected));
        Assert.Equal("MD5", Checksums.AlgorithmFor("d41d8cd98f00b204e9800998ecf8427e"));
        Assert.Null(Checksums.AlgorithmFor("not a checksum"));
    }

    // ---- update check --------------------------------------------------------------------

    [Fact]
    public void GitHub_release_is_read_as_an_update()
    {
        const string json = """
            {
              "tag_name": "v1.4.0",
              "html_url": "https://github.com/someone/VeloDM/releases/tag/v1.4.0",
              "body": "Fixes",
              "assets": [
                { "browser_download_url": "https://github.com/someone/VeloDM/releases/download/v1.4.0/notes.txt" },
                { "browser_download_url": "https://github.com/someone/VeloDM/releases/download/v1.4.0/VeloDM-1.4.0-x64.msi" }
              ]
            }
            """;

        var update = UpdateChecker.ParseGitHubRelease(json)!;

        Assert.Equal(new Version(1, 4, 0, 0), update.Version);
        Assert.EndsWith("VeloDM-1.4.0-x64.msi", update.InstallerUrl);
        Assert.Equal("Fixes", update.Notes);
        Assert.True(UpdateChecker.TryGetGitHubRepository(new Uri("https://github.com/someone/VeloDM.git"), out var owner, out var repo));
        Assert.Equal(("someone", "VeloDM"), (owner, repo));
    }

    [Fact]
    public void Update_feed_file_is_read_and_unsafe_links_are_dropped()
    {
        var feed = new Uri("https://example.com/velodm/update.json");

        var update = UpdateChecker.ParseFeed("""{ "version": "2.0", "url": "http://example.com/VeloDM.msi", "notes": "n" }""", feed)!;

        Assert.Equal(new Version(2, 0, 0, 0), update.Version);
        Assert.Null(update.InstallerUrl); // not https
        Assert.Equal(feed.AbsoluteUri, update.PageUrl);
        Assert.Null(UpdateChecker.ParseFeed("""{ "version": "soon" }""", feed));
    }

    [Theory]
    [InlineData("v1.3.0", "1.3.0.0")]
    [InlineData("1.3", "1.3.0.0")]
    [InlineData("2", "2.0.0.0")]
    [InlineData("1.4.0-beta.2", "1.4.0.0")]
    public void Version_tags_are_parsed(string tag, string expected)
    {
        Assert.True(UpdateChecker.TryParseVersion(tag, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Fact]
    public void Checksum_list_entry_is_found_by_file_name()
    {
        const string sums = "aaaa  yt-dlp\nBBBB  yt-dlp.exe\ncccc *yt-dlp_x86.exe\n";
        Assert.Equal("bbbb", VideoTools.FindChecksum(sums, "yt-dlp.exe"));
        Assert.Equal("cccc", VideoTools.FindChecksum(sums, "yt-dlp_x86.exe"));
        Assert.Null(VideoTools.FindChecksum(sums, "ffmpeg.exe"));
    }
}
