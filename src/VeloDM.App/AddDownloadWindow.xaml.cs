using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using MonoTorrent;
using VeloDM.Core;

namespace VeloDM.App;

/// <summary>One file of a torrent in the Add dialog's checklist.</summary>
public sealed class TorrentFileChoice
{
    public required string Path { get; init; }
    public required long Length { get; init; }
    public string SizeText => Format.Size(Length);
    public bool Wanted { get; set; } = true;
}

public partial class AddDownloadWindow : Window
{
    private static readonly int[] ConnectionChoices = [1, 2, 4, 8, 12, 16, 24, 32];

    private readonly DownloadManager _manager;
    private readonly BridgeRequest? _request;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _probeCts;
    private RemoteFileInfo? _info;
    private bool _suppress;
    private bool _nameEdited, _folderEdited, _categoryEdited;

    // Torrent mode
    private bool _isTorrent;
    private string? _torrentHash;
    private long _torrentSize = -1;
    private byte[]? _torrentBytes;
    private System.Collections.Generic.List<TorrentFileChoice> _torrentFiles = new();

    // Video mode: the address is a page with a streamed video, handed to yt-dlp.
    private bool _isVideo;
    private bool _videoChosenByUser;

    public DownloadItem? Result { get; private set; }
    public bool StartNow { get; private set; }

