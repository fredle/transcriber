using System.Windows;

namespace MeetingTranscriber;

public partial class WhatsNewDialog : Window
{
    public WhatsNewDialog(Window owner, string version, string notes)
    {
        InitializeComponent();
        Owner = owner;
        Title = $"What's new in Teeline v{version}";
        HeaderText.Text = $"What's new in Teeline v{version}";
        NotesText.Text = notes.Trim().Length > 0 ? notes.Trim() : "No release notes for this version.";
    }

    private void OnGotIt(object sender, RoutedEventArgs e) => Close();
}
