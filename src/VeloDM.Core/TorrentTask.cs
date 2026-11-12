using MonoTorrent.Client;

namespace VeloDM.Core;

/// <summary>Drives one torrent in the shared engine and mirrors its state onto the <see cref="DownloadItem"/>.</summary>
internal sealed class TorrentTask : IDownloadJob
{
    private const int PieceMapBuckets = 300;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly DownloadManager _manager;
    private readonly TorrentService _service;
    private readonly CancellationTokenSource _cts = new();
    private long _downloadRate;

    public DownloadItem Item { get; }
    public Task Completion { get; private set; } = Task.CompletedTask;
    public bool CompletionReported { get; private set; }

    public TorrentTask(DownloadItem item, DownloadManager manager, TorrentService service)
    {
        Item = item;
        _manager = manager;
        _service = service;
    }

    public void Start() => Completion = Task.Run(() => RunAsync(_cts.Token));

    public void Cancel()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Sample(long timestamp)
    {
        Item.Speed = Item.IsCompleted ? 0 : _downloadRate;
        Item.Eta = !Item.IsCompleted && Item.TotalSize > 0 && _downloadRate > 0
            ? TimeSpan.FromSeconds((Item.TotalSize - Item.Downloaded) / (double)_downloadRate)
            : null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        TorrentManager? torrent = null;
        try
        {
            Item.ErrorMessage = null;
            Item.Phase = "Starting…";
            Item.Status = DownloadStatus.Connecting;
            Directory.CreateDirectory(Item.SaveDirectory);

            torrent = await _service.AttachAsync(Item, ct).ConfigureAwait(false);
            if (torrent.State == TorrentState.Error) await torrent.StopAsync().ConfigureAwait(false);
            if (torrent.State is TorrentState.Stopped or TorrentState.Paused) await torrent.StartAsync().ConfigureAwait(false);
            Item.Status = DownloadStatus.Downloading;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Mirror(torrent);

                if (torrent.State == TorrentState.Error)
                    throw new IOException(torrent.Error?.Exception?.Message ?? "The torrent stopped with an error.",
                        torrent.Error?.Exception);

                if (torrent.Complete && torrent.State == TorrentState.Seeding)
                {
                    if (!Item.IsCompleted)
                    {
                        Item.InQueue = false;
                        Item.Status = DownloadStatus.Completed;
                        CompletionReported = true;
                        _manager.ReportCompleted(Item);
                    }
                    var s = _manager.Settings;
                    if (!s.SeedAfterDownload || Ratio(torrent) >= s.SeedRatioLimit) break;
                }

                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!Item.IsCompleted) Item.Status = DownloadStatus.Paused;
        }
        catch (Exception ex)
        {
            Item.ErrorMessage = ex.Message;
            Item.Status = DownloadStatus.Error;
        }
        finally
        {
            if (torrent is not null)
            {
                try { await torrent.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (Exception) { /* engine may be shutting down */ }
            }
            _downloadRate = 0;
            Item.Phase = null;
            Item.Speed = 0;
            Item.UploadSpeed = 0;
            Item.Eta = null;
            Item.ActiveConnections = 0;
            Item.Seeds = Item.Peers = 0;
            Item.NotifyProgress();
            _cts.Dispose();
        }
    }

    private static double Ratio(TorrentManager t) =>
        t.Torrent is { Size: > 0 } info ? t.Monitor.DataBytesSent / (double)info.Size : 0;

    private void Mirror(TorrentManager t)
    {
        Item.Phase = t.State switch
        {
            TorrentState.Metadata => "Fetching metadata…",
            TorrentState.FetchingHashes => "Fetching hashes…",
            TorrentState.Hashing => $"Checking files {t.PartialProgress:0}%",
            TorrentState.Starting => "Starting…",
            TorrentState.Seeding => $"Seeding (ratio {Ratio(t):0.00})",
            TorrentState.Downloading when t.OpenConnections == 0 => "Looking for peers…",
            _ => null,
        };

        if (t.HasMetadata && t.Torrent is { } info)
        {
            if (Item.TotalSize != info.Size) Item.TotalSize = info.Size;
            if (Item.FileName != info.Name) Item.FileName = info.Name;
            Item.TorrentBytes = (long)(info.Size * (t.Progress / 100.0));
            Item.PieceMap = BuildPieceMap(t);
            Item.TorrentFiles = t.Files
                .Select(f => new TorrentFileState(f.Path, f.Length, f.BitField.PercentComplete, f.Priority.ToString()))
                .ToArray();
        }

        _downloadRate = t.Monitor.DownloadRate;
        Item.UploadSpeed = t.Monitor.UploadRate;
        Item.ActiveConnections = t.OpenConnections;
        Item.Seeds = t.Peers.Seeds;
        Item.Peers = t.Peers.Leechs;
    }

    /// <summary>Downsamples the piece bitfield into fractions so the UI can draw it at any width.</summary>
    private static double[] BuildPieceMap(TorrentManager t)
    {
        var bits = t.Bitfield;
        int buckets = Math.Min(PieceMapBuckets, Math.Max(1, bits.Length));
        var map = new double[buckets];
        for (int b = 0; b < buckets; b++)
        {
            int from = (int)((long)b * bits.Length / buckets);
            int to = (int)((long)(b + 1) * bits.Length / buckets);
            if (to <= from) to = from + 1;
            int have = 0;
            for (int i = from; i < to && i < bits.Length; i++)
                if (bits[i]) have++;
            map[b] = have / (double)(to - from);
        }
        return map;
    }
}
