using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using VeloDM.Core;

namespace VeloDM.App;

public partial class App : Application
{
    public static DownloadManager Manager { get; private set; } = null!;
    public static new App Current => (App)Application.Current;

    private Mutex? _mutex;
    private BrowserBridge? _bridge;
    private TrayIcon? _tray;
    private MainWindow? _main;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A URL, magnet link or .torrent file (from file/protocol associations or the command line).
        string? url = e.Args.FirstOrDefault(a => IsHttpUrl(a) || TorrentSources.IsMagnet(a) || TorrentSources.IsTorrentFile(a));
        // VELODM_DATA runs a separate copy with its own settings and download list (portable use, testing).
        string? dataDirectory = Environment.GetEnvironmentVariable("VELODM_DATA");
        if (string.IsNullOrWhiteSpace(dataDirectory) || !System.IO.Path.IsPathFullyQualified(dataDirectory)) dataDirectory = null;
        _dataDirectory = dataDirectory ?? DownloadManager.DefaultDataDirectory;
        string instance = dataDirectory is null ? "" : "." + ((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(dataDirectory)).ToString("x");
        _mutex = new Mutex(true, "VeloDM.SingleInstance." + Environment.UserName + instance, out bool firstInstance);
        if (!firstInstance)
        {
            ForwardToRunningInstance(url);
            Shutdown();
            return;
        }

        var dispatcher = Dispatcher;
        ObservableObject.Dispatcher = action =>
        {
            if (dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        };
        DispatcherUnhandledException += OnUnhandledException;

        Manager = new DownloadManager(_dataDirectory);
        Manager.LoadDownloads();
        ThemeManager.Apply(Manager.Settings.Theme);
        Manager.DownloadCompleted += item => Dispatcher.BeginInvoke(() => OnDownloadCompleted(item));
        Manager.DownloadFailed += item => Dispatcher.BeginInvoke(() =>
            _tray?.Balloon("Download failed", $"{item.FileName}\n{item.ErrorMessage}", error: true));
        Manager.QueueFinished += () => Dispatcher.BeginInvoke(OnQueueFinished);
        Manager.PostProcessed += (item, result) => Dispatcher.BeginInvoke(() =>
        {
            if (result.ThreatFound)
                _tray?.Balloon("Virus scan warning", $"{item.FileName}\n{result.Note}", error: true);
        });

        // Escape hatch for VMs / remote sessions where GPU composition leaves WPF windows blank.
        if (Environment.GetEnvironmentVariable("VELODM_SOFTWARE") == "1" || e.Args.Contains("--software-render"))
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        _main = new MainWindow();
        _tray = new TrayIcon(this);
        if (!e.Args.Contains("--tray")) _main.Show();

        RestartBridge();
        if (url is not null) _main.ShowAddDialog(new BridgeRequest(url));
        // The installer passes this on a first install.
        if (e.Args.Contains("--setup-extension")) Dispatcher.BeginInvoke(_main.ShowBrowserSetup);
        if (Manager.Settings.CheckForUpdatesOnStartup) _ = CheckForUpdatesAsync(silent: true);
    }

    private string _dataDirectory = DownloadManager.DefaultDataDirectory;

    public static Version CurrentVersion => typeof(App).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>
    /// Looks for a newer version. A silent check (at startup) only shows a tray notification when
    /// there is one; a manual check always reports what it found.
    /// </summary>
    public async System.Threading.Tasks.Task CheckForUpdatesAsync(bool silent)
    {
        var feed = Manager.Settings.UpdateFeedUrl;
        if (string.IsNullOrWhiteSpace(feed))
        {
            if (!silent)
                MessageBox.Show("No update address is set. Add one in Options → General.", "VeloDM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var update = await UpdateChecker.CheckAsync(feed, CurrentVersion, CancellationToken.None);
            if (update is null)
            {
                if (!silent)
                    MessageBox.Show($"VeloDM {CurrentVersion.ToString(3)} is the latest version.", "VeloDM", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (silent)
            {
                _tray?.Balloon($"VeloDM {update.Version.ToString(3)} is available", "Open Help → Check for updates to get it.");
                return;
            }
            var notes = string.IsNullOrWhiteSpace(update.Notes) ? "" : "\n\n" + (update.Notes.Length > 600 ? update.Notes[..600] + "…" : update.Notes);
            var answer = MessageBox.Show(
                $"VeloDM {update.Version.ToString(3)} is available (you have {CurrentVersion.ToString(3)}).{notes}\n\n" +
                (update.InstallerUrl is null ? "Open the download page?" : "Download the installer now?"),
                "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes) return;
            if (update.InstallerUrl is not null)
                _main?.HandleExternalRequest(new BridgeRequest(update.InstallerUrl, Referrer: update.PageUrl, Force: true));
            else
                Process.Start(new ProcessStartInfo(update.PageUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OperationCanceledException or ArgumentException
                                       or JsonException or System.IO.IOException or System.ComponentModel.Win32Exception)
        {
            if (!silent)
                MessageBox.Show("Could not check for updates: " + ex.Message, "VeloDM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static bool IsHttpUrl(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>Anything VeloDM can download: http(s) URLs, magnet links and local .torrent files.</summary>
    public static bool IsDownloadSource(string? text) => IsHttpUrl(text) || TorrentSources.IsTorrent(text);

    // ---- window / tray -----------------------------------------------------------------

    public void ShowMain()
    {
        if (_main is null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void ShowAddDialog() => _main?.ShowAddDialog(null);

    public void Balloon(string title, string text) => _tray?.Balloon(title, text);

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _bridge?.Dispose();
        _tray?.Dispose();
        _main?.CloseForExit();
        Manager.Dispose();
        _mutex?.ReleaseMutex();
        Shutdown();
    }

    // ---- browser integration -------------------------------------------------------------

    public void RestartBridge()
    {
        _bridge?.Dispose();
        _bridge = null;
        if (!Manager.Settings.BrowserIntegration) return;
        try
        {
            _bridge = new BrowserBridge(Manager.Settings.BrowserPort, HandleBridgeRequest);
            _bridge.Start();
        }
        catch (Exception ex)
        {
            _bridge = null;
            _tray?.Balloon("Browser integration unavailable",
                $"Could not listen on port {Manager.Settings.BrowserPort}: {ex.Message}", error: true);
        }
    }

    /// <summary>Called on a thread-pool thread. Returns whether VeloDM takes the download.</summary>
    private bool HandleBridgeRequest(BridgeRequest request)
    {
        if (request.ShowWindow)
        {
            Dispatcher.BeginInvoke(ShowMain);
            return true;
        }
        if (!request.Force && !request.Stream && !ShouldCapture(request)) return false;
        Dispatcher.BeginInvoke(() => _main?.HandleExternalRequest(request));
        return true;
    }

    private static bool ShouldCapture(BridgeRequest r)
    {
        var settings = Manager.Settings;
        if (TorrentSources.IsMagnet(r.Url)) return true;
        if (!string.IsNullOrWhiteSpace(r.FileName) && settings.IsCapturedExtension(r.FileName)) return true;
        if (Uri.TryCreate(r.Url, UriKind.Absolute, out var u) && settings.IsCapturedExtension(u.AbsolutePath)) return true;
        return FileNameHelper.ExtensionForMime(r.Mime) is { } ext && settings.IsCapturedExtension("x" + ext);
    }

    private void ForwardToRunningInstance(string? url)
    {
        try
        {
            var port = AppSettings.Load(System.IO.Path.Combine(_dataDirectory, "settings.json")).BrowserPort;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/{(url is null ? "show" : "add")}");
            req.Headers.Add(BrowserBridge.AuthHeader, "1");
            req.Content = new StringContent(JsonSerializer.Serialize(new { url, force = true }), Encoding.UTF8, "application/json");
            client.Send(req);
        }
        catch (Exception)
        {
            // The running instance has browser integration disabled; nothing else to do.
        }
    }

    // ---- notifications -----------------------------------------------------------------

    private void OnDownloadCompleted(DownloadItem item)
    {
        _tray?.Balloon("Download complete", item.FileName);
        _main?.OnDownloadCompleted(item);
    }

    private void OnQueueFinished()
    {
        switch (Manager.Settings.AfterQueue)
        {
            case AfterQueueAction.ExitApp:
                ExitApp();
                break;
            case AfterQueueAction.Sleep:
                System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, false, false);
                break;
            case AfterQueueAction.ShutDown:
                Process.Start(new ProcessStartInfo("shutdown", "/s /t 60 /c \"VeloDM finished the download queue.\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
                _tray?.Balloon("Shutting down in 60 seconds", "Run \"shutdown /a\" to cancel.");
                break;
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "VeloDM", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
