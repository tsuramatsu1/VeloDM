using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VeloDM.Core;

namespace VeloDM.App;

/// <summary>Downloads yt-dlp and FFmpeg into VeloDM's data folder, with progress.</summary>
public partial class VideoToolsWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly CancellationTokenSource _cts = new();

    public VideoToolsWindow(DownloadManager manager)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _manager = manager;
        Loaded += async (_, _) => await InstallAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    /// <summary>
    /// True when the tools are present. If they are not, explains what they are and, with the
    /// user's agreement, downloads them.
    /// </summary>
    public static bool EnsureInstalled(Window? owner, DownloadManager manager)
    {
        if (manager.LocateVideoTools() is not null) return true;
        var answer = MessageBox.Show(owner ?? Application.Current.MainWindow,
            "Streamed video (YouTube and most other video sites) is downloaded with two free, open-source helper " +
            "programs that are not part of VeloDM:\n\n" +
            "  •  yt-dlp — reads the video page and downloads the stream\n" +
            "  •  FFmpeg — joins the picture and the sound into one file\n\n" +
            "VeloDM can download them now from their official GitHub release pages (about 150 MB) into its data folder.\n\n" +
            "Download the video tools?",
            "Video tools needed", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return answer == MessageBoxResult.Yes && Install(owner, manager);
    }

    public static bool Install(Window? owner, DownloadManager manager)
    {
        var window = new VideoToolsWindow(manager);
        if (owner is { IsVisible: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return window.ShowDialog() == true;
    }

    private async Task InstallAsync()
    {
        var progress = new Progress<(string Step, double Percent)>(p =>
        {
            StepText.Text = $"{p.Step}… {p.Percent:0}%";
            Progress.Value = p.Percent;
        });
        try
        {
            await Task.Run(() => VideoTools.InstallAsync(_manager.DataDirectory, progress, _cts.Token));
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            // Closed by the user.
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "The video tools could not be downloaded:\n\n" + ex.Message +
                "\n\nYou can also put yt-dlp.exe and ffmpeg.exe in this folder yourself:\n" +
                VideoTools.DirectoryFor(_manager.DataDirectory),
                "Video tools", MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
