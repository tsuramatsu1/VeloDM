using System.Windows;

namespace VeloDM.App;

/// <summary>Asks for one line of text.</summary>
public partial class InputDialog : Window
{
    public string Value => ValueBox.Text.Trim();

    public InputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initial;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
