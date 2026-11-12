using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using VeloDM.Core;

namespace VeloDM.App;

/// <summary>One file of a torrent in the progress window's list.</summary>
public sealed class TorrentFileRow : ObservableObject
{
    private bool _wanted = true;
    private string _percentText = "";

    public required string Path { get; init; }
    public required string SizeText { get; init; }
    public bool Wanted { get => _wanted; set => SetField(ref _wanted, value); }
    public string PercentText { get => _percentText; set => SetField(ref _percentText, value); }
}

public partial class ProgressWindow : Window
{
    private readonly System.Collections.ObjectModel.ObservableCollection<TorrentFileRow> _files = new();
    private bool _changingFiles;

    private readonly DownloadManager _manager;
    private readonly DownloadItem _item;
    private readonly DispatcherTimer _timer;

    public ProgressWindow(DownloadManager manager, DownloadItem item)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _manager = manager;
        _item = item;
        DataContext = item;
        SegBar.Item = item;
        FilesList.ItemsSource = _files;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Refresh();
    }

    public void Refresh()
    {
        Title = string.IsNullOrEmpty(_item.ProgressText) ? _item.FileName : $"{_item.ProgressText} {_item.FileName}";
        SegBar.InvalidateVisual();
        bool torrent = _item.IsTorrent;
        bool http = _item.Kind == DownloadKind.Http;
        bool hasFiles = torrent && _item.TorrentFiles.Count > 0;
        FilesList.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        ConnectionList.Visibility = http ? Visibility.Visible : Visibility.Collapsed;
        PeersLabel.Visibility = PeersValue.Visibility = torrent ? Visibility.Visible : Visibility.Collapsed;
        DetailNote.Visibility = http || hasFiles ? Visibility.Collapsed : Visibility.Visible;
        DetailNote.Text = torrent
            ? "The list of files appears here once the torrent has started; untick a file to skip it."
            : "Streamed video is downloaded by yt-dlp. Picture and sound usually arrive as two separate downloads, " +
              "which FFmpeg then joins into one file.";

        if (torrent)
        {
            if (!_changingFiles) RefreshFiles();
        }
        else if (http)
        {
            ConnectionList.ItemsSource = _item.SnapshotSegments().Select(s => new
            {
                s.Index,
                StartText = Format.Size(s.Start),
                EndText = s.End >= 0 ? Format.Size(s.End + 1) : "Unknown",
                DownloadedText = Format.Size(s.Downloaded),
                State = s.Complete ? "Completed" : string.IsNullOrEmpty(s.State) ? "Waiting" : s.State,
            }).ToList();
        }

        bool running = _manager.IsRunning(_item);
        bool seeding = running && _item.IsCompleted;
        PauseButton.Content = seeding ? "_Stop seeding" : running ? "_Pause" : "_Resume";
        PauseButton.Visibility = _item.IsCompleted && !seeding ? Visibility.Collapsed : Visibility.Visible;
        OpenButton.Visibility = _item.IsCompleted ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshFiles()
    {
        var files = _item.TorrentFiles;
        if (_files.Count != files.Count || !_files.Select(r => r.Path).SequenceEqual(files.Select(f => f.Path)))
        {
            _files.Clear();
            foreach (var f in files) _files.Add(new TorrentFileRow { Path = f.Path, SizeText = Format.Size(f.Length) });
        }
        for (int i = 0; i < files.Count; i++)
        {
            _files[i].PercentText = $"{files[i].Percent:0.0}%";
            _files[i].Wanted = !_item.SkippedFiles.Contains(files[i].Path, StringComparer.OrdinalIgnoreCase);
        }
    }

    private async void FileWanted_Click(object sender, RoutedEventArgs e)
    {
        var skipped = _files.Where(r => !r.Wanted).Select(r => r.Path).ToList();
        if (skipped.Count == _files.Count)
        {
            MessageBox.Show(this, "At least one file has to stay selected. To stop the torrent, pause or delete it.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshFiles();
            return;
        }
        _changingFiles = true;
        try
        {
            await _manager.SetSkippedFilesAsync(_item, skipped);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not change the file selection: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _changingFiles = false;
            RefreshFiles();
        }
    }

    private void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        if (_manager.IsRunning(_item)) _manager.Pause(_item);
        else _manager.Start(_item);
        Refresh();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_item.FullPath) || Directory.Exists(_item.FullPath))
            Process.Start(new ProcessStartInfo(_item.FullPath) { UseShellExecute = true });
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => MainWindow.OpenFolder(_item);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
