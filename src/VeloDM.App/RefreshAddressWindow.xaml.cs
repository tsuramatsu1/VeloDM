using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using VeloDM.Core;

namespace VeloDM.App;

/// <summary>
/// Replaces the address of an unfinished HTTP download whose link has expired. While the
/// window is open, the next download the browser extension captures is offered here instead
/// of being added as a new download.
/// </summary>
public partial class RefreshAddressWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly DownloadItem _item;
    private BridgeRequest? _captured;
    private bool _busy;

    public DownloadItem Item => _item;

    public RefreshAddressWindow(DownloadManager manager, DownloadItem item)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _manager = manager;
        _item = item;
        FileText.Text = item.FileName;
        OpenPageButton.IsEnabled = App.IsHttpUrl(item.Referrer);
        if (!OpenPageButton.IsEnabled)
            OpenPageButton.ToolTip = "VeloDM does not know which page this download came from.";
        Loaded += (_, _) => UrlBox.Focus();
    }

    /// <summary>Called when the browser extension hands over a download while this window is open.</summary>
    public void OfferCaptured(BridgeRequest request)
    {
        _captured = request;
        UrlBox.Text = request.Url ?? "";
        _captured = request; // the text change above cleared it
        StatusText.Text = "New link received from the browser" +
                          (string.IsNullOrWhiteSpace(request.FileName) ? "." : $" ({request.FileName}).");
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void UrlBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _captured = null; // a hand-edited address no longer belongs to the captured request
        UseButton.IsEnabled = !_busy && App.IsHttpUrl(UrlBox.Text);
    }

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if (!App.IsHttpUrl(_item.Referrer)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_item.Referrer!.Trim()) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText.Text = "Could not open the page: " + ex.Message;
        }
    }

    private async void Use_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (!App.IsHttpUrl(url)) return;
        var captured = _captured;
        var referrer = captured?.Referrer ?? _item.Referrer;
        var cookies = captured?.Cookies ?? _item.Cookies;
        var userAgent = captured?.UserAgent ?? _item.UserAgent ?? _manager.Settings.UserAgent;

        _busy = true;
        UseButton.IsEnabled = false;
        StatusText.Text = "Checking the new address…";
        try
        {
            var info = await HttpEngine.ProbeAsync(url, referrer, cookies, userAgent, CancellationToken.None);
            bool sameFile = info.AcceptRanges && _item.TotalSize > 0 && info.Size == _item.TotalSize;
            if (!sameFile && _item.Downloaded > 0)
            {
                string reason = !info.AcceptRanges
                    ? "The new address does not support resuming."
                    : $"The new address points to a file of a different size ({Format.Size(info.Size)} instead of {_item.SizeText}).";
                var answer = MessageBox.Show(this,
                    reason + "\n\nUsing it will start the download again from the beginning. Continue?",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    StatusText.Text = "Not changed. Try another link.";
                    return;
                }
            }

            await _manager.UpdateAddressAsync(_item, url, captured?.Referrer, captured?.Cookies, captured?.UserAgent);
            _manager.Start(_item);
            Close();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TimeoutException or System.IO.IOException
                                       or InvalidOperationException or UriFormatException)
        {
            StatusText.Text = "That address did not work: " + ex.Message;
        }
        finally
        {
            _busy = false;
            UseButton.IsEnabled = App.IsHttpUrl(UrlBox.Text);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
