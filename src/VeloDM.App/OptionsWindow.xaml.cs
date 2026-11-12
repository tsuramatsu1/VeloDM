using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using VeloDM.Core;

namespace VeloDM.App;

public sealed record Choice<T>(string Label, T Value);

public partial class OptionsWindow : Window
{
    public AppSettings Settings { get; }

    public OptionsWindow(AppSettings settings, int tab)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        Settings = settings;
        AfterQueueBox.ItemsSource = Enum.GetValues<AfterQueueAction>();
        ThemeBox.ItemsSource = new Choice<AppTheme>[]
        {
            new("Light", AppTheme.Light), new("Dark", AppTheme.Dark), new("Same as Windows", AppTheme.System),
        };
        ProxyModeBox.ItemsSource = new Choice<ProxyMode>[]
        {
            new("Use the Windows proxy setting", ProxyMode.System), new("No proxy", ProxyMode.None), new("Use this proxy", ProxyMode.Custom),
        };
        QualityBox.ItemsSource = new Choice<int>[]
        {
            new("Best available", 0), new("Up to 4K (2160p)", 2160), new("Up to 1440p", 1440), new("Up to 1080p", 1080),
            new("Up to 720p", 720), new("Up to 480p", 480), new("Up to 360p", 360),
        };
        ProxyPasswordBox.Password = settings.ProxyPassword;
        DataContext = settings;
        UpdateProxyFields();
        UpdateToolsStatus();
        Tabs.SelectedIndex = tab;
        _runAtStartup = StartupRegistration.IsEnabled;
        RunAtStartupBox.IsChecked = _runAtStartup;
    }

    // Kept in the registry, not in settings.json, so it is read and written here.
    private readonly bool _runAtStartup;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Default download folder" };
        if (Directory.Exists(Settings.DownloadFolder)) dialog.InitialDirectory = Settings.DownloadFolder;
        if (dialog.ShowDialog(this) != true) return;
        Settings.DownloadFolder = dialog.FolderName;
        DataContext = null;
        DataContext = Settings;
    }

    private void ProxyModeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateProxyFields();

    private void UpdateProxyFields()
    {
        bool custom = ProxyModeBox.SelectedValue is ProxyMode.Custom;
        ProxyAddressBox.IsEnabled = ProxyLoginPanel.IsEnabled = custom;
    }

    private void UpdateToolsStatus()
    {
        var tools = App.Manager.LocateVideoTools();
        ToolsStatus.Text = tools is null
            ? "The video tools are not installed yet. VeloDM offers to download them the first time you download a streamed video."
            : "The video tools are installed:\n" + tools.YtDlp + "\n" + Path.Combine(tools.FfmpegDirectory, "ffmpeg.exe") + "\n" +
              (tools.JsRuntime is { } runtime
                  ? runtime[(runtime.IndexOf(':') + 1)..]
                  : "No JavaScript runtime (Deno) was found: YouTube needs one. Choose Update video tools to add it.") +
              (VideoTools.ManagedToolAge(App.Manager.DataDirectory) is { } age ? $"\nDownloaded {age.Days} day(s) ago." : "");
        ToolsButton.Content = tools is null ? "Download video tools (about 250 MB)" : "Update video tools";
    }

    private void VideoTools_Click(object sender, RoutedEventArgs e)
    {
        VideoToolsWindow.Install(this, App.Manager);
        UpdateToolsStatus();
    }

    private static string? ScannerCommandError(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        try
        {
            var (program, _) = PostProcessor.ParseScannerCommand(command, "file");
            return File.Exists(program) ? null : "The virus scanner program was not found: " + program;
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
    }

    private void Associate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociations.Register();
            MessageBox.Show(this,
                "VeloDM is now registered for magnet links and .torrent files.\n\n" +
                "Browsers will offer to open magnet links with VeloDM. If Windows keeps opening .torrent files " +
                "in another app, right-click one → Open with → Choose another app → VeloDM → Always.",
                "BitTorrent", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not register: " + ex.Message, "BitTorrent", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string? error =
            !Path.IsPathFullyQualified(Settings.DownloadFolder) ? "The download folder must be an absolute path." :
            Settings.DefaultConnections is < 1 or > 32 ? "Connections per download must be between 1 and 32." :
            Settings.MaxSimultaneousDownloads is < 1 or > 20 ? "Simultaneous downloads must be between 1 and 20." :
            Settings.MaxRetries is < 0 or > 1000 ? "Retries must be between 0 and 1000." :
            Settings.SpeedLimitKBps < 1 ? "The speed limit must be at least 1 KB/s." :
            Settings.BrowserPort is < 1024 or > 65535 ? "The port must be between 1024 and 65535." :
            !TimeOnly.TryParse(Settings.ScheduleStartTime, out _) ? "The start time must look like 02:00." :
            !TimeOnly.TryParse(Settings.ScheduleStopTime, out _) ? "The stop time must look like 08:00." :
            Settings.TorrentPort is < 1024 or > 65535 ? "The torrent port must be between 1024 and 65535." :
            Settings.TorrentPort == Settings.BrowserPort ? "The torrent port must differ from the browser integration port." :
            Settings.TorrentUploadLimitKBps < 0 ? "The upload limit cannot be negative." :
            Settings.SeedRatioLimit is <= 0 or > 1000 ? "The seed ratio must be greater than 0." :
            Settings.ProxyMode == ProxyMode.Custom && !HttpEngine.IsValidProxyAddress(Settings.ProxyAddress)
                ? "The proxy address must look like http://host:port or socks5://host:port." :
            !string.IsNullOrWhiteSpace(Settings.UpdateFeedUrl) && !(Uri.TryCreate(Settings.UpdateFeedUrl.Trim(), UriKind.Absolute, out var feed)
                                                                    && feed.Scheme == Uri.UriSchemeHttps)
                ? "The update address must start with https:// (or be left empty)." :
            ScannerCommandError(Settings.VirusScannerCommand);
        if (error is not null)
        {
            MessageBox.Show(this, error, "Options", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Settings.ProxyPassword = ProxyPasswordBox.Password;

        bool runAtStartup = RunAtStartupBox.IsChecked == true;
        if (runAtStartup != _runAtStartup)
        {
            try
            {
                StartupRegistration.Set(runAtStartup);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not change the startup setting: " + ex.Message, "Options", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        DialogResult = true;
    }
}
