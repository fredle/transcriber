using System.Windows;
using System.Windows.Input;

namespace MeetingTranscriber;

public partial class InputDialog : Window
{
    public string InputText => InputBox.Text.Trim();

    public InputDialog(string title, string prompt, string defaultText = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = defaultText;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
    }

    public static string? Prompt(Window owner, string title, string prompt, string defaultText = "")
    {
        var dialog = new InputDialog(title, prompt, defaultText) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.InputText : null;
    }
}
