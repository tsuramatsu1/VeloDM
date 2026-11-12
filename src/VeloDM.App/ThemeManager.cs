using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VeloDM.Core;

namespace VeloDM.App;

/// <summary>
/// Switches the app between its light and dark look. Light is the classic Windows look with
/// VeloDM's own colours; dark uses WPF's Fluent dark theme for the standard controls plus the
/// matching palette from Themes/Dark.xaml.
/// </summary>
public static class ThemeManager
{
    private const string PaletteMarker = "WindowBrush";
    private static bool _watching;
    private static AppTheme _current;

    public static bool IsDark { get; private set; }

    public static void Apply(AppTheme theme)
    {
        _current = theme;
        bool dark = theme == AppTheme.Dark || (theme == AppTheme.System && WindowsAppsUseDark());
        var app = Application.Current;

#pragma warning disable WPF0001 // ThemeMode is still marked experimental
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.None;
#pragma warning restore WPF0001

        // Added after the Fluent dictionary (which ThemeMode manages) so VeloDM's colours win.
        var merged = app.Resources.MergedDictionaries;
        var old = merged.FirstOrDefault(d => d.Contains(PaletteMarker));
        if (old is not null) merged.Remove(old);
        merged.Add(new ResourceDictionary
        {
            Source = new Uri($"/VeloDM;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative),
        });
        IsDark = dark;

        // Extends the current theme's button, which a XAML style cannot do when the theme changes at run time.
        var dialogButton = new Style(typeof(Button), app.TryFindResource(typeof(Button)) as Style);
        dialogButton.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 96.0));
        dialogButton.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 5, 12, 5)));
        dialogButton.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0)));
        app.Resources["DialogButton"] = dialogButton;

        if (!_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _current == AppTheme.System)
                    app.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
            };
        }
    }

    private static bool WindowsAppsUseDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
