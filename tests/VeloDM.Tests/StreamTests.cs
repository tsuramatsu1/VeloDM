using VeloDM.Core;

namespace VeloDM.Tests;

/// <summary>
/// Streamed-video downloads, with FakeYtDlp standing in for yt-dlp: these cover how VeloDM
/// drives the program and reads its output, not the program itself.
/// </summary>
public sealed class StreamTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "VeloDM.Tests", Guid.NewGuid().ToString("N"));
    private readonly DownloadManager _manager;

    public StreamTests()
    {
        Directory.CreateDirectory(_dir);
        _manager = new DownloadManager(Path.Combine(_dir, "data"))
        {
            LocateVideoTools = () => new VideoToolPaths(Path.Combine(AppContext.BaseDirectory, "FakeYtDlp.exe"), _dir),
        };
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private DownloadItem NewItem(string url, string fileName = "clip.mp4") => new()
    {
        Kind = DownloadKind.Stream,
        Url = url,
        FileName = fileName,
        SaveDirectory = _dir,
        Category = "Video",
    };

    private static async Task WaitForAsync(DownloadItem item, Func<DownloadItem, bool> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition(item))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Status {item.Status} ({item.StatusText}), {item.Downloaded} bytes, error: {item.ErrorMessage}");
            await Task.Delay(20);
        }
    }

    private static Task WaitForEndAsync(DownloadItem item) =>
        WaitForAsync(item, i => i.Status is DownloadStatus.Completed or DownloadStatus.Error);

    private string[] ArgumentsFor(string fileName) => File.ReadAllLines(Path.Combine(_dir, fileName + ".args.txt"));

    [Fact]
    public async Task Stream_download_completes_and_reports_combined_progress()
    {
        var item = NewItem("https://video.example/watch?v=slow-1");
        long peak = 0, total = 0;
        _manager.Ticked += () =>
        {
            peak = Math.Max(peak, item.Downloaded);
            if (item.Status == DownloadStatus.Downloading) total = Math.Max(total, item.TotalSize);
        };

        _manager.Add(item, startNow: true);
        await WaitForEndAsync(item);

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        Assert.Equal("clip.mp4", item.FileName);
        Assert.Equal(3000, new FileInfo(item.FullPath).Length);
        Assert.Equal(3000, item.TotalSize);
        Assert.Equal(100, item.Progress);
        // Picture (2000 bytes) and sound (1000 bytes) are counted together against the announced size.
        Assert.Equal(3000, total);
        Assert.InRange(peak, 2001, 3000);
    }

    [Fact]
    public async Task Video_title_names_a_download_added_without_a_name()
    {
        var item = NewItem("https://video.example/watch?v=2", fileName: "");

        _manager.Add(item, startNow: true);
        await WaitForEndAsync(item);

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        Assert.Equal("Fake Title.mp4", item.FileName);
        Assert.True(File.Exists(item.FullPath));
    }

    [Fact]
    public async Task Settings_reach_the_tool_as_options()
    {
        var settings = _manager.Settings.Clone();
        settings.StreamMaxHeight = 720;
        settings.ProxyMode = ProxyMode.None;
        _manager.ApplySettings(settings);
        var item = NewItem("https://video.example/watch?v=3", "limited 100%.mp4");
        item.SpeedLimitKBps = 300;
        item.Cookies = "session=abc";
        item.Referrer = "https://video.example/";

        _manager.Add(item, startNow: true);
        await WaitForEndAsync(item);

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        Assert.Equal("limited 100%.mp4", item.FileName);
        var args = ArgumentsFor("limited 100%.mp4");
        Assert.Contains("bv*[height<=720]+ba/b[height<=720]/bv*+ba/b", args);
        Assert.Equal("307200", args[Array.IndexOf(args, "--limit-rate") + 1]);
        Assert.Equal("8", args[Array.IndexOf(args, "--concurrent-fragments") + 1]);
        Assert.Equal("", args[Array.IndexOf(args, "--proxy") + 1]);
        Assert.Contains("Cookie:session=abc", args);
        Assert.DoesNotContain("Referer:https://video.example/", args); // only sent with a bare playlist
        Assert.Equal(["--", item.Url], args[^2..]);
    }

    [Fact]
    public async Task Unrecognised_page_falls_back_to_the_playlist_seen_in_the_browser()
    {
        var item = NewItem("https://unsupported.example/page");
        item.ManifestUrl = "https://cdn.example/video/master.m3u8";
        item.Referrer = "https://unsupported.example/page";

        _manager.Add(item, startNow: true);
        await WaitForEndAsync(item);

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        var args = ArgumentsFor("clip.mp4");
        Assert.Equal(item.ManifestUrl, args[^1]);
        Assert.Contains("Referer:https://unsupported.example/page", args);
    }

    [Fact]
    public async Task Tool_error_message_is_shown_on_the_download()
    {
        var unsupported = NewItem("https://unsupported.example/page", "a.mp4");
        var broken = NewItem("https://video.example/broken", "b.mp4");

        _manager.Add(unsupported, startNow: true);
        _manager.Add(broken, startNow: true);
        await WaitForEndAsync(unsupported);
        await WaitForEndAsync(broken);

        Assert.Equal(DownloadStatus.Error, unsupported.Status);
        Assert.StartsWith("Unsupported URL", unsupported.ErrorMessage);
        Assert.Equal(DownloadStatus.Error, broken.Status);
        Assert.Equal("[generic] This video is unavailable", broken.ErrorMessage);
    }

    [Fact]
    public async Task Pausing_stops_the_tool_and_resuming_finishes_the_download()
    {
        var item = NewItem("https://video.example/watch?v=slow-4");

        _manager.Add(item, startNow: true);
        await WaitForAsync(item, i => i.Downloaded > 0);
        _manager.Pause(item);
        await WaitForAsync(item, i => i.Status == DownloadStatus.Paused && !_manager.IsRunning(i));
        Assert.False(File.Exists(item.FullPath));

        _manager.Start(item);
        await WaitForEndAsync(item);

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        Assert.True(File.Exists(item.FullPath));
    }

    [Fact]
    public async Task JavaScript_runtime_is_passed_on_when_one_is_installed()
    {
        var fake = Path.Combine(AppContext.BaseDirectory, "FakeYtDlp.exe");
        _manager.LocateVideoTools = () => new VideoToolPaths(fake, _dir, @"deno:C:\tools\deno.exe");
        var with = NewItem("https://video.example/watch?v=7", "with.mp4");
        _manager.Add(with, startNow: true);
        await WaitForEndAsync(with);

        _manager.LocateVideoTools = () => new VideoToolPaths(fake, _dir);
        var without = NewItem("https://video.example/watch?v=8", "without.mp4");
        _manager.Add(without, startNow: true);
        await WaitForEndAsync(without);

        var args = ArgumentsFor("with.mp4");
        Assert.Equal(@"deno:C:\tools\deno.exe", args[Array.IndexOf(args, "--js-runtimes") + 1]);
        Assert.DoesNotContain("--js-runtimes", ArgumentsFor("without.mp4"));
    }

    [Fact]
    public async Task Failure_with_old_tools_suggests_updating_them()
    {
        var recent = NewItem("https://video.example/broken-1", "recent.mp4");
        _manager.Add(recent, startNow: true);
        await WaitForEndAsync(recent);
        Assert.Equal("[generic] This video is unavailable", recent.ErrorMessage);

        // VeloDM's own copy of yt-dlp, downloaded a month ago.
        var tools = VideoTools.DirectoryFor(_manager.DataDirectory);
        Directory.CreateDirectory(tools);
        var ytDlp = Path.Combine(tools, "yt-dlp.exe");
        File.WriteAllText(ytDlp, "");
        File.SetLastWriteTimeUtc(ytDlp, DateTime.UtcNow.AddDays(-30.5));
        Assert.Equal(30, VideoTools.ManagedToolAge(_manager.DataDirectory)!.Value.Days);

        var old = NewItem("https://video.example/broken-2", "old.mp4");
        _manager.Add(old, startNow: true);
        await WaitForEndAsync(old);

        Assert.StartsWith("[generic] This video is unavailable", old.ErrorMessage);
        Assert.Contains("30 days old", old.ErrorMessage);
        Assert.Contains("Update video tools and retry", old.ErrorMessage);
    }

    [Fact]
    public async Task Video_title_and_length_can_be_read_before_downloading()
    {
        var tools = _manager.LocateVideoTools()!;

        var info = await VideoProbe.GetAsync(tools, _manager.Settings, "https://video.example/watch?v=9", "session=abc", CancellationToken.None);
        var live = await VideoProbe.GetAsync(tools, _manager.Settings, "https://video.example/live", null, CancellationToken.None);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VideoProbe.GetAsync(tools, _manager.Settings, "https://unsupported.example/page", null, CancellationToken.None));

        Assert.Equal(new VideoInfo("Fake: Title?", "0:19"), info);
        Assert.Null(live.Duration);
        Assert.StartsWith("Unsupported URL", failure.Message);
    }

    [Fact]
    public async Task Missing_tools_are_reported_with_where_to_get_them()
    {
        _manager.LocateVideoTools = () => null;
        var item = NewItem("https://video.example/watch?v=5");

        _manager.Add(item, startNow: true);
        await WaitForEndAsync(item);

        Assert.Equal(DownloadStatus.Error, item.Status);
        Assert.Contains("Options → Video", item.ErrorMessage);
    }

    [Fact]
    public async Task Removing_an_unfinished_video_deletes_only_its_own_partial_files()
    {
        string[] own = ["clip.f137.mp4.part", "clip.f140.m4a.ytdl", "clip.mp4.part", "clip.f137.mp4.part-Frag12.part"];
        string[] others = ["clip.other.mp4.part", "clip.final.mp4", "clip2.mp4.part", "clip.mp4"];
        foreach (var name in own.Concat(others)) File.WriteAllText(Path.Combine(_dir, name), "x");
        var item = NewItem("https://video.example/watch?v=6");
        _manager.Add(item, startNow: false);

        await _manager.RemoveAsync([item], deleteFiles: false);

        Assert.All(own, name => Assert.False(File.Exists(Path.Combine(_dir, name)), name));
        Assert.All(others, name => Assert.True(File.Exists(Path.Combine(_dir, name)), name));
    }

    [Theory]
    [InlineData("VDM|downloading|1024|4096|NA|512.5|6|C:\\v\\a.f1.mp4", "downloading", 1024, 4096, 512.5)]
    [InlineData("VDM|downloading|1024|NA|8000.0|NA|NA|a.mp4", "downloading", 1024, 8000, 0)]
    [InlineData("VDM|finished|4096|4096|NA|NA|NA|a.mp4", "finished", 4096, 4096, 0)]
    public void Progress_lines_are_parsed(string line, string status, long downloaded, long total, double speed)
    {
        Assert.True(StreamProgress.TryParse(line, out var p));
        Assert.Equal((status, downloaded, total, speed), (p.Status, p.Downloaded, p.Total, p.Speed));
    }

    [Theory]
    [InlineData("[download] 12% of 3MiB")]
    [InlineData("VDMFILE|C:\\v\\a.mp4")]
    [InlineData("")]
    public void Other_output_is_not_mistaken_for_progress(string line) =>
        Assert.False(StreamProgress.TryParse(line, out _));
}
