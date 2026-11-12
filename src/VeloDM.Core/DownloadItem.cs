using System.Text.Json.Serialization;

namespace VeloDM.Core;

public enum DownloadStatus { Queued, Connecting, Downloading, Paused, Completed, Error }

public enum DownloadKind { Http, Torrent }

public sealed record TorrentFileState(string Path, long Length, double Percent, string Priority);

public sealed class DownloadItem : ObservableObject
{
    private string _url = "";
    private string _fileName = "";
    private string _saveDirectory = "";
    private string _category = Categories.General;
    private long _totalSize = -1;
    private bool? _resumeSupported;
    private DownloadStatus _status = DownloadStatus.Paused;
    private string? _errorMessage;
    private DateTime? _dateCompleted;
    private bool _inQueue;
    private int _maxConnections = 8;
    private double _speed;
    private TimeSpan? _eta;
    private int _activeConnections;
    private string? _phase;

    public Guid Id { get; set; } = Guid.NewGuid();
    public DownloadKind Kind { get; set; } = DownloadKind.Http;

    /// <summary>Torrents only: lower-case hex info-hash, set once known.</summary>
    public string? InfoHash { get; set; }

    /// <summary>Torrents only: verified bytes on disk (HTTP downloads derive progress from segments).</summary>
    public long TorrentBytes { get; set; }
    public string Url { get => _url; set => SetField(ref _url, value); }
    public string? Referrer { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }

    public string FileName
    {
        get => _fileName;
        set { if (SetField(ref _fileName, value)) Notify(nameof(FullPath)); }
    }

    public string SaveDirectory
    {
        get => _saveDirectory;
        set { if (SetField(ref _saveDirectory, value)) Notify(nameof(FullPath)); }
    }

    public string Category { get => _category; set => SetField(ref _category, value); }

    public long TotalSize
    {
        get => _totalSize;
        set { if (SetField(ref _totalSize, value)) Notify(nameof(SizeText), nameof(Progress), nameof(ProgressText)); }
    }

