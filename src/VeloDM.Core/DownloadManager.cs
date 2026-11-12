using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VeloDM.Core;

/// <summary>
/// Owns the download list, running jobs (HTTP and BitTorrent), the queue, the scheduler and
/// persistence. <see cref="Items"/> must only be mutated (Add/Remove) from the UI thread;
/// background readers take <c>_sync</c>.
/// </summary>
public sealed class DownloadManager : IDisposable
{
    private readonly object _sync = new();
    private readonly object _saveLock = new();
    private readonly Dictionary<Guid, IDownloadJob> _running = new();
    private readonly Timer _timer;
    private readonly string _downloadsPath;
    private readonly string _settingsPath;
    private int _ticks;
    private int _inTick;
    private DateOnly? _lastScheduledStart, _lastScheduledStop;
    private bool _disposed;
    private readonly CancellationTokenSource _shutdown = new();

    public ObservableCollection<DownloadItem> Items { get; } = new();
    public AppSettings Settings { get; private set; }
    public SpeedLimiter Limiter { get; } = new();
    public TorrentService Torrents { get; }
    public string DataDirectory { get; }
    public bool QueueRunning { get; private set; }
    public double TotalSpeed { get; private set; }

    /// <summary>Downloads still transferring (torrents that are only seeding are not counted).</summary>
    public int ActiveCount { get { lock (_sync) return _running.Values.Count(j => !j.Item.IsCompleted); } }

    public event Action<DownloadItem>? DownloadCompleted;
    public event Action<DownloadItem>? DownloadFailed;
    /// <summary>Raised when the after-download steps (virus scan, unpacking) of an item have finished.</summary>
    public event Action<DownloadItem, PostProcessResult>? PostProcessed;

    /// <summary>Finds yt-dlp and FFmpeg for streamed video; null when they are not installed.</summary>
    public Func<VideoToolPaths?> LocateVideoTools { get; set; }
    public event Action? QueueStateChanged;
    public event Action? QueueFinished;
    public event Action? Ticked;

    public DownloadManager(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? DefaultDataDirectory;
        Directory.CreateDirectory(DataDirectory);
        _downloadsPath = Path.Combine(DataDirectory, "downloads.json");
        _settingsPath = Path.Combine(DataDirectory, "settings.json");
        Settings = AppSettings.Load(_settingsPath);
        Limiter.Limit = Settings.SpeedLimitBytes;
        HttpEngine.ConfigureProxy(Settings);
        LocateVideoTools = () => VideoTools.Locate(DataDirectory);
        Torrents = new TorrentService(Path.Combine(DataDirectory, "torrents"), () => Settings);
        _timer = new Timer(OnTick, null, 500, 500);
    }

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VeloDM");

    // ---- persistence ------------------------------------------------------------

