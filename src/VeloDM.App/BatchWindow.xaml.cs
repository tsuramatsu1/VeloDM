using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace VeloDM.App;

public partial class BatchWindow : Window
{
    public IReadOnlyList<string> Urls { get; private set; } = [];
    public bool StartQueue => StartQueueBox.IsChecked == true;

    public BatchWindow()
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        Loaded += (_, _) => UrlsBox.Focus();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var lines = UrlsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var valid = lines.Where(App.IsHttpUrl).Distinct().ToList();
        int invalid = lines.Length - lines.Count(App.IsHttpUrl);
        if (valid.Count == 0)
        {
            MessageBox.Show(this, "No valid http:// or https:// addresses found.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (invalid > 0 && MessageBox.Show(this, $"{invalid} line(s) are not valid addresses and will be skipped. Continue?",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        Urls = valid;
        DialogResult = true;
    }
}
