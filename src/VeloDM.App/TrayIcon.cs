using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VeloDM.App;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(App app)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open VeloDM", null, (_, _) => app.ShowMain());
        menu.Items.Add("Add URL…", null, (_, _) => app.ShowAddDialog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Start queue", null, (_, _) => App.Manager.StartQueue());
        menu.Items.Add("Stop all downloads", null, (_, _) => App.Manager.StopAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => app.ExitApp());

        _icon = new NotifyIcon
        {
            Icon = AppIcon.Icon,
            Text = "VeloDM",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => app.ShowMain();
    }

    public void Balloon(string title, string text, bool error = false) =>
        _icon.ShowBalloonTip(4000, title, text.Length > 250 ? text[..250] : text, error ? ToolTipIcon.Error : ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>
/// The app icon from Assets\VeloDM.ico (regenerate it with tools/IconGen). The .ico holds sizes
/// 16–256, so the tray and windows each get a natively drawn size for the current DPI.
/// </summary>
public static class AppIcon
{
    private static readonly Uri Source = new("pack://application:,,,/Assets/VeloDM.ico");
    private static Icon? _icon;
    private static ImageSource? _image;

    public static Icon Icon => _icon ??= LoadTrayIcon();

    // WPF picks the best frame per use (title bar, taskbar, Alt+Tab).
    public static ImageSource ImageSource => _image ??= BitmapFrame.Create(Source);

    private static Icon LoadTrayIcon()
    {
        using var stream = System.Windows.Application.GetResourceStream(Source).Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }
}
