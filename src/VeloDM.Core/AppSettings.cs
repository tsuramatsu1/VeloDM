using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeloDM.Core;

public enum AfterQueueAction { DoNothing, ExitApp, Sleep, ShutDown }

public enum ProxyMode { System, None, Custom }

public enum AppTheme { Light, Dark, System }

public sealed class AppSettings
{
    public string DownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public bool UseCategoryFolders { get; set; } = true;

    public int DefaultConnections { get; set; } = 8;
    public int MaxSimultaneousDownloads { get; set; } = 3;
    public bool SpeedLimitEnabled { get; set; }
    public int SpeedLimitKBps { get; set; } = 1024;
    public int MaxRetries { get; set; } = 10;
    public string UserAgent { get; set; } = HttpEngine.DefaultUserAgent;

    public bool MonitorClipboard { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowProgressWindow { get; set; } = true;

    public bool BrowserIntegration { get; set; } = true;
    public int BrowserPort { get; set; } = 9615;
    // On by default, like IDM: a download from the browser first shows its name and folder
    // (already filled in for its category) with Start Download and Download Later.
    public bool ShowDialogForBrowserDownloads { get; set; } = true;

    /// <summary>
    /// Which generation of defaults this file was written with. Version 1 files (before 1.3.2)
    /// started browser downloads without asking; they are switched to asking once, on load.
    /// </summary>
    public int SettingsVersion { get; set; }

    private const int CurrentSettingsVersion = 2;
    public string CaptureExtensions { get; set; } =
        "3gp 7z aac apk avi bin bz2 deb dmg epub exe flac flv gz img iso jar m4a m4v mkv mov mp3 mp4 mpeg mpg msi msix " +
        "ogg opus pdf pkg rar rpm tar tgz torrent ts wav webm wma wmv xz zip zst";

    public int TorrentPort { get; set; } = 51515;
    public bool TorrentDht { get; set; } = true;
    public bool TorrentPortForwarding { get; set; } = true;
    public bool SeedAfterDownload { get; set; }
    public double SeedRatioLimit { get; set; } = 1.0;
    public int TorrentUploadLimitKBps { get; set; }

    public bool ScheduleStartEnabled { get; set; }
    public string ScheduleStartTime { get; set; } = "02:00";
    public bool ScheduleStopEnabled { get; set; }
    public string ScheduleStopTime { get; set; } = "08:00";
    public AfterQueueAction AfterQueue { get; set; } = AfterQueueAction.DoNothing;

    // Proxy for HTTP downloads and streams (BitTorrent traffic is not proxied).
    public ProxyMode ProxyMode { get; set; } = ProxyMode.System;
    /// <summary>Custom proxy, e.g. "http://host:8080" or "socks5://host:1080".</summary>
    public string ProxyAddress { get; set; } = "";
    public string ProxyUser { get; set; } = "";
    /// <summary>Encrypted for the current Windows user; set and read through <see cref="ProxyPassword"/>.</summary>
    public string ProxyPasswordProtected { get; set; } = "";

    [JsonIgnore]
    public string ProxyPassword
    {
        get => Secrets.Unprotect(ProxyPasswordProtected);
        set => ProxyPasswordProtected = Secrets.Protect(value);
    }

    // After a download finishes.
    public bool ExtractArchives { get; set; }
    public bool ScanAfterDownload { get; set; }
    /// <summary>Empty = Windows Defender. Otherwise a command line in which {file} is the downloaded path.</summary>
    public string VirusScannerCommand { get; set; } = "";

    /// <summary>Streamed video: the tallest picture to download, 0 = the best available.</summary>
    public int StreamMaxHeight { get; set; }

    public const string DefaultUpdateFeed = "https://github.com/tsuramatsu1/VeloDM";

    /// <summary>Where to look for new versions: an update.json address or a GitHub repository address.</summary>
    public string UpdateFeedUrl { get; set; } = DefaultUpdateFeed;
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    public AppTheme Theme { get; set; } = AppTheme.Light;

    [JsonIgnore] public long SpeedLimitBytes => SpeedLimitEnabled ? Math.Max(1, SpeedLimitKBps) * 1024L : 0;

    public bool IsCapturedExtension(string fileNameOrPath)
    {
        var ext = Path.GetExtension(fileNameOrPath).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0) return false;
        return CaptureExtensions.Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Any(e => e.TrimStart('.').Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) is { } loaded)
            {
                if (loaded.SettingsVersion < CurrentSettingsVersion) loaded.ShowDialogForBrowserDownloads = true;
                loaded.SettingsVersion = CurrentSettingsVersion;
                return loaded;
            }
        }
        catch (Exception) { /* corrupt settings: fall back to defaults */ }
        return new AppSettings { SettingsVersion = CurrentSettingsVersion };
    }

    public void Save(string path)
    {
        SettingsVersion = CurrentSettingsVersion; // whatever is saved now is the user's own choice
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
}
