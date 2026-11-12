using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using VeloDM.Core;

namespace VeloDM.App;

public partial class ProgressWindow : Window
{
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
        FilesList.Visibility = torrent ? Visibility.Visible : Visibility.Collapsed;
        ConnectionList.Visibility = torrent ? Visibility.Collapsed : Visibility.Visible;
        PeersLabel.Visibility = PeersValue.Visibility = torrent ? Visibility.Visible : Visibility.Collapsed;

        if (torrent)
        {
            FilesList.ItemsSource = _item.TorrentFiles.Select(f => new
            {
                f.Path,
                SizeText = Format.Size(f.Length),
                PercentText = $"{f.Percent:0.0}%",
                f.Priority,
            }).ToList();
        }
        else
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
