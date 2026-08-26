using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using MeetingTranscriber.Services;

namespace MeetingTranscriber;

public partial class MergeMeetingsDialog : Window
{
    public string ResultTitle => TitleBox.Text.Trim();

    public MergeMeetingsDialog(IReadOnlyList<Meeting> meetings, string defaultTitle)
    {
        InitializeComponent();
        HeaderText.Text = $"Merge {meetings.Count} meetings into one:";
        MeetingsList.ItemsSource = meetings.Select(m => $"- {m.Title} ({m.When})").ToList();
        TitleBox.Text = defaultTitle;
        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    private void OnMerge(object sender, RoutedEventArgs e) => TryAccept();

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TryAccept();
    }

    private void TryAccept()
    {
        if (ResultTitle.Length == 0)
        {
            TitleBox.Focus();
            return;
        }
        DialogResult = true;
    }

    public static string? Prompt(Window owner, IReadOnlyList<Meeting> meetings, string defaultTitle)
    {
        var dialog = new MergeMeetingsDialog(meetings, defaultTitle) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.ResultTitle : null;
    }
}
