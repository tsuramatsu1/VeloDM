using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections.TrackerServer;
using MonoTorrent.TrackerServer;
using VeloDM.Core;

namespace VeloDM.Tests;

/// <summary>
/// Full swarm on loopback: an HTTP tracker, a seeding MonoTorrent engine, and VeloDM's
/// DownloadManager as the leecher.
/// </summary>
public sealed class TorrentTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VeloDM.TorrentTests", Guid.NewGuid().ToString("N"));
    private TrackerServer _tracker = null!;
    private ITrackerListener _trackerListener = null!;
    private ClientEngine _seeder = null!;
    private byte[] _payload = null!;
    private Torrent _torrent = null!;
    private string _torrentPath = null!;

    private string SeedDir => Path.Combine(_root, "seed");
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
        Directory.CreateDirectory(SeedDir);
        Directory.CreateDirectory(DownloadDir);

        _payload = new byte[3 * 1024 * 1024 + 4321];
        new Random(42).NextBytes(_payload);
        var file = Path.Combine(SeedDir, "payload.bin");
        await File.WriteAllBytesAsync(file, _payload);

        var announce = $"http://127.0.0.1:{FreePort()}/announce/";
        _tracker = new TrackerServer { AllowUnregisteredTorrents = true };
        _trackerListener = TrackerListenerFactory.CreateHttp(announce);
        _tracker.RegisterListener(_trackerListener);
        _trackerListener.Start();

        var creator = new TorrentCreator(TorrentType.V1Only);
        creator.Announces.Add([announce]);
        var encoded = await creator.CreateAsync(new TorrentFileSource(file));
        _torrentPath = Path.Combine(_root, "payload.torrent");
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
        var seed = await _seeder.AddAsync(_torrent, SeedDir);
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

    private static async Task WaitForEndAsync(DownloadItem item, int seconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (item.Status is not (DownloadStatus.Completed or DownloadStatus.Error))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Status {item.Status} ({item.StatusText}), {item.Downloaded} bytes");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Torrent_file_downloads_from_local_swarm()
    {
        using var manager = CreateManager();
        var item = new DownloadItem
        {
            Kind = DownloadKind.Torrent,
            Url = _torrentPath,
            FileName = _torrent.Name,
            SaveDirectory = DownloadDir,
            Category = Categories.Torrents,
        };
        var completed = new TaskCompletionSource();
        manager.DownloadCompleted += i => { if (i == item) completed.TrySetResult(); };

        manager.Add(item, startNow: true);
        await WaitForEndAsync(item);

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(_payload.Length, item.TotalSize);
        Assert.Equal(TorrentSources.HashOf(_torrent.InfoHashes), item.InfoHash);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(Path.Combine(DownloadDir, "payload.bin")));
    }

    [Fact]
    public async Task Magnet_link_fetches_metadata_then_downloads()
    {
        using var manager = CreateManager();
        var magnet = new MagnetLink(_torrent.InfoHashes, _torrent.Name, [_torrent.AnnounceUrls[0][0]], null, _torrent.Size);
        var item = new DownloadItem
        {
            Kind = DownloadKind.Torrent,
            Url = magnet.ToV1String(),
            FileName = TorrentSources.NameFor(magnet),
            SaveDirectory = DownloadDir,
            Category = Categories.Torrents,
        };

        manager.Add(item, startNow: true);
        await WaitForEndAsync(item);

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        Assert.Equal("payload.bin", item.FileName);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(item.FullPath));
    }

    [Fact]
    public async Task Removing_with_delete_files_cleans_up_torrent_data()
    {
        using var manager = CreateManager();
        var item = new DownloadItem
        {
            Kind = DownloadKind.Torrent,
            Url = _torrentPath,
            FileName = _torrent.Name,
            SaveDirectory = DownloadDir,
        };
        manager.Add(item, startNow: true);
        await WaitForEndAsync(item);
        Assert.True(File.Exists(item.FullPath));

        await manager.RemoveAsync([item], deleteFiles: true);

        Assert.Empty(manager.Items);
        Assert.False(File.Exists(item.FullPath));
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=x", true)]
    [InlineData("https://example.com/files/linux.torrent", true)]
    [InlineData("https://example.com/files/linux.iso", false)]
    [InlineData("magnet:?dn=no-hash", false)]
    public void Recognises_torrent_sources(string text, bool expected) =>
        Assert.Equal(expected, TorrentSources.IsTorrent(text));
}