    public bool? ResumeSupported
    {
        get => _resumeSupported;
        set { if (SetField(ref _resumeSupported, value)) Notify(nameof(ResumeText)); }
    }

    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (!SetField(ref _status, value)) return;
            if (value == DownloadStatus.Completed) DateCompleted = DateTime.Now;
            Notify(nameof(StatusText), nameof(IsActive), nameof(IsCompleted), nameof(Progress), nameof(ProgressText),
                   nameof(SpeedText), nameof(EtaText));
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set { if (SetField(ref _errorMessage, value)) Notify(nameof(StatusText)); }
    }

    public DateTime DateAdded { get; set; } = DateTime.Now;
    public DateTime? DateCompleted { get => _dateCompleted; set => SetField(ref _dateCompleted, value); }
    public bool InQueue { get => _inQueue; set => SetField(ref _inQueue, value); }
    public int MaxConnections { get => _maxConnections; set => SetField(ref _maxConnections, value); }

    public List<Segment> Segments { get; set; } = new();

    // ---- runtime-only state -------------------------------------------------

    [JsonIgnore] public object SegmentsLock { get; } = new();
    [JsonIgnore] public double Speed { get => _speed; set => _speed = value; }
    [JsonIgnore] public TimeSpan? Eta { get => _eta; set => _eta = value; }
    [JsonIgnore] public int ActiveConnections { get => _activeConnections; set => _activeConnections = value; }

    // Torrent live state, refreshed by the torrent job.
    [JsonIgnore] public bool IsTorrent => Kind == DownloadKind.Torrent;
    [JsonIgnore] public double UploadSpeed { get; set; }
    [JsonIgnore] public int Seeds { get; set; }
    [JsonIgnore] public int Peers { get; set; }
    [JsonIgnore] public double[]? PieceMap { get; set; }
    [JsonIgnore] public IReadOnlyList<TorrentFileState> TorrentFiles { get; set; } = [];

    /// <summary>Optional detail ("Fetching metadata…", "Seeding") shown instead of the plain status.</summary>
    [JsonIgnore]
    public string? Phase
    {
        get => _phase;
        set { if (SetField(ref _phase, value)) Notify(nameof(StatusText)); }
    }

    [JsonIgnore]
    public string ConnectionsText => IsTorrent
        ? (IsActive || Phase is not null ? $"{Seeds} / {Peers}" : "")
        : (ActiveConnections > 0 ? ActiveConnections.ToString() : "");

    [JsonIgnore] public string FullPath => Path.Combine(SaveDirectory, FileName);
    [JsonIgnore] public string TempPath => FullPath + ".vdmpart";
    [JsonIgnore] public bool IsActive => Status is DownloadStatus.Connecting or DownloadStatus.Downloading;
    [JsonIgnore] public bool IsCompleted => Status == DownloadStatus.Completed;

    [JsonIgnore]
    public long Downloaded
    {
        get
        {
            if (IsTorrent) return TorrentBytes;
            lock (SegmentsLock)
            {
                long sum = 0;
                foreach (var s in Segments) sum += s.Downloaded;
                return sum;
            }
        }
    }

    [JsonIgnore]
    public double Progress => TotalSize > 0
        ? Math.Min(100.0, Downloaded * 100.0 / TotalSize)
        : IsCompleted ? 100.0 : 0.0;

    [JsonIgnore]
    public string ProgressText => TotalSize > 0 ? $"{Progress:0.0}%"
        : IsCompleted ? "100%"
        : Downloaded > 0 ? Format.Size(Downloaded) : "";

    [JsonIgnore] public string SizeText => TotalSize >= 0 ? Format.Size(TotalSize) : "Unknown";

    [JsonIgnore]
    public string DownloadedText => TotalSize > 0
        ? $"{Format.Size(Downloaded)} ({Progress:0.00}%)"
        : Format.Size(Downloaded);

    [JsonIgnore]
    public string SpeedText
    {
        get
        {
            var down = IsActive && Speed > 0 ? Format.Speed(Speed) : "";
            if (!IsTorrent || UploadSpeed <= 0) return down;
            var up = "↑ " + Format.Speed(UploadSpeed);
            return down.Length > 0 ? $"{down}  {up}" : up;
        }
    }

    [JsonIgnore] public string EtaText => IsActive && Eta is { } e ? Format.Duration(e) : "";

    [JsonIgnore]
    public string ResumeText => IsTorrent ? "Yes (BitTorrent)"
        : ResumeSupported switch { true => "Yes", false => "No", _ => "Unknown" };

    [JsonIgnore]
    public string StatusText => Phase is { } phase && (IsActive || IsCompleted) ? phase : Status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Connecting => "Connecting…",
        DownloadStatus.Downloading => string.IsNullOrEmpty(ProgressText) ? "Receiving data…" : ProgressText,
        DownloadStatus.Paused => Downloaded > 0 ? "Paused" : "Stopped",
        DownloadStatus.Completed => "Complete",
        DownloadStatus.Error => "Error: " + ErrorMessage,
        _ => Status.ToString()
    };

    /// <summary>Raises change notifications for all progress-related properties.</summary>
    public void NotifyProgress() =>
        Notify(nameof(Downloaded), nameof(Progress), nameof(ProgressText), nameof(DownloadedText), nameof(SpeedText),
               nameof(EtaText), nameof(StatusText), nameof(ActiveConnections), nameof(Speed), nameof(SizeText),
               nameof(ConnectionsText));

    public SegmentSnapshot[] SnapshotSegments()
    {
        lock (SegmentsLock)
        {
            var result = new SegmentSnapshot[Segments.Count];
            for (int i = 0; i < Segments.Count; i++)
            {
                var s = Segments[i];
                result[i] = new SegmentSnapshot(i + 1, s.Start, s.End, s.Downloaded, s.Active, s.IsComplete, s.State);
            }
            return result;
        }
    }

    /// <summary>Discards all progress so the next start downloads from scratch.</summary>
    public void ResetProgress()
    {
        lock (SegmentsLock) Segments.Clear();
        TorrentBytes = 0;
        PieceMap = null;
        ErrorMessage = null;
        NotifyProgress();
    }
}
