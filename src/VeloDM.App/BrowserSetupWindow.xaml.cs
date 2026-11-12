using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace VeloDM.App;

/// <summary>
/// Walks the user through loading the extension. Chromium browsers refuse to install an extension
/// from outside their store silently, so the most an app can do is open the right page and hand
/// over the folder path.
/// </summary>
public partial class BrowserSetupWindow : Window
{
    private static readonly (string Name, string Exe, string Url)[] Browsers =
    {
        ("Google Chrome", "chrome.exe", "chrome://extensions"),
        ("Microsoft Edge", "msedge.exe", "edge://extensions"),
        ("Brave", "brave.exe", "brave://extensions"),
        ("Vivaldi", "vivaldi.exe", "vivaldi://extensions"),
        ("Opera", "opera.exe", "opera://extensions"),
    };

    public static string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "extension");

    public BrowserSetupWindow()
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        FolderBox.Text = ExtensionFolder;

        foreach (var (name, exe, url) in Browsers)
        {
            var path = FindBrowser(exe);
            if (path is null) continue;
            var button = new Button
            {
                Content = $"Open {name} extensions",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            button.Click += (_, _) => OpenExtensionsPage(path, url);
            BrowserButtons.Children.Add(button);
        }
        if (BrowserButtons.Children.Count == 0)
        {
            BrowserButtons.Children.Add(new TextBlock
            {
                Text = "No Chromium browser was found. Open chrome://extensions (or your browser's equivalent) yourself.",
                TextWrapping = TextWrapping.Wrap,
            });
        }

        Loaded += (_, _) => CopyFolder();
    }

    private static string? FindBrowser(string exe)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
            if (key?.GetValue("") is string path && File.Exists(path)) return path;
        }
        return null;
    }

    private void OpenExtensionsPage(string browserPath, string url)
    {
        CopyFolder();
        try
        {
            Process.Start(new ProcessStartInfo(browserPath, url) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not start the browser: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyFolder()
    {
        try
        {
            Clipboard.SetText(ExtensionFolder);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another app holds the clipboard; the Copy button lets the user retry.
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyFolder();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(ExtensionFolder)) Process.Start("explorer.exe", $"\"{ExtensionFolder}\"");
    }
}
