using VeloDM.Core;

namespace VeloDM.Tests;

public sealed class DownloadEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "VeloDM.Tests", Guid.NewGuid().ToString("N"));
    private readonly DownloadManager _manager;

    public DownloadEngineTests()
    {
        Directory.CreateDirectory(_dir);
        _manager = new DownloadManager(_dir);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private DownloadItem NewItem(string url, int connections = 8) => new()
    {
        Url = url,
        FileName = "payload.bin",
        SaveDirectory = _dir,
        MaxConnections = connections,
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

    [Fact]
    public async Task MultiConnection_download_produces_identical_file()
    {
        using var server = new TestServer(4 * 1024 * 1024);
        var item = NewItem(server.BaseUrl + "file");

        _manager.Add(item, startNow: true);
        await WaitForEndAsync(item);

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        Assert.Equal(server.Data, File.ReadAllBytes(Path.Combine(_dir, "payload.bin")));
        Assert.False(File.Exists(item.TempPath));
        Assert.True(item.ResumeSupported);
        Assert.True(server.PeakConnections > 2, $"peak connections was {server.PeakConnections}");
        Assert.True(item.Segments.Count > 2, $"segments: {item.Segments.Count}");
    }

    [Fact]
    public async Task Pause_and_resume_continues_from_partial_data()
    {
        using var server = new TestServer(6 * 1024 * 1024) { ChunkDelayMs = 5 };
        var item = NewItem(server.BaseUrl + "file", connections: 4);

        _manager.Add(item, startNow: true);
        await WaitForAsync(item, i => i.Downloaded > 1024 * 1024);
        _manager.Pause(item);
        await WaitForAsync(item, i => i.Status == DownloadStatus.Paused && !_manager.IsRunning(i));

        long partial = item.Downloaded;
        Assert.True(partial > 0 && partial < server.Data.Length);
        Assert.True(File.Exists(item.TempPath));

        // Persist and reload through JSON, as an app restart would.
        _manager.Save();
        using var reloaded = new DownloadManager(_dir);
        reloaded.LoadDownloads();
        var restored = Assert.Single(reloaded.Items);
        Assert.Equal(partial, restored.Downloaded);

        server.ChunkDelayMs = 0;
        reloaded.Start(restored);
        await WaitForEndAsync(restored);

        Assert.True(restored.Status == DownloadStatus.Completed, restored.ErrorMessage);
        Assert.Equal(server.Data, File.ReadAllBytes(restored.FullPath));
    }

    [Fact]
    public async Task Server_without_range_support_uses_single_connection()
    {
        using var server = new TestServer(2 * 1024 * 1024);
        var item = NewItem(server.BaseUrl + "norange");

        _manager.Add(item, startNow: true);
        await WaitForEndAsync(item);

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        Assert.False(item.ResumeSupported);
        Assert.Single(item.Segments);
        Assert.Equal(server.Data, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Unknown_size_download_reads_to_end_of_stream()
    {
        using var server = new TestServer(1024 * 1024 + 123);
        var item = NewItem(server.BaseUrl + "chunked");

        _manager.Add(item, startNow: true);
        await WaitForEndAsync(item);

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        Assert.Equal(server.Data.Length, item.TotalSize);
        Assert.Equal(server.Data, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Existing_file_is_not_overwritten()
    {
        using var server = new TestServer(256 * 1024);
        File.WriteAllText(Path.Combine(_dir, "payload.bin"), "keep me");
        var item = NewItem(server.BaseUrl + "file");

        _manager.Add(item, startNow: true);
        await WaitForEndAsync(item);

        Assert.Equal("payload (1).bin", item.FileName);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(_dir, "payload.bin")));
    }

    [Fact]
    public async Task Queue_runs_items_up_to_the_concurrency_limit()
    {
        using var server = new TestServer(1024 * 1024);
        var settings = _manager.Settings.Clone();
        settings.MaxSimultaneousDownloads = 2;
        _manager.ApplySettings(settings);

        var items = Enumerable.Range(0, 4).Select(i =>
        {
            var it = NewItem(server.BaseUrl + "file", connections: 2);
            it.FileName = $"q{i}.bin";
            it.InQueue = true;
            return it;
        }).ToList();
        foreach (var it in items) _manager.Add(it, startNow: false);

        var finished = new TaskCompletionSource();
        _manager.QueueFinished += () => finished.TrySetResult();
        _manager.StartQueue();
        Assert.True(_manager.ActiveCount <= 2);

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.All(items, i => Assert.Equal(DownloadStatus.Completed, i.Status));
        Assert.False(_manager.QueueRunning);
    }

    [Fact]
    public async Task Speed_limiter_throttles_throughput()
    {
        var limiter = new SpeedLimiter { Limit = 200 * 1024 };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10; i++) await limiter.WaitAsync(64 * 1024, CancellationToken.None);
        // 640 KB at 200 KB/s with a 200 KB burst allowance ⇒ at least ~2.2 s.
        Assert.True(sw.Elapsed.TotalSeconds > 1.8, $"took {sw.Elapsed.TotalSeconds:0.00}s");
    }
}