    public void LoadDownloads()
    {
        if (!File.Exists(_downloadsPath)) return;
        List<DownloadItem>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<DownloadItem>>(File.ReadAllText(_downloadsPath), AppSettings.JsonOptions);
        }
        catch (JsonException)
        {
            File.Copy(_downloadsPath, _downloadsPath + ".corrupt", overwrite: true);
            return;
        }
        lock (_sync)
        {
            foreach (var item in items ?? [])
            {
                if (item.Status is DownloadStatus.Connecting or DownloadStatus.Downloading)
                    item.Status = DownloadStatus.Paused;
                Items.Add(item);
            }
        }
    }

    public void Save()
    {
        DownloadItem[] snapshot;
        lock (_sync) snapshot = Items.ToArray();
        var array = new JsonArray();
        foreach (var item in snapshot)
            lock (item.SegmentsLock)
                array.Add(JsonSerializer.SerializeToNode(item, AppSettings.JsonOptions));

        lock (_saveLock)
        {
            var tmp = _downloadsPath + ".tmp";
            File.WriteAllText(tmp, array.ToJsonString(AppSettings.JsonOptions));
            File.Move(tmp, _downloadsPath, overwrite: true);
        }
    }

    public void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        Limiter.Limit = settings.SpeedLimitBytes;
        HttpEngine.ConfigureProxy(settings);
        settings.Save(_settingsPath);
        _ = Torrents.ApplySettingsAsync(settings).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        PumpQueue();
    }

    // ---- download control ---------------------------------------------------------

    public DownloadItem? FindByUrl(string url)
    {
        lock (_sync) return Items.FirstOrDefault(i => string.Equals(i.Url, url, StringComparison.Ordinal));
    }

    public DownloadItem? FindByInfoHash(string? infoHash)
    {
        if (string.IsNullOrEmpty(infoHash)) return null;
        lock (_sync)
            return Items.FirstOrDefault(i => string.Equals(i.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsRunning(DownloadItem item)
    {
        lock (_sync) return _running.ContainsKey(item.Id);
    }

    /// <summary>Adds a download. Call on the UI thread.</summary>
    public void Add(DownloadItem item, bool startNow)
    {
        lock (_sync) Items.Add(item);
        if (startNow)
            Start(item);
        else if (item.InQueue)
        {
            item.Status = DownloadStatus.Queued;
            PumpQueue();
        }
        Save();
    }

    public void Start(DownloadItem item)
    {
        IDownloadJob job;
        lock (_sync)
        {
            if (_disposed || _running.ContainsKey(item.Id) || item.Status == DownloadStatus.Completed) return;
            job = item.Kind switch
            {
                DownloadKind.Torrent => new TorrentTask(item, this, Torrents),
                DownloadKind.Stream => new StreamTask(item, this),
                _ => new DownloadTask(item, this),
            };
            _running[item.Id] = job;
            job.Start();
        }
        job.Completion.ContinueWith(_ => OnJobEnded(job), TaskScheduler.Default);
    }

    /// <summary>Pauses a download, or stops seeding a finished torrent.</summary>
    public void Pause(DownloadItem item)
    {
        IDownloadJob? job;
        lock (_sync) _running.TryGetValue(item.Id, out job);
        if (job is not null)
            job.Cancel();
        else if (item.Status == DownloadStatus.Queued)
            item.Status = DownloadStatus.Paused;
    }

    public void StopAll()
    {
        StopQueue();
        DownloadItem[] items;
        lock (_sync) items = Items.ToArray();
        foreach (var i in items) Pause(i);
    }

    /// <summary>Restarts a download from scratch.</summary>
    public async Task RedownloadAsync(DownloadItem item)
    {
        await StopAndWaitAsync(item);
        if (item.IsTorrent)
            await Torrents.RemoveAsync(item, deleteData: true);
        else if (item.IsStream)
            StreamTask.DeletePartialFiles(item);
        else
            TryDelete(item.TempPath);
        item.ResetProgress();
        item.TotalSize = -1;
        item.Status = DownloadStatus.Paused;
        Start(item);
    }

    /// <summary>Removes items from the list. Call on the UI thread.</summary>
    public async Task RemoveAsync(IReadOnlyList<DownloadItem> items, bool deleteFiles)
    {
        foreach (var item in items)
        {
            await StopAndWaitAsync(item);
            lock (_sync) Items.Remove(item);
            if (item.IsTorrent)
            {
                await Torrents.RemoveAsync(item, deleteFiles);
                continue;
            }
            if (item.IsStream) StreamTask.DeletePartialFiles(item);
            else TryDelete(item.TempPath);
            if (deleteFiles && item.IsCompleted) TryDelete(item.FullPath);
        }
        Save();
    }

    /// <summary>
    /// Points an unfinished download at a new address (for links that expire) and keeps what is
    /// already on disk. The download is stopped first; the caller decides whether to start it again.
    /// </summary>
    public async Task UpdateAddressAsync(DownloadItem item, string url, string? referrer, string? cookies, string? userAgent)
    {
        if (item.Kind != DownloadKind.Http) throw new InvalidOperationException("Only HTTP downloads have an address to refresh.");
        await StopAndWaitAsync(item);
        item.Url = url;
        if (!string.IsNullOrWhiteSpace(referrer)) item.Referrer = referrer;
        if (!string.IsNullOrWhiteSpace(cookies)) item.Cookies = cookies;
        if (!string.IsNullOrWhiteSpace(userAgent)) item.UserAgent = userAgent;
        item.ErrorMessage = null;
        if (item.Status == DownloadStatus.Error) item.Status = DownloadStatus.Paused;
        Save();
    }

    /// <summary>
    /// Chooses which files of a torrent are downloaded. A finished torrent that gains files
    /// goes back to downloading.
    /// </summary>
    public async Task SetSkippedFilesAsync(DownloadItem item, IReadOnlyCollection<string> skipped)
    {
        if (!item.IsTorrent) throw new InvalidOperationException("Only torrents have a file selection.");
        bool addsFiles = item.SkippedFiles.Any(path => !skipped.Contains(path, StringComparer.OrdinalIgnoreCase));
        bool resume = item.IsCompleted && addsFiles;
        if (resume) await StopAndWaitAsync(item);

        item.SkippedFiles = skipped.ToList();
        await Torrents.ApplyFileSelectionAsync(item);
        if (resume)
        {
            item.Status = DownloadStatus.Paused;
            item.DateCompleted = null;
            Start(item);
        }
        Save();
    }

    private async Task StopAndWaitAsync(DownloadItem item)
    {
        IDownloadJob? job;
        lock (_sync) _running.TryGetValue(item.Id, out job);
        if (job is null) return;
        job.Cancel();
        try { await job.Completion; } catch { /* status already recorded on the item */ }
        // OnJobEnded runs as a continuation; make sure the slot is released before we return.
        Release(job);
    }

    private void Release(IDownloadJob job)
    {
        lock (_sync)
            if (_running.TryGetValue(job.Item.Id, out var current) && current == job)
                _running.Remove(job.Item.Id);
    }

    /// <summary>Raised by jobs that finish downloading but keep running (seeding torrents).</summary>
    internal void ReportCompleted(DownloadItem item)
    {
        try { Save(); } catch (IOException) { }
        DownloadCompleted?.Invoke(item);
        StartPostProcessing(item);
        PumpQueue();
    }

    /// <summary>Runs the virus scan and unpacking in the background; the queue does not wait for them.</summary>
    private void StartPostProcessing(DownloadItem item)
    {
        var settings = Settings;
        if (!settings.ScanAfterDownload && !settings.ExtractArchives) return;
        var ct = _shutdown.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // A seeding torrent keeps showing its own state.
                var result = await PostProcessor.RunAsync(item, settings, phase => { if (!item.IsTorrent) item.Phase = phase; }, ct)
                    .ConfigureAwait(false);
                item.Note = result.Note;
                try { Save(); } catch (IOException) { }
                PostProcessed?.Invoke(item, result);
            }
            catch (OperationCanceledException)
            {
                // The app is closing.
            }
            catch (Exception ex)
            {
                item.Note = "After-download steps failed: " + ex.Message;
            }
        }, CancellationToken.None);
    }

    private void OnJobEnded(IDownloadJob job)
    {
        var item = job.Item;
        Release(job);
        try { Save(); } catch (IOException) { }

        if (item.Status == DownloadStatus.Completed && !job.CompletionReported)
        {
            DownloadCompleted?.Invoke(item);
            StartPostProcessing(item);
        }
        else if (item.Status == DownloadStatus.Error) DownloadFailed?.Invoke(item);
        PumpQueue();
    }

    // ---- queue ------------------------------------------------------------------------

    public void SetInQueue(DownloadItem item, bool inQueue)
    {
        item.InQueue = inQueue && !item.IsCompleted;
        if (item.InQueue && !IsRunning(item) && item.Status is DownloadStatus.Paused or DownloadStatus.Error)
            item.Status = DownloadStatus.Queued;
        else if (!item.InQueue && item.Status == DownloadStatus.Queued)
            item.Status = DownloadStatus.Paused;
        PumpQueue();
    }

    public void StartQueue()
    {
        lock (_sync)
        {
            foreach (var i in Items)
                if (i.InQueue && !_running.ContainsKey(i.Id) && i.Status is DownloadStatus.Paused or DownloadStatus.Error)
                    i.Status = DownloadStatus.Queued;
            QueueRunning = true;
        }
        QueueStateChanged?.Invoke();
        PumpQueue();
    }

    public void StopQueue()
    {
        IDownloadJob[] queued;
        lock (_sync)
        {
            if (!QueueRunning) return;
            QueueRunning = false;
            queued = _running.Values.Where(j => j.Item.InQueue).ToArray();
        }
        foreach (var j in queued) j.Cancel();
        QueueStateChanged?.Invoke();
    }

    private void PumpQueue()
    {
        var toStart = new List<DownloadItem>();
        bool finished = false;
        lock (_sync)
        {
            if (!QueueRunning || _disposed) return;
            int active = _running.Values.Count(j => j.Item.InQueue);
            foreach (var i in Items)
            {
                if (active >= Math.Max(1, Settings.MaxSimultaneousDownloads)) break;
                if (i.InQueue && i.Status == DownloadStatus.Queued && !_running.ContainsKey(i.Id))
                {
                    toStart.Add(i);
                    active++;
                }
            }
            if (active == 0)
            {
                QueueRunning = false;
                finished = true;
            }
        }
        foreach (var i in toStart) Start(i);
        if (finished)
        {
            QueueStateChanged?.Invoke();
            QueueFinished?.Invoke();
        }
    }

    // ---- periodic work: speed, ETA, autosave, scheduler --------------------------------------

    private void OnTick(object? state)
    {
        if (Interlocked.Exchange(ref _inTick, 1) == 1) return;
        try
        {
            IDownloadJob[] jobs;
            lock (_sync) jobs = _running.Values.ToArray();
            long now = Stopwatch.GetTimestamp();
            double total = 0;

            foreach (var job in jobs)
            {
                job.Sample(now);
                total += job.Item.Speed;
                job.Item.NotifyProgress();
            }
            TotalSpeed = total;
            Ticked?.Invoke();

            if (++_ticks % 20 == 0)
            {
                if (jobs.Length > 0) Save();
                CheckSchedule();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Autosave can collide with antivirus/backup locks; the next tick retries.
        }
        finally
        {
            Volatile.Write(ref _inTick, 0);
        }
    }

    private void CheckSchedule()
    {
        var s = Settings;
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);

        if (s.ScheduleStartEnabled && IsDue(s.ScheduleStartTime, now) && _lastScheduledStart != today)
        {
            _lastScheduledStart = today;
            StartQueue();
        }
        if (s.ScheduleStopEnabled && IsDue(s.ScheduleStopTime, now) && _lastScheduledStop != today)
        {
            _lastScheduledStop = today;
            StopQueue();
        }
    }

    private static bool IsDue(string time, DateTime now) =>
        TimeOnly.TryParse(time, out var t)
        && now.TimeOfDay >= t.ToTimeSpan()
        && now.TimeOfDay < t.ToTimeSpan() + TimeSpan.FromMinutes(2);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        IDownloadJob[] jobs;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            jobs = _running.Values.ToArray();
        }
        _timer.Dispose();
        _shutdown.Cancel();
        foreach (var j in jobs) j.Cancel();
        try { Task.WaitAll(jobs.Select(j => j.Completion).ToArray(), TimeSpan.FromSeconds(8)); } catch { }
        Torrents.Dispose();
        try { Save(); } catch (IOException) { }
    }
}
