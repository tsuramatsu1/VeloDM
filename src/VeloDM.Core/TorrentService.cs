using System.Net;
using MonoTorrent;
using MonoTorrent.Client;

namespace VeloDM.Core;

/// <summary>
/// Owns the single MonoTorrent <see cref="ClientEngine"/>. The engine (and its listening
/// socket, DHT and UPnP mapping) is only created when the first torrent runs, so users who
/// never download torrents never open a public port.
/// </summary>
public sealed class TorrentService : IDisposable
{
    private readonly string _cacheDirectory;
    private readonly Func<AppSettings> _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClientEngine? _engine;

    public TorrentService(string cacheDirectory, Func<AppSettings> settings)
    {
        _cacheDirectory = cacheDirectory;
        _settings = settings;
        Directory.CreateDirectory(Path.Combine(_cacheDirectory, "files"));
    }

    public bool IsEngineRunning => _engine is not null;

    private EngineSettings BuildSettings(AppSettings s) => new EngineSettingsBuilder
    {
        CacheDirectory = _cacheDirectory,
        AutoSaveLoadFastResume = true,
        AutoSaveLoadMagnetLinkMetadata = true,
        AutoSaveLoadDhtCache = true,
        AllowPortForwarding = s.TorrentPortForwarding,
        AllowLocalPeerDiscovery = true,
        ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Any, s.TorrentPort) },
        DhtEndPoint = s.TorrentDht ? new IPEndPoint(IPAddress.Any, s.TorrentPort) : null,
        MaximumDownloadRate = (int)Math.Min(int.MaxValue, s.SpeedLimitBytes),
        MaximumUploadRate = (int)Math.Min(int.MaxValue, Math.Max(0, s.TorrentUploadLimitKBps) * 1024L),
    }.ToSettings();

    private ClientEngine Engine => _engine ??= new ClientEngine(BuildSettings(_settings()));

    public async Task ApplySettingsAsync(AppSettings settings)
    {
        if (_engine is null) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _engine.UpdateSettingsAsync(BuildSettings(settings)).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Returns the engine's manager for this item, registering the torrent on first use.</summary>
    internal async Task<TorrentManager> AttachAsync(DownloadItem item, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var engine = Engine;
            if (Find(item) is { } existing) return existing;

            var settings = new TorrentSettingsBuilder { CreateContainingDirectory = true }.ToSettings();
            TorrentManager manager;
            if (TorrentSources.IsMagnet(item.Url))
            {
                manager = await engine.AddAsync(MagnetLink.Parse(item.Url.Trim()), item.SaveDirectory, settings).ConfigureAwait(false);
            }
            else
            {
                var torrent = TorrentSources.Parse(await GetTorrentBytesAsync(item, ct).ConfigureAwait(false));
                manager = await engine.AddAsync(torrent, item.SaveDirectory, settings).ConfigureAwait(false);
            }
            item.InfoHash = TorrentSources.HashOf(manager.InfoHashes);
            await ApplyFileSelectionAsync(item, manager).ConfigureAwait(false);
            return manager;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Applies <see cref="DownloadItem.SkippedFiles"/> to a torrent the engine already knows.</summary>
    internal async Task ApplyFileSelectionAsync(DownloadItem item)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Find(item) is { } manager) await ApplyFileSelectionAsync(item, manager).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Does nothing until the file list is known (a magnet link still fetching its metadata).</summary>
    internal static async Task ApplyFileSelectionAsync(DownloadItem item, TorrentManager manager)
    {
        if (!manager.HasMetadata) return;
        var skipped = new HashSet<string>(item.SkippedFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var file in manager.Files)
        {
            var wanted = skipped.Contains(file.Path) ? Priority.DoNotDownload : Priority.Normal;
            if (file.Priority != wanted) await manager.SetFilePriorityAsync(file, wanted).ConfigureAwait(false);
        }
    }

    private TorrentManager? Find(DownloadItem item)
    {
        if (_engine is null || string.IsNullOrEmpty(item.InfoHash)) return null;
        var hash = InfoHash.FromHex(item.InfoHash);
        return _engine.Torrents.FirstOrDefault(t => t.InfoHashes.Contains(hash));
    }

    private string CachedTorrentPath(DownloadItem item) => Path.Combine(_cacheDirectory, "files", item.Id.ToString("N") + ".torrent");

    /// <summary>Keeps a private copy so the original .torrent can be moved/deleted or its URL can expire.</summary>
    public void StoreTorrentFile(DownloadItem item, byte[] bytes) => File.WriteAllBytes(CachedTorrentPath(item), bytes);

    private async Task<byte[]> GetTorrentBytesAsync(DownloadItem item, CancellationToken ct)
    {
        var cached = CachedTorrentPath(item);
        if (File.Exists(cached)) return await File.ReadAllBytesAsync(cached, ct).ConfigureAwait(false);
        var bytes = await TorrentSources.ReadTorrentBytesAsync(item.Url, item.Referrer, item.Cookies,
            item.UserAgent ?? _settings().UserAgent, ct).ConfigureAwait(false);
        StoreTorrentFile(item, bytes);
        return bytes;
    }

    /// <summary>Unregisters the torrent and optionally deletes what it downloaded.</summary>
    internal async Task RemoveAsync(DownloadItem item, bool deleteData)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Find(item) is { } manager)
            {
                if (manager.State is not (TorrentState.Stopped or TorrentState.Error))
                    await manager.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await _engine!.RemoveAsync(manager, deleteData ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly)
                    .ConfigureAwait(false);
            }
            else if (deleteData)
            {
                DeletePath(item.FullPath);
            }
            TryDeleteFile(CachedTorrentPath(item));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void DeletePath(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        var engine = _engine;
        _engine = null;
        if (engine is null) return;
        try { engine.StopAllAsync(TimeSpan.FromSeconds(5)).Wait(TimeSpan.FromSeconds(8)); }
        catch (Exception) { /* shutting down: fast-resume data was saved when each torrent stopped */ }
        engine.Dispose();
    }
}
