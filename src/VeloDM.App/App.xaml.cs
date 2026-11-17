using System;
using System.Collections.Generic;
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

        Log.Initialize(_dataDirectory);
        Log.Info($"VeloDM {CurrentVersion.ToString(3)} started on {Environment.OSVersion.VersionString}; data folder: {_dataDirectory}");
        Manager = new DownloadManager(_dataDirectory);
        Manager.LoadDownloads();
        ThemeManager.Apply(Manager.Settings.Theme);
        Manager.DownloadCompleted += item => Dispatcher.BeginInvoke(() => OnDownloadCompleted(item));
        Manager.DownloadFailed += item => Dispatcher.BeginInvoke(() =>
            _tray?.Balloon("Download failed",
                $"{item.FileName}\n{item.ErrorMessage}" +
                (item.IsStream ? "\nOpen the download to update the video tools and try again." : ""), error: true));
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
            Log.Info("Update check: " + (update is null ? "no newer version" : $"version {update.Version.ToString(3)} is available"));
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
            Log.Warn("Update check failed", ex);
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
        Log.Info("VeloDM exiting");
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
            _bridge.ExtensionSeen += OnExtensionSeen;
            _bridge.Start();
        }
        catch (Exception ex)
        {
            _bridge = null;
            Log.Warn($"Browser integration could not listen on port {Manager.Settings.BrowserPort}", ex);
            _tray?.Balloon("Browser integration unavailable",
                $"Could not listen on port {Manager.Settings.BrowserPort}: {ex.Message}", error: true);
        }
    }

    private readonly HashSet<string> _extensionsSeen = [];

    /// <summary>The version of the extension that ships with this copy of VeloDM, or null if it cannot be read.</summary>
    private static Version? BundledExtensionVersion()
    {
        try
        {
            var manifest = System.IO.Path.Combine(AppContext.BaseDirectory, "extension", "manifest.json");
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(manifest));
            return Version.TryParse(doc.RootElement.GetProperty("version").GetString(), out var version) ? version : null;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or JsonException
                                       or System.Collections.Generic.KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// A browser keeps running the copy of the extension's script it first loaded, even after
    /// VeloDM is upgraded and the browser restarted. From 1.5.4 the extension notices the newer
    /// copy and reloads itself; an older one stays until Reload is pressed on the browser's
    /// extensions page, so the user is told, once per run for each old copy that calls.
    /// </summary>
    private void OnExtensionSeen(string reported, string? script)
    {
        lock (_extensionsSeen)
        {
            if (!_extensionsSeen.Add(reported + "|" + script)) return;
        }
        var bundled = BundledExtensionVersion();
        bool updatesItself = Version.TryParse(script, out var running);
        if (updatesItself)
        {
            Log.Info($"Browser extension: script {running}; this VeloDM ships {bundled}");
            return; // an older script of this kind is about to reload itself
        }
        // Without the script header the version comes from the manifest, which the browser may
        // have read again since: the script is older than 1.5.4 whatever it says.
        string says = Version.TryParse(reported, out _) ? reported : "older than 1.5.1";
        Log.Warn($"Browser extension: an old script is running (it reports {says}, and cannot update itself); this VeloDM ships {bundled}. Reload is needed on the browser's extensions page.");
        Dispatcher.BeginInvoke(() => OnTop.Ask(null,
            "One of your browsers is still running an old copy of the VeloDM extension, so recent fixes are missing. " +
            "For example, the browser's own Save window still opens before VeloDM's.\n\n" +
            "To update it, in that browser:\n" +
            "  1. Open the extensions page (type brave://extensions, edge://extensions or chrome://extensions in the address bar).\n" +
            "  2. On VeloDM Integration, press the Reload button (the round arrow).\n\n" +
            "This is needed once in each browser. After that the extension updates itself whenever VeloDM is upgraded.",
            "VeloDM - reload the browser extension", MessageBoxButton.OK, MessageBoxImage.Information));
    }

    /// <summary>Called on a thread-pool thread. Returns whether VeloDM takes the download.</summary>
    private bool HandleBridgeRequest(BridgeRequest request)
    {
        if (request.ShowWindow)
        {
            Dispatcher.BeginInvoke(ShowMain);
            return true;
        }
        string what = $"{(request.Stream ? "video on " : "")}{Log.Address(request.Url)} (file \"{request.FileName}\", type \"{request.Mime}\")" +
                      (request.AudioUrl is null ? "" : $" with sound from {Log.Address(request.AudioUrl)}") +
                      (string.IsNullOrWhiteSpace(request.Note) ? "" : $" [{(request.Note.Length > 700 ? request.Note[..700] : request.Note)}]");
        if (!request.Force && !request.Stream && !ShouldCapture(request))
        {
            Log.Info("Browser offered " + what + ": left to the browser (file type is not in the capture list)");
            return false;
        }
        Log.Info("Browser offered " + what + ": taken");
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
        Log.Error("Unexpected error", e.Exception);
        MessageBox.Show(e.Exception.Message, "VeloDM", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
