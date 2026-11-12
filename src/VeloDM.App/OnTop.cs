using System.Windows;

namespace VeloDM.App;

/// <summary>
/// A download usually starts from the browser, so VeloDM is not the window in front. Windows
/// does not let a background app push itself forward by asking, and whatever it opens lands
/// behind the browser. These helpers put VeloDM's windows above it anyway.
/// </summary>
internal static class OnTop
{
    /// <summary>Shows a window above the others without taking the keyboard away from the browser.</summary>
    public static void Show(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.ShowActivated = false;
        window.Topmost = true;
        window.Show();
        window.Topmost = false; // stays in front until the user picks another window
    }

    /// <summary>A message box that appears in front even when VeloDM itself is in the background.</summary>
    public static MessageBoxResult Ask(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage image,
                                       MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        if (owner is { IsVisible: true, IsActive: true })
            return MessageBox.Show(owner, text, caption, buttons, image, defaultResult);

        // A message box sits above its owner, so an invisible always-on-top owner carries it to the front.
        var anchor = new Window
        {
            Width = 1,
            Height = 1,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Opacity = 0,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        anchor.Show();
        try
        {
            return MessageBox.Show(anchor, text, caption, buttons, image, defaultResult);
        }
        finally
        {
            anchor.Close();
        }
    }
}
