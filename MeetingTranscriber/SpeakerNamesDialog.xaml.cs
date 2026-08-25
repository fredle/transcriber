using System.Collections.Generic;
using System.Windows;

namespace MeetingTranscriber;

public sealed class SpeakerNameRow
{
    public required string Key { get; init; }
    public required string Label { get; init; }   // default label shown alongside the editable name, e.g. "Speaker A"
    public string Name { get; set; } = "";
}

public partial class SpeakerNamesDialog : Window
{
    private readonly List<SpeakerNameRow> _rows;

    /// <summary>Names keyed by TranscriptLine.SpeakerKey, populated on Save. Blank entries are omitted.</summary>
    public Dictionary<string, string> Result { get; } = new();

    public SpeakerNamesDialog(Window owner, List<SpeakerNameRow> rows)
    {
        InitializeComponent();
        Owner = owner;
        _rows = rows;
        RowsList.ItemsSource = rows;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
            if (!string.IsNullOrWhiteSpace(row.Name))
                Result[row.Key] = row.Name.Trim();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
