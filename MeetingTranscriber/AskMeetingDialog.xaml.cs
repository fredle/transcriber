using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MeetingTranscriber.Services;

namespace MeetingTranscriber;

public partial class AskMeetingDialog : Window
{
    private sealed class Turn : INotifyPropertyChanged
    {
        public required string Question { get; init; }

        private string _answer = "Thinking...";
        public string Answer
        {
            get => _answer;
            set { _answer = value; OnPropertyChanged(nameof(Answer)); }
        }

        private Brush _answerColor = Brushes.Gray;
        public Brush AnswerColor
        {
            get => _answerColor;
            set { _answerColor = value; OnPropertyChanged(nameof(AnswerColor)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly BackendClient _backend;
    private readonly string _meetingId;
    private readonly ObservableCollection<Turn> _turns = new();

    private static Brush Themed(string key) => (Brush)Application.Current.FindResource(key);

    public AskMeetingDialog(Window owner, BackendClient backend, string meetingId, string meetingTitle)
    {
        InitializeComponent();
        Owner = owner;
        _backend = backend;
        _meetingId = meetingId;

        Title = $"Ask about: {meetingTitle}";
        HeaderText.Text = $"Answers come from Claude, using \"{meetingTitle}\"'s synced transcript as context. " +
                           "Nothing here is saved with the meeting.";
        HistoryList.ItemsSource = _turns;
        _turns.CollectionChanged += (_, _) => EmptyHint.Visibility = _turns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        QuestionBox.Focus();
    }

    private void OnQuestionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnAskClick(sender, e);
    }

    private async void OnAskClick(object sender, RoutedEventArgs e)
    {
        var question = QuestionBox.Text.Trim();
        if (question.Length == 0 || !AskButton.IsEnabled) return;

        QuestionBox.Clear();
        QuestionBox.IsEnabled = false;
        AskButton.IsEnabled = false;

        var turn = new Turn { Question = question };
        _turns.Add(turn);
        HistoryScroll.ScrollToEnd();

        try
        {
            var answer = await _backend.AskAsync(_meetingId, question);
            turn.Answer = answer.Length == 0 ? "(no answer)" : answer;
            turn.AnswerColor = Themed("Fg");
        }
        catch (Exception ex)
        {
            turn.Answer = $"Couldn't get an answer: {ex.Message}";
            turn.AnswerColor = Themed("Danger");
        }
        finally
        {
            QuestionBox.IsEnabled = true;
            AskButton.IsEnabled = true;
            QuestionBox.Focus();
            HistoryScroll.ScrollToEnd();
        }
    }

}
