namespace VeloDM.Core;

/// <summary>A running download of any kind (HTTP or BitTorrent).</summary>
internal interface IDownloadJob
{
    DownloadItem Item { get; }
    Task Completion { get; }

    /// <summary>True once the job has already raised the completion notification (torrents that keep seeding).</summary>
    bool CompletionReported { get; }

    void Start();
    void Cancel();

    /// <summary>Called periodically to refresh <see cref="DownloadItem.Speed"/> and <see cref="DownloadItem.Eta"/>.</summary>
    void Sample(long timestamp);
}
