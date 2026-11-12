using System.Buffers;
using System.Net;
using Microsoft.Win32.SafeHandles;

namespace VeloDM.Core;

/// <summary>
/// One running download. Workers write straight into the preallocated temp file at their
/// own offsets, so there is no merge step. Whenever a connection slot is free, the segment
/// with the most bytes left is split in half and the new half gets its own connection
/// (dynamic segmentation, as in IDM), so fast connections keep taking work from slow ones.
/// </summary>
internal sealed class DownloadTask : IDownloadJob
{
    private const int BufferSize = 64 * 1024;
    // Must stay well above BufferSize: a split never lands inside bytes a worker has in flight.
    internal const long MinSplitSize = 512 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private readonly DownloadManager _manager;
    private readonly CancellationTokenSource _cts = new();
    private readonly SpeedLimiter _ownLimiter = new();

    private long _lastSampleBytes = -1;
    private long _lastSampleTimestamp;

    public DownloadItem Item { get; }
    public Task Completion { get; private set; } = Task.CompletedTask;
    public bool CompletionReported => false;

    /// <summary>Exponentially smoothed speed from the byte count delta since the last sample.</summary>
    public void Sample(long timestamp)
    {
        long bytes = Item.Downloaded;
        if (_lastSampleBytes >= 0)
        {
            double seconds = (timestamp - _lastSampleTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
            double instant = seconds > 0 ? Math.Max(0, bytes - _lastSampleBytes) / seconds : 0;
            Item.Speed = Item.Speed <= 0 ? instant : Item.Speed * 0.7 + instant * 0.3;
        }
        _lastSampleBytes = bytes;
        _lastSampleTimestamp = timestamp;
        Item.Eta = Item.TotalSize > 0 && Item.Speed > 1
            ? TimeSpan.FromSeconds((Item.TotalSize - bytes) / Item.Speed)
            : null;
    }

    public DownloadTask(DownloadItem item, DownloadManager manager)
    {
        Item = item;
        _manager = manager;
    }

    public void Start() => Completion = Task.Run(() => RunAsync(_cts.Token));

    public void Cancel()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private string UserAgent => string.IsNullOrWhiteSpace(Item.UserAgent) ? _manager.Settings.UserAgent : Item.UserAgent!;

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            Item.ErrorMessage = null;
            Item.Status = DownloadStatus.Connecting;
            await PrepareAsync(ct).ConfigureAwait(false);
            Item.Status = DownloadStatus.Downloading;
            await DownloadSegmentsAsync(ct).ConfigureAwait(false);
            FinishFile();
            Item.InQueue = false;
            Item.Status = DownloadStatus.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Item.Status = DownloadStatus.Paused;
        }
        catch (Exception ex)
        {
            Item.ErrorMessage = LooksExpired(ex)
                ? ex.Message + " The link may have expired: right-click the download and choose Refresh download address."
                : ex.Message;
            Item.Status = DownloadStatus.Error;
        }
        finally
        {
            Item.Speed = 0;
            Item.Eta = null;
            Item.ActiveConnections = 0;
            Item.NotifyProgress();
            _cts.Dispose();
        }
    }

    private async Task PrepareAsync(CancellationToken ct)
    {
        var info = await HttpEngine.ProbeAsync(Item.Url, Item.Referrer, Item.Cookies, UserAgent, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(Item.FileName))
            Item.FileName = info.FileName;
        if (string.IsNullOrWhiteSpace(Item.SaveDirectory))
            Item.SaveDirectory = Categories.FolderFor(_manager.Settings, Item.Category);
        Directory.CreateDirectory(Item.SaveDirectory);

        bool resume;
        lock (Item.SegmentsLock)
        {
            bool hasProgress = Item.Segments.Any(s => s.Downloaded > 0);
            resume = hasProgress && info.AcceptRanges && info.Size > 0 && Item.TotalSize == info.Size
                     && File.Exists(Item.TempPath);
            if (!resume)
            {
                Item.Segments.Clear();
                if (info.Size != 0)
                    Item.Segments.Add(new Segment { Start = 0, End = info.Size > 0 ? info.Size - 1 : -1 });
            }
        }
        if (!resume && File.Exists(Item.TempPath))
            File.Delete(Item.TempPath);

        Item.TotalSize = info.Size;
        Item.ResumeSupported = info.AcceptRanges;
    }

    private async Task DownloadSegmentsAsync(CancellationToken ct)
    {
        bool ranges = Item.ResumeSupported == true && Item.TotalSize > 0;
        int maxConnections = ranges ? Math.Clamp(Item.MaxConnections, 1, 32) : 1;

        bool resuming = File.Exists(Item.TempPath);
        using var handle = File.OpenHandle(Item.TempPath, resuming ? FileMode.Open : FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, FileOptions.Asynchronous, preallocationSize: resuming ? 0 : Math.Max(0, Item.TotalSize));
        using var workersCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = new Dictionary<Task, Segment>();
        int errors = 0;
        Exception? lastError = null;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                FillConnections(running, maxConnections, ranges, handle, workersCts.Token);

                if (running.Count == 0)
                {
                    bool done;
                    lock (Item.SegmentsLock) done = Item.Segments.All(s => s.IsComplete);
                    if (done) return;
                    throw lastError ?? new IOException("The download stalled.");
                }

                var finished = await Task.WhenAny(running.Keys).ConfigureAwait(false);
                var segment = running[finished];
                running.Remove(finished);
                lock (Item.SegmentsLock) segment.Active = false;

                if (finished.IsCompletedSuccessfully) { errors = 0; continue; }
                ct.ThrowIfCancellationRequested();

                var ex = finished.Exception!.GetBaseException();
                lastError = ex;
                Log.Warn($"\"{Item.FileName}\": a connection failed at {Format.Size(Item.Downloaded)} of {Item.SizeText}", ex);
                if (ex is RangeNotSupportedException || IsPermanentHttpError(ex)) throw ex;

                if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable }
                    && running.Count > 0)
                {
                    // The server caps connections per client: settle for what it accepts.
                    maxConnections = running.Count;
                    continue;
                }

                if (segment.LastRunBytes > 0) errors = 0;
                if (++errors > _manager.Settings.MaxRetries) throw ex;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(errors, 5)), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            workersCts.Cancel();
            try { await Task.WhenAll(running.Keys).ConfigureAwait(false); } catch { /* already reported or cancelled */ }
            lock (Item.SegmentsLock)
                foreach (var s in Item.Segments) { s.Active = false; s.State = s.IsComplete ? "Completed" : ""; }
            Item.ActiveConnections = 0;
        }
    }

    private void FillConnections(Dictionary<Task, Segment> running, int maxConnections, bool ranges,
                                 SafeFileHandle handle, CancellationToken ct)
    {
        lock (Item.SegmentsLock)
        {
            foreach (var s in Item.Segments)
            {
                if (running.Count >= maxConnections) break;
                if (!s.IsComplete && !s.Active) running.Add(StartWorker(s, handle, ranges, ct), s);
            }

            while (ranges && running.Count < maxConnections)
            {
                Segment? largest = null;
                foreach (var s in Item.Segments)
                    if (s.Active && !s.IsComplete && (largest is null || s.Remaining > largest.Remaining))
                        largest = s;
                if (largest is null || TrySplit(largest) is not { } split) break;
                Item.Segments.Insert(Item.Segments.IndexOf(largest) + 1, split);
                running.Add(StartWorker(split, handle, ranges, ct), split);
            }

            Item.ActiveConnections = running.Count;
        }
    }

    private static Segment? TrySplit(Segment s)
    {
        lock (s.Sync)
        {
            if (s.End < 0) return null;
            long remaining = s.End - s.Position + 1;
            if (remaining < MinSplitSize * 2) return null;
            long middle = s.Position + remaining / 2;
            var tail = new Segment { Start = middle, End = s.End };
            s.End = middle - 1;
            return tail;
        }
    }

    /// <summary>A partly downloaded file whose address now answers "forbidden" or "gone": typical of expiring links.</summary>
    private bool LooksExpired(Exception ex) =>
        Item.Downloaded > 0
        && ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone
                                                    or HttpStatusCode.Unauthorized };

    private static bool IsPermanentHttpError(Exception ex) =>
        ex is HttpRequestException { StatusCode: { } code }
        && (int)code is >= 400 and < 500
        && code is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);

    private Task StartWorker(Segment s, SafeFileHandle handle, bool ranges, CancellationToken ct)
    {
        s.Active = true;
        s.LastRunBytes = 0;
        s.State = "Connecting";
        return Task.Run(() => RunSegmentAsync(s, handle, ranges, ct), CancellationToken.None);
    }

    private async Task RunSegmentAsync(Segment seg, SafeFileHandle handle, bool ranges, CancellationToken ct)
    {
        long from, to;
        lock (seg.Sync)
        {
            if (!ranges) { seg.Downloaded = 0; seg.Eof = false; } // no resume: start over
            from = seg.Position;
            to = seg.End;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadTimeout);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var request = HttpEngine.CreateRequest(Item.Url, Item.Referrer, Item.Cookies, UserAgent,
                ranges ? from : null, ranges ? to : null);
            using var response = await HttpEngine.Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            HttpEngine.EnsureSuccess(response);
            if (ranges && from > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                throw new RangeNotSupportedException();

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            seg.State = "Receiving";

            while (true)
            {
                int want = BufferSize;
                lock (seg.Sync)
                {
                    if (seg.End >= 0)
                    {
                        long remaining = seg.End - seg.Position + 1;
                        if (remaining <= 0) break;
                        if (remaining < want) want = (int)remaining;
                    }
                }

                timeout.CancelAfter(ReadTimeout);
                int read = await stream.ReadAsync(buffer.AsMemory(0, want), timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    lock (seg.Sync)
                    {
                        if (seg.End >= 0) throw new IOException("The server closed the connection early.");
                        seg.Eof = true;
                    }
                    break;
                }

                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), seg.Position, ct).ConfigureAwait(false);
                lock (seg.Sync) seg.Downloaded += read;
                seg.LastRunBytes += read;
                await _manager.Limiter.WaitAsync(read, ct).ConfigureAwait(false);
                _ownLimiter.Limit = Item.SpeedLimitKBps * 1024L;
                await _ownLimiter.WaitAsync(read, ct).ConfigureAwait(false);
            }
            seg.State = "Completed";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            seg.State = "Timed out";
            throw new TimeoutException("The connection timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            seg.State = "Error";
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void FinishFile()
    {
        if (Item.TotalSize < 0) Item.TotalSize = Item.Downloaded;
        var finalName = FileNameHelper.MakeUnique(Item.SaveDirectory, Item.FileName, ignoreTempFiles: true);
        var tempPath = Item.TempPath;
        File.Move(tempPath, Path.Combine(Item.SaveDirectory, finalName));
        Item.FileName = finalName;
    }
}
