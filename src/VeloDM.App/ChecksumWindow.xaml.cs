using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using VeloDM.Core;

namespace VeloDM.App;

/// <summary>Computes a finished file's checksum and compares it with the one the user pastes in.</summary>
public partial class ChecksumWindow : Window
{
    private readonly string _path;
    private CancellationTokenSource? _cts;
    private string? _actual;
    private bool _suppress;

    public ChecksumWindow(string path)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _path = path;
        FileText.Text = Path.GetFileName(path);
        FileText.ToolTip = path;
        _suppress = true;
        AlgorithmBox.ItemsSource = Checksums.Algorithms;
        AlgorithmBox.SelectedItem = "SHA-256";
        _suppress = false;
        Loaded += (_, _) => _ = ComputeAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    private async Task ComputeAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var algorithm = (string)AlgorithmBox.SelectedItem;
        _actual = null;
        ActualBox.Text = "Calculating…";
        CopyButton.IsEnabled = false;
        Progress.Value = 0;
        ShowResult();
        try
        {
            var progress = new Progress<double>(p => { if (!cts.IsCancellationRequested) Progress.Value = p; });
            var sum = await Task.Run(() => Checksums.ComputeAsync(_path, algorithm, progress, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            _actual = sum;
            ActualBox.Text = sum;
            Progress.Value = 100;
            CopyButton.IsEnabled = true;
            ShowResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActualBox.Text = "Could not read the file: " + ex.Message;
        }
    }

    private void ShowResult()
    {
        var expected = Checksums.Normalize(ExpectedBox.Text);
        if (expected.Length == 0 || _actual is null)
        {
            ResultText.Text = "";
            return;
        }
        bool match = Checksums.Matches(_actual, expected);
        ResultText.Text = match
            ? "✔ The checksums match: the file is complete and unaltered."
            : "✖ The checksums do NOT match: the file is damaged, incomplete or not the file the site published.";
        ResultText.Foreground = (Brush)FindResource(match ? "SuccessBrush" : "ErrorBrush");
    }

    private void ExpectedBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // The length of a pasted checksum tells which kind it is.
        if (Checksums.AlgorithmFor(ExpectedBox.Text) is { } algorithm && !Equals(AlgorithmBox.SelectedItem, algorithm))
            AlgorithmBox.SelectedItem = algorithm; // recalculates
        else
            ShowResult();
    }

    private void AlgorithmBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_suppress && IsLoaded) _ = ComputeAsync();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_actual is null) return;
        try { Clipboard.SetText(_actual); }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard busy */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
