using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using VeloDM.Core;

namespace VeloDM.App;

public sealed record SidebarEntry(string Name, string Glyph, Func<DownloadItem, bool> Filter, bool Child = false)
{
    public Thickness Indent => Child ? new Thickness(14, 0, 0, 0) : new Thickness(0);
}

public partial class MainWindow : Window
{
    private readonly DownloadManager _manager = App.Manager;
    private readonly ListCollectionView _view;
    private readonly Dictionary<Guid, ProgressWindow> _progressWindows = new();
    private ClipboardMonitor? _clipboard;
    private bool _closingForExit;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_manager.Items);
        _view.IsLiveFiltering = true;
        foreach (var p in new[] { nameof(DownloadItem.Status), nameof(DownloadItem.Category), nameof(DownloadItem.InQueue) })
            _view.LiveFilteringProperties.Add(p);
        _view.SortDescriptions.Add(new SortDescription(nameof(DownloadItem.DateAdded), ListSortDirection.Descending));
        DownloadGrid.ItemsSource = _view;
        ((System.Collections.Specialized.INotifyCollectionChanged)_view).CollectionChanged += (_, _) => UpdateEmptyState();

        var entries = new List<SidebarEntry>
        {
            new("All Downloads", "", _ => true),
        };
        entries.AddRange(Categories.All.Select(c =>
            new SidebarEntry(c.Name, c.Glyph, i => i.Category == c.Name, Child: true)));
        entries.Add(new("Unfinished", "", i => !i.IsCompleted));
        entries.Add(new("Finished", "", i => i.IsCompleted));
        entries.Add(new("Queue", "", i => i.InQueue));
        CategoryList.ItemsSource = entries;
        CategoryList.SelectedIndex = 0;

        _manager.Ticked += () => Dispatcher.BeginInvoke(UpdateStatusBar);
        _manager.QueueStateChanged += () => Dispatcher.BeginInvoke(UpdateStatusBar);
        UpdateStatusBar();
        SpeedLimitMenu.IsChecked = _manager.Settings.SpeedLimitEnabled;

        SourceInitialized += (_, _) =>
        {
            _clipboard = new ClipboardMonitor(this);
            _clipboard.TextCopied += OnClipboardText;
        };
    }

    private void UpdateEmptyState() => EmptyState.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

    private List<DownloadItem> Selected => DownloadGrid.SelectedItems.Cast<DownloadItem>().ToList();

    private void UpdateStatusBar()
    {
        int active = _manager.ActiveCount;
        var limit = _manager.Settings.SpeedLimitEnabled ? $"   ·   Limit {Format.Speed(_manager.Settings.SpeedLimitBytes)}" : "";
        StatusLine.Text = active == 0
            ? $"{_manager.Items.Count} downloads   ·   Idle{limit}"
            : $"{active} active   ·   {Format.Speed(_manager.TotalSpeed)}{limit}";
        QueueStatus.Text = _manager.QueueRunning ? "Queue: running" : "Queue: stopped";
    }

    // ---- adding downloads ------------------------------------------------------------------

    public void ShowAddDialog(BridgeRequest? request)
    {
        if (request is null && TryGetClipboardUrl() is { } clip)
            request = new BridgeRequest(clip);

        if (request?.Url is { } url && _manager.FindByUrl(url) is { } existing)
        {
            var answer = MessageBox.Show(VisibleOwner,
                $"This address is already in the list as \"{existing.FileName}\" ({existing.StatusText}).\n\nAdd it again?",
                "Duplicate download", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        var dialog = new AddDownloadWindow(_manager, request);
        if (IsVisible) dialog.Owner = this;
        else dialog.Topmost = true;
        if (dialog.ShowDialog() != true || dialog.Result is not { } item) return;

        if (!dialog.StartNow) item.InQueue = true;
        _manager.Add(item, dialog.StartNow);
        if (dialog.StartNow && _manager.Settings.ShowProgressWindow) OpenProgress(item);
    }

    public void HandleExternalRequest(BridgeRequest request)
    {
        // Torrents always go through the dialog: it reads the metadata and checks for duplicates.
        if (_manager.Settings.ShowDialogForBrowserDownloads || TorrentSources.IsTorrent(request.Url))
        {
            ShowAddDialog(request);
            return;
        }
        var name = !string.IsNullOrWhiteSpace(request.FileName)
            ? FileNameHelper.Sanitize(request.FileName)
            : FileNameHelper.FromUrl(new Uri(request.Url!));
        var category = Categories.ForFileName(name);
        var item = new DownloadItem
        {
            Url = request.Url!,
            Referrer = request.Referrer,
            Cookies = request.Cookies,
            UserAgent = request.UserAgent,
            Category = category,
            SaveDirectory = Categories.FolderFor(_manager.Settings, category),
            MaxConnections = _manager.Settings.DefaultConnections,
        };
        item.FileName = FileNameHelper.MakeUnique(item.SaveDirectory, name);
        _manager.Add(item, startNow: true);
        if (_manager.Settings.ShowProgressWindow) OpenProgress(item);
        else App.Current.Balloon("Download started", item.FileName);
    }

    private void OnClipboardText(string text)
    {
        if (!_manager.Settings.MonitorClipboard || _manager.FindByUrl(text) is not null) return;
        bool wanted = TorrentSources.IsMagnet(text)
                      || (App.IsHttpUrl(text) && _manager.Settings.IsCapturedExtension(new Uri(text).AbsolutePath));
        if (wanted) ShowAddDialog(new BridgeRequest(text));
    }

    private static string? TryGetClipboardUrl()
    {
        try
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : null;
            return App.IsHttpUrl(text) || TorrentSources.IsMagnet(text) ? text : null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private Window VisibleOwner => IsVisible ? this : new Window { Topmost = true, ShowInTaskbar = false };

    // ---- progress windows -------------------------------------------------------------------

    private void OpenProgress(DownloadItem item)
    {
        if (_progressWindows.TryGetValue(item.Id, out var existing))
        {
            existing.Show();
            existing.Activate();
            return;
        }
        var window = new ProgressWindow(_manager, item);
        _progressWindows[item.Id] = window;
        window.Closed += (_, _) => _progressWindows.Remove(item.Id);
        window.Show();
    }

    public void OnDownloadCompleted(DownloadItem item)
    {
        if (_progressWindows.TryGetValue(item.Id, out var window)) window.Refresh();
    }

    // ---- commands ---------------------------------------------------------------------

    private void AddUrl_Click(object sender, RoutedEventArgs e) => ShowAddDialog(null);

    private void AddTorrent_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open torrent file",
            Filter = "Torrent files (*.torrent)|*.torrent|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FileNames) ShowAddDialog(new BridgeRequest(path));
    }

    private void AddBatch_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BatchWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        foreach (var url in dialog.Urls)
        {
            var name = FileNameHelper.FromUrl(new Uri(url));
            var category = Categories.ForFileName(name);
            var item = new DownloadItem
            {
                Url = url,
                Category = category,
                SaveDirectory = Categories.FolderFor(_manager.Settings, category),
                MaxConnections = _manager.Settings.DefaultConnections,
                InQueue = true,
            };
            item.FileName = FileNameHelper.MakeUnique(item.SaveDirectory, name);
            _manager.Add(item, startNow: false);
        }
        if (dialog.StartQueue) _manager.StartQueue();
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected.Where(i => !i.IsCompleted)) _manager.Start(item);
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected) _manager.Pause(item);
    }

    private void StopAll_Click(object sender, RoutedEventArgs e) => _manager.StopAll();

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var items = Selected;
        if (items.Count == 0) return;
        var answer = MessageBox.Show(this,
            $"Remove {items.Count} download(s) from the list?\n\n" +
            "Yes — also delete downloaded files from disk\nNo — remove from the list only",
            "Delete downloads", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Cancel) return;
        foreach (var i in items)
            if (_progressWindows.TryGetValue(i.Id, out var w)) w.Close();
        await _manager.RemoveAsync(items, deleteFiles: answer == MessageBoxResult.Yes);
        UpdateEmptyState();
    }

    private async void DeleteCompleted_Click(object sender, RoutedEventArgs e)
    {
        var done = _manager.Items.Where(i => i.IsCompleted).ToList();
        if (done.Count == 0) return;
        await _manager.RemoveAsync(done, deleteFiles: false);
        UpdateEmptyState();
    }

    private async void Redownload_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected) await _manager.RedownloadAsync(item);
    }

    private void AddToQueue_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected) _manager.SetInQueue(item, true);
    }

    private void RemoveFromQueue_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected) _manager.SetInQueue(item, false);
    }

    private void StartQueue_Click(object sender, RoutedEventArgs e) => _manager.StartQueue();
    private void StopQueue_Click(object sender, RoutedEventArgs e) => _manager.StopQueue();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is { } item) OpenItem(item);
    }

    private void OpenItem(DownloadItem item)
    {
        if (!item.IsCompleted)
        {
            OpenProgress(item);
            return;
        }
        if (!File.Exists(item.FullPath) && !Directory.Exists(item.FullPath))
        {
            MessageBox.Show(this, $"The file no longer exists:\n{item.FullPath}", "VeloDM", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is { } item) OpenFolder(item);
    }

    public static void OpenFolder(DownloadItem item)
    {
        var target = item.IsCompleted || item.IsTorrent ? item.FullPath : item.TempPath;
        if (File.Exists(target) || Directory.Exists(target))
            Process.Start("explorer.exe", $"/select,\"{target}\"");
        else if (Directory.Exists(item.SaveDirectory))
            Process.Start("explorer.exe", $"\"{item.SaveDirectory}\"");
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is not { } item) return;
        if (_clipboard is not null) _clipboard.SetTextSilently(item.Url);
        else Clipboard.SetText(item.Url);
    }

    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is { } item) OpenProgress(item);
    }

    private void DownloadGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && System.Windows.Controls.ItemsControl.ContainerFromElement(DownloadGrid, d) is System.Windows.Controls.DataGridRow row
            && row.Item is DownloadItem item)
            OpenItem(item);
    }

    private void CategoryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is SidebarEntry entry)
        {
            _view.Filter = o => entry.Filter((DownloadItem)o);
            UpdateEmptyState();
        }
    }

    private void SpeedLimit_Click(object sender, RoutedEventArgs e)
    {
        var settings = _manager.Settings.Clone();
        settings.SpeedLimitEnabled = SpeedLimitMenu.IsChecked;
        _manager.ApplySettings(settings);
        UpdateStatusBar();
    }

    private void Options_Click(object sender, RoutedEventArgs e) => ShowOptions(0);
    private void Scheduler_Click(object sender, RoutedEventArgs e) => ShowOptions(3);

    private void ShowOptions(int tab)
    {
        var old = _manager.Settings;
        var dialog = new OptionsWindow(old.Clone(), tab) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _manager.ApplySettings(dialog.Settings);
        if (old.BrowserIntegration != dialog.Settings.BrowserIntegration || old.BrowserPort != dialog.Settings.BrowserPort)
            App.Current.RestartBridge();
        SpeedLimitMenu.IsChecked = dialog.Settings.SpeedLimitEnabled;
        UpdateStatusBar();
    }

    private void InstallExtension_Click(object sender, RoutedEventArgs e) => ShowBrowserSetup();

    public void ShowBrowserSetup() => new BrowserSetupWindow { Owner = IsVisible ? this : null }.ShowDialog();

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            "VeloDM 1.2\n\nMulti-connection download manager with dynamic segmentation, resume, queue, scheduler, " +
            "speed limiter, BitTorrent (magnet links and .torrent files) and browser integration.\n\n" +
            "BitTorrent support uses MonoTorrent (MIT licence).\n\nData folder:\n" + _manager.DataDirectory,
            "About VeloDM", MessageBoxButton.OK, MessageBoxImage.Information);

    private void Exit_Click(object sender, RoutedEventArgs e) => App.Current.ExitApp();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowAddDialog(null);
            e.Handled = true;
        }
        else if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control)
        {
            AddTorrent_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && DownloadGrid.IsKeyboardFocusWithin)
        {
            Delete_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && DownloadGrid.IsKeyboardFocusWithin && Selected.FirstOrDefault() is { } item)
        {
            OpenItem(item);
            e.Handled = true;
        }
    }

    // ---- closing -------------------------------------------------------------------------

    public void CloseForExit()
    {
        _closingForExit = true;
        foreach (var w in _progressWindows.Values.ToList()) w.Close();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closingForExit) return;
        if (_manager.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                App.Current.Balloon("VeloDM is still running", "Downloads continue in the background. Right-click the tray icon to exit.");
            }
            return;
        }
        // WPF forbids Close() while a close is in progress, so exit on the next dispatcher turn.
        e.Cancel = true;
        Dispatcher.BeginInvoke(App.Current.ExitApp);
    }

    protected override void OnClosed(EventArgs e)
    {
        _clipboard?.Dispose();
        base.OnClosed(e);
    }
}
