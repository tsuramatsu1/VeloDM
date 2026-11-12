using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections.TrackerServer;
using MonoTorrent.TrackerServer;
using VeloDM.Core;

namespace VeloDM.Tests;

/// <summary>Choosing files inside a torrent, against a two-file torrent seeded on loopback.</summary>
public sealed class TorrentSelectionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VeloDM.TorrentTests", Guid.NewGuid().ToString("N"));
    private TrackerServer _tracker = null!;
    private ITrackerListener _trackerListener = null!;
    private ClientEngine _seeder = null!;
    private byte[] _wanted = null!;
    private byte[] _other = null!;
    private Torrent _torrent = null!;
    private string _torrentPath = null!;

    private string DownloadDir => Path.Combine(_root, "download");

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async Task InitializeAsync()
    {
        var content = Path.Combine(_root, "seed", "album");
        Directory.CreateDirectory(content);
        Directory.CreateDirectory(DownloadDir);

        _wanted = new byte[1024 * 1024 + 111];
        _other = new byte[2 * 1024 * 1024 + 222];
        new Random(1).NextBytes(_wanted);
        new Random(2).NextBytes(_other);
        await File.WriteAllBytesAsync(Path.Combine(content, "a-wanted.bin"), _wanted);
        await File.WriteAllBytesAsync(Path.Combine(content, "b-other.bin"), _other);

        var announce = $"http://127.0.0.1:{FreePort()}/announce/";
        _tracker = new TrackerServer { AllowUnregisteredTorrents = true };
        _trackerListener = TrackerListenerFactory.CreateHttp(announce);
        _tracker.RegisterListener(_trackerListener);
        _trackerListener.Start();

        var creator = new TorrentCreator(TorrentType.V1Only);
        creator.Announces.Add([announce]);
        var encoded = await creator.CreateAsync(new TorrentFileSource(content));
        _torrentPath = Path.Combine(_root, "album.torrent");
        await File.WriteAllBytesAsync(_torrentPath, encoded.Encode());
        _torrent = Torrent.Load(_torrentPath);

        _seeder = new ClientEngine(new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(_root, "seed-cache"),
            ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Loopback, FreePort()) },
            DhtEndPoint = null,
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false,
        }.ToSettings());
        var seed = await _seeder.AddAsync(_torrent, Path.Combine(_root, "seed"));
        await seed.StartAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (seed.State != TorrentState.Seeding)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Seeder stuck in {seed.State}");
            await Task.Delay(50);
        }
    }

    public async Task DisposeAsync()
    {
        await _seeder.StopAllAsync();
        _seeder.Dispose();
        _trackerListener.Stop();
        _tracker.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private DownloadManager CreateManager()
    {
        var manager = new DownloadManager(Path.Combine(_root, "app-" + Guid.NewGuid().ToString("N")[..6]));
        var settings = manager.Settings.Clone();
        settings.TorrentPort = FreePort();
        settings.TorrentDht = false;
        settings.TorrentPortForwarding = false;
        manager.ApplySettings(settings);
        return manager;
    }

    private static async Task WaitForAsync(DownloadItem item, Func<DownloadItem, bool> condition, int seconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition(item))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Status {item.Status} ({item.StatusText}), {item.Downloaded} of {item.TotalSize} bytes");
            await Task.Delay(100);
        }
    }

    private string PathOf(string fileName) => Path.Combine(DownloadDir, _torrent.Name, fileName);

    [Fact]
    public async Task Deselected_file_is_not_downloaded_and_can_be_added_later()
    {
        using var manager = CreateManager();
        var skippedPath = _torrent.Files.Single(f => f.Path.ToString().EndsWith("b-other.bin")).Path.ToString();
        var item = new DownloadItem
        {
            Kind = DownloadKind.Torrent,
            Url = _torrentPath,
            FileName = _torrent.Name,
            SaveDirectory = DownloadDir,
            Category = Categories.Torrents,
            SkippedFiles = [skippedPath],
        };
        int completions = 0;
        manager.DownloadCompleted += i => { if (i == item) Interlocked.Increment(ref completions); };

        manager.Add(item, startNow: true);
        await WaitForAsync(item, i => i.Status is DownloadStatus.Completed or DownloadStatus.Error);

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        Assert.Equal(_wanted.Length, item.TotalSize);
        Assert.Equal(_wanted, await File.ReadAllBytesAsync(PathOf("a-wanted.bin")));
        // Only the piece shared with the wanted file may have been fetched from the other one.
        Assert.False(File.Exists(PathOf("b-other.bin")) && (await File.ReadAllBytesAsync(PathOf("b-other.bin"))).SequenceEqual(_other),
            "the deselected file was downloaded in full");
        Assert.Equal("DoNotDownload", item.TorrentFiles.Single(f => f.Path == skippedPath).Priority);

        await manager.SetSkippedFilesAsync(item, []);
        await WaitForAsync(item, i => i.Status == DownloadStatus.Completed && i.TotalSize == _torrent.Size);

        Assert.Equal(_other, await File.ReadAllBytesAsync(PathOf("b-other.bin")));
        Assert.Equal(2, Volatile.Read(ref completions));
    }
}
