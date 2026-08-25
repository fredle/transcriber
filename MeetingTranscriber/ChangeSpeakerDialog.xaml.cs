using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MeetingTranscriber;

public sealed class SpeakerOption
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public bool IsNew { get; init; }
}

public partial class ChangeSpeakerDialog : Window
{
    /// <summary>The chosen existing speaker's key, or null when a new speaker was named instead.</summary>
    public string? ResultKey { get; private set; }

    /// <summary>Set only when the user named a brand-new speaker.</summary>
    public string? NewSpeakerName { get; private set; }

    public ChangeSpeakerDialog(Window owner, List<SpeakerOption> existing)
    {
        InitializeComponent();
        Owner = owner;

        var options = new List<SpeakerOption>(existing)
        {
            new() { Key = "", DisplayName = "+ New speaker...", IsNew = true },
        };
        OptionsList.ItemsSource = options;
    }

    private void OnOptionSelected(object sender, SelectionChangedEventArgs e)
    {
        var option = OptionsList.SelectedItem as SpeakerOption;
        NewNameBox.Visibility = option is { IsNew: true } ? Visibility.Visible : Visibility.Collapsed;
        OkButton.IsEnabled = option != null;
        if (option is { IsNew: true }) NewNameBox.Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TryAccept();
    }

    private void OnOk(object sender, RoutedEventArgs e) => TryAccept();

    private void TryAccept()
    {
        if (OptionsList.SelectedItem is not SpeakerOption option) return;

        if (option.IsNew)
        {
            var name = NewNameBox.Text.Trim();
            if (name.Length == 0)
            {
                NewNameBox.Focus();
                return;
            }
            NewSpeakerName = name;
            ResultKey = null;
        }
        else
        {
            ResultKey = option.Key;
        }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
