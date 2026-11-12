using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VeloDM.App;

/// <summary>Raises <see cref="TextCopied"/> whenever text lands on the clipboard (WM_CLIPBOARDUPDATE).</summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private readonly HwndSource _source;
    private string? _ignoreNext;

    public event Action<string>? TextCopied;

    public ClipboardMonitor(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle)!;
        _source.AddHook(WndProc);
        AddClipboardFormatListener(handle);
    }

    /// <summary>Copies text without triggering our own listener.</summary>
    public void SetTextSilently(string text)
    {
        _ignoreNext = text;
        Clipboard.SetText(text);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_CLIPBOARDUPDATE) return IntPtr.Zero;
        try
        {
            if (!Clipboard.ContainsText()) return IntPtr.Zero;
            var text = Clipboard.GetText().Trim();
            if (text == _ignoreNext)
            {
                _ignoreNext = null;
                return IntPtr.Zero;
            }
            TextCopied?.Invoke(text);
        }
        catch (COMException)
        {
            // Another process holds the clipboard open; skip this update.
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