    public AddDownloadWindow(DownloadManager manager, BridgeRequest? request, bool video = false)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _manager = manager;
        _request = request;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = ProbeAsync(); };

        _suppress = true;
        CategoryBox.ItemsSource = Categories.All.Select(c => c.Name).ToList();
        CategoryBox.SelectedItem = Categories.General;
        ConnectionsBox.ItemsSource = ConnectionChoices;
        ConnectionsBox.SelectedItem = ConnectionChoices.Contains(manager.Settings.DefaultConnections)
            ? manager.Settings.DefaultConnections : 8;
        FolderBox.Text = Categories.FolderFor(manager.Settings, Categories.General);
        if (!string.IsNullOrWhiteSpace(request?.FileName))
        {
            SetName(FileNameHelper.Sanitize(request.FileName));
            _nameEdited = true;
        }
        else if (video && !string.IsNullOrWhiteSpace(request?.Title))
        {
            SetName(FileNameHelper.Sanitize(request.Title) + ".mp4");
            _nameEdited = true;
        }
        UrlBox.Text = request?.Url ?? "";
        _suppress = false;
        _videoChosenByUser = video;
        SetVideoMode(video);
        UpdateMode();

        InfoText.Text = UrlBox.Text.Length > 0 ? "Fetching file information…"
            : video ? "Paste the address of the page the video is on."
            : "Paste a download address or magnet link.";
        Loaded += (_, _) =>
        {
            if (UrlBox.Text.Length > 0) _ = ProbeAsync();
            (UrlBox.Text.Length == 0 ? UrlBox : NameBox).Focus();
            if (Topmost) { Activate(); Topmost = false; }
        };
        Closed += (_, _) => _probeCts?.Cancel();
    }

    private string Source => UrlBox.Text.Trim();

    private void UpdateMode()
    {
        bool torrent = TorrentSources.IsTorrent(Source);
        if (torrent != _isTorrent)
        {
            _isTorrent = torrent;
            _torrentHash = null;
            _torrentBytes = null;
            _torrentSize = -1;
            if (!_categoryEdited)
            {
                _suppress = true;
                CategoryBox.SelectedItem = torrent ? Categories.Torrents : Categories.ForFileName(NameBox.Text);
                if (!_folderEdited) FolderBox.Text = Categories.FolderFor(_manager.Settings, (string)CategoryBox.SelectedItem);
                _suppress = false;
            }
        }
        if (_isTorrent && _isVideo) SetVideoMode(false);
        var visibility = _isTorrent || _isVideo ? Visibility.Collapsed : Visibility.Visible;
        ConnectionsLabel.Visibility = ConnectionsBox.Visibility = visibility;
        VideoBox.Visibility = _isTorrent ? Visibility.Collapsed : Visibility.Visible;
        if (!_isTorrent) ShowTorrentFiles(null);
        // A torrent's file/folder name comes from its metadata; MonoTorrent saves it under that name.
        NameBox.IsReadOnly = _isTorrent;
        Title = _isTorrent ? "Add Torrent" : _isVideo ? "Download Video" : "Download File Info";
    }

    private void SetVideoMode(bool video)
    {
        _isVideo = video;
        VideoBox.IsChecked = video;
        if (!video || _categoryEdited) return;
        _suppress = true;
        CategoryBox.SelectedItem = "Video";
        if (!_folderEdited) FolderBox.Text = Categories.FolderFor(_manager.Settings, "Video");
        _suppress = false;
    }

    private void VideoBox_Click(object sender, RoutedEventArgs e)
    {
        _videoChosenByUser = true;
        SetVideoMode(VideoBox.IsChecked == true);
        if (_isVideo && !_nameEdited) SetName("");
        UpdateMode();
        _ = ProbeAsync();
    }

    private void ShowTorrentFiles(Torrent? torrent)
    {
        _torrentFiles = torrent is null
            ? new()
            : torrent.Files.Select(f => new TorrentFileChoice { Path = f.Path, Length = f.Length }).ToList();
        TorrentFilesList.ItemsSource = _torrentFiles;
        // A single-file torrent has nothing to choose between.
        TorrentFilesPanel.Visibility = _torrentFiles.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TorrentFile_Click(object sender, RoutedEventArgs e)
    {
        long wanted = _torrentFiles.Where(f => f.Wanted).Sum(f => f.Length);
        int count = _torrentFiles.Count(f => f.Wanted);
        InfoText.Text = $"BitTorrent     Selected: {count} of {_torrentFiles.Count} files, {Format.Size(wanted)}";
    }

    private void SetName(string name)
    {
        bool old = _suppress;
        _suppress = true;
        NameBox.Text = name;
        if (!_categoryEdited && !_isTorrent)
        {
            CategoryBox.SelectedItem = Categories.ForFileName(name);
            if (!_folderEdited) FolderBox.Text = Categories.FolderFor(_manager.Settings, (string)CategoryBox.SelectedItem);
        }
        _suppress = old;
    }

    private async Task ProbeAsync()
    {
        _probeCts?.Cancel();
        var cts = _probeCts = new CancellationTokenSource();
        var source = Source;
        _info = null;

        if (TorrentSources.IsTorrent(source))
        {
            await ProbeTorrentAsync(source, cts.Token);
            return;
        }
        if (!App.IsHttpUrl(source))
        {
            InfoText.Text = _isVideo ? "Paste the address of the page the video is on."
                : source.Length == 0 ? "Paste a download address or magnet link."
                : "Enter an http(s) address, a magnet link or the path of a .torrent file.";
            return;
        }
        if (_isVideo)
        {
            await ProbeVideoAsync(source, cts.Token);
            return;
        }

        InfoText.Text = "Fetching file information…";
        try
        {
            var info = await HttpEngine.ProbeAsync(source, _request?.Referrer, _request?.Cookies,
                _request?.UserAgent ?? _manager.Settings.UserAgent, cts.Token);
            if (cts.IsCancellationRequested) return;
            _info = info;
            // A web page or a stream playlist is not the file the user is after: offer the video on it instead.
            bool page = info.ContentType is { } type
                        && (type.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                            || type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                            || type.Equals("application/dash+xml", StringComparison.OrdinalIgnoreCase));
            if (page && !_videoChosenByUser)
            {
                SetVideoMode(true);
                if (!_nameEdited) SetName("");
                UpdateMode();
                await ProbeVideoAsync(source, cts.Token);
                return;
            }
            if (!_nameEdited) SetName(info.FileName);
            InfoText.Text = $"Size: {(info.Size >= 0 ? Format.Size(info.Size) : "Unknown")}     " +
                            $"Resume / multi-connection: {(info.AcceptRanges ? "Yes" : "No")}" +
                            (info.ContentType is { } ct ? $"     Type: {ct}" : "");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                InfoText.Text = "Could not get file information: " + ex.Message;
        }
    }

    private async Task ProbeVideoAsync(string source, CancellationToken ct)
    {
        const string nameHint = "\nLeave Save as empty to name the file after the video's title.";
        if (_manager.LocateVideoTools() is not { } tools)
        {
            InfoText.Text = "VeloDM will read this page and download the video on it (the video tools are downloaded first)." + nameHint;
            return;
        }
        InfoText.Text = "Reading the video page…";
        try
        {
            var info = await VideoProbe.GetAsync(tools, _manager.Settings, source, _request?.Cookies, ct);
            if (ct.IsCancellationRequested) return;
            if (!_nameEdited) SetName(FileNameHelper.Sanitize(info.Title) + ".mp4");
            int height = _manager.Settings.StreamMaxHeight;
            InfoText.Text = $"Video: {info.Title}" +
                            // Under a minute the tool gives bare seconds ("19"); longer ones read as m:ss.
                            (info.Duration is null ? "" : $"     Length: {info.Duration}{(info.Duration.Contains(':') ? "" : " seconds")}") +
                            $"     Quality: {(height > 0 ? $"up to {height}p" : "best available")}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            if (!ct.IsCancellationRequested)
                InfoText.Text = "The video tools could not read this page yet: " + ex.Message + "\nYou can still start the download." + nameHint;
        }
    }

    private async Task ProbeTorrentAsync(string source, CancellationToken ct)
    {
        try
        {
            if (TorrentSources.IsMagnet(source))
            {
                var magnet = MagnetLink.Parse(source);
                _torrentHash = TorrentSources.HashOf(magnet.InfoHashes);
                _torrentSize = magnet.Size ?? -1;
                _torrentBytes = null;
                ShowTorrentFiles(null);
                SetName(TorrentSources.NameFor(magnet));
                InfoText.Text = $"BitTorrent magnet link     Hash: {_torrentHash[..12]}…" +
                                (magnet.Size is long size ? $"     Size: {Format.Size(size)}" : "") +
                                "\nThe file list is fetched from peers when the download starts; you can untick files in the progress window then.";
                return;
            }

            InfoText.Text = "Reading torrent…";
            var bytes = await TorrentSources.ReadTorrentBytesAsync(source, _request?.Referrer, _request?.Cookies,
                _request?.UserAgent ?? _manager.Settings.UserAgent, ct);
            if (ct.IsCancellationRequested) return;
            var torrent = TorrentSources.Parse(bytes);
            _torrentBytes = bytes;
            _torrentHash = TorrentSources.HashOf(torrent.InfoHashes);
            _torrentSize = torrent.Size;
            ShowTorrentFiles(torrent);
            SetName(torrent.Name);
            InfoText.Text = $"BitTorrent     Size: {Format.Size(torrent.Size)}     Files: {torrent.Files.Count}" +
                            (torrent.IsPrivate ? "     Private tracker" : "") +
                            $"\nHash: {_torrentHash}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _torrentHash = null;
            InfoText.Text = "Could not read the torrent: " + ex.Message;
        }
    }

    private void UrlBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppress) return;
        UpdateMode();
        if (!_nameEdited && !_isTorrent && !_isVideo && App.IsHttpUrl(Source)) SetName(FileNameHelper.FromUrl(new Uri(Source)));
        _debounce.Stop();
        _debounce.Start();
    }

    private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppress || _isTorrent) return;
        _nameEdited = true;
        if (!_categoryEdited && !_isVideo)
        {
            _suppress = true;
            CategoryBox.SelectedItem = Categories.ForFileName(NameBox.Text);
            if (!_folderEdited) FolderBox.Text = Categories.FolderFor(_manager.Settings, (string)CategoryBox.SelectedItem);
            _suppress = false;
        }
    }

    private void CategoryBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppress || CategoryBox.SelectedItem is not string category) return;
        _categoryEdited = true;
        if (!_folderEdited)
        {
            _suppress = true;
            FolderBox.Text = Categories.FolderFor(_manager.Settings, category);
            _suppress = false;
        }
    }

    private void FolderBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_suppress) _folderEdited = true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Save downloads to" };
        if (Directory.Exists(FolderBox.Text)) dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private void Start_Click(object sender, RoutedEventArgs e) => Accept(startNow: true);
    private void Later_Click(object sender, RoutedEventArgs e) => Accept(startNow: false);

    private void Accept(bool startNow)
    {
        var source = Source;
        if (!App.IsDownloadSource(source))
        {
            Warn("Please enter an http(s) address, a magnet link or the path of a .torrent file.");
            UrlBox.Focus();
            return;
        }
        var folder = FolderBox.Text.Trim();
        try
        {
            if (!Path.IsPathFullyQualified(folder)) throw new IOException("Choose an absolute folder path.");
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            Warn("Cannot use this folder: " + ex.Message);
            FolderBox.Focus();
            return;
        }

        Result = _isTorrent ? BuildTorrentItem(source, folder)
            : _isVideo ? BuildStreamItem(source, folder)
            : BuildHttpItem(source, folder);
        if (Result is null) return;
        StartNow = startNow;
        DialogResult = true;
    }

    private DownloadItem BuildHttpItem(string url, string folder)
    {
        var name = FileNameHelper.MakeUnique(folder, FileNameHelper.Sanitize(NameBox.Text));
        return new DownloadItem
        {
            Url = url,
            FileName = name,
            SaveDirectory = folder,
            Category = CategoryBox.SelectedItem as string ?? Categories.ForFileName(name),
            MaxConnections = ConnectionsBox.SelectedItem is int n ? n : _manager.Settings.DefaultConnections,
            Referrer = _request?.Referrer,
            Cookies = _request?.Cookies,
            UserAgent = _request?.UserAgent,
            TotalSize = _info?.Size ?? -1,
            ResumeSupported = _info?.AcceptRanges,
        };
    }

    private DownloadItem? BuildStreamItem(string url, string folder)
    {
        if (!App.IsHttpUrl(url))
        {
            Warn("A video needs the http(s) address of the page it is on.");
            return null;
        }
        // An empty name is filled in from the video's title once its page has been read.
        var name = NameBox.Text.Trim();
        if (name.Length > 0)
        {
            name = FileNameHelper.Sanitize(name);
            if (Path.GetExtension(name).Length == 0) name += ".mp4";
            name = FileNameHelper.MakeUnique(folder, name);
        }
        return new DownloadItem
        {
            Kind = DownloadKind.Stream,
            Url = url,
            ManifestUrl = _request?.ManifestUrl,
            FileName = name,
            SaveDirectory = folder,
            Category = CategoryBox.SelectedItem as string ?? "Video",
            Referrer = _request?.Referrer,
            Cookies = _request?.Cookies,
            UserAgent = _request?.UserAgent,
            ResumeSupported = true,
        };
    }

    private DownloadItem? BuildTorrentItem(string source, string folder)
    {
        if (_torrentHash is null)
        {
            Warn("The torrent could not be read yet. Check the address or file and try again.");
            return null;
        }
        if (_manager.FindByInfoHash(_torrentHash) is { } existing)
        {
            Warn($"This torrent is already in the list as \"{existing.FileName}\" ({existing.StatusText}).");
            return null;
        }
        var skipped = _torrentFiles.Where(f => !f.Wanted).ToList();
        if (_torrentFiles.Count > 0 && skipped.Count == _torrentFiles.Count)
        {
            Warn("Tick at least one file to download.");
            return null;
        }

        var item = new DownloadItem
        {
            Kind = DownloadKind.Torrent,
            // Store the absolute path for local files so a later resume still finds it.
            Url = TorrentSources.IsTorrentFile(source) ? Path.GetFullPath(source) : source,
            FileName = NameBox.Text,
            SaveDirectory = folder,
            Category = CategoryBox.SelectedItem as string ?? Categories.Torrents,
            InfoHash = _torrentHash,
            SkippedFiles = skipped.Select(f => f.Path).ToList(),
            TotalSize = skipped.Count > 0 ? _torrentFiles.Where(f => f.Wanted).Sum(f => f.Length) : _torrentSize,
            ResumeSupported = true,
            Referrer = _request?.Referrer,
            Cookies = _request?.Cookies,
            UserAgent = _request?.UserAgent,
        };
        if (_torrentBytes is not null) _manager.Torrents.StoreTorrentFile(item, _torrentBytes);
        return item;
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
