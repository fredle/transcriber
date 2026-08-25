using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace MeetingTranscriber.Services;

/// <summary>
/// A small, self-dismissing popup used in place of NotifyIcon.ShowBalloonTip.
/// Windows 10/11 render a balloon tip as a full Action Center toast and
/// ignore the timeout passed to ShowBalloonTip, so the size and duration of
/// that notification could not be controlled. This one is entirely our own:
/// compact, themed like the rest of the app, and closes itself.
/// </summary>
public sealed class Toast : Window
{
    private const int VisibleMs = 2500; // half of the 5s previously asked of ShowBalloonTip
    private const int FadeMs = 150;

    public Toast(string title, string message, Action? onClick)
    {
        Width = 280;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Opacity = 0;

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Foreground = Themed("Fg"),
        });
        stack.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 11,
            Foreground = Themed("Dim"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        });

        Content = new Border
        {
            Background = Themed("Panel"),
            BorderBrush = Themed("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Effect = (Effect)Application.Current.FindResource("PopupShadow"),
            Child = stack,
        };

        MouseLeftButtonUp += (_, _) =>
        {
            onClick?.Invoke();
            Close();
        };

        Loaded += (_, _) => PositionAboveTray();
    }

    private static Brush Themed(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>Bottom-right of the work area, clear of the taskbar and tray icons.</summary>
    private void PositionAboveTray()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Bottom - ActualHeight - 16;
    }

    /// <summary>Fade in, hold briefly, fade out, then close itself.</summary>
    public void Reveal()
    {
        Show();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeMs)));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(VisibleMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(FadeMs));
            fadeOut.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fadeOut);
        };
        timer.Start();
    }
}
