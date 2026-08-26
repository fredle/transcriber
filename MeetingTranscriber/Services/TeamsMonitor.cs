using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MeetingTranscriber.Services;

/// <summary>
/// Detects whether Teams is actually in a call, and what the meeting is called.
/// </summary>
public static class TeamsMonitor
{
    private static readonly string[] ProcessNames = { "ms-teams", "teams" };

    // Teams prefixes the real meeting name with these while the call window
    // is still settling (join dialog, then the floating compact-view strip).
    // Left in place, each transition reads as a brand-new meeting title and
    // spuriously splits the recording, so strip them down to the real name.
    private static readonly string[] TransitionalPrefixes =
    {
        "meeting join | ",
        "meeting compact view | ",
    };

    /// <summary>
    /// True when Teams has an <em>active</em> WASAPI audio session.
    ///
    /// Window titles cannot tell a call from an open chat - both clients title
    /// chat windows exactly like meeting windows - but a chat never opens an
    /// audio stream while a live call always does, so the audio session is the
    /// reliable signal.
    /// </summary>
    public static bool IsInCall() => GetActiveSpeakerDeviceId() != null;

    /// <summary>
    /// Id of the render (speaker) endpoint Teams currently has an active
    /// audio session on, or null when it has none open.
    /// </summary>
    public static string? GetActiveSpeakerDeviceId() => FindActiveDeviceId(DataFlow.Render);

    /// <summary>
    /// Id of the capture (microphone) endpoint Teams currently has an active
    /// audio session on, or null when it has none open.
    /// </summary>
    public static string? GetActiveMicDeviceId() => FindActiveDeviceId(DataFlow.Capture);

    /// <summary>
    /// Finds the endpoint of the given flow that Teams is actually streaming
    /// through right now. This reflects whatever device Teams itself has
    /// picked, which can differ from the Windows default communications
    /// device if it was chosen inside Teams' own audio settings.
    /// </summary>
    private static string? FindActiveDeviceId(DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.State != AudioSessionState.AudioSessionStateActive)
                            continue;
                        if (IsTeamsProcess((int)session.GetProcessID))
                            return device.ID;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Audio stack unavailable - report "nothing detected" rather than throw.
        }
        return null;
    }

    private static bool IsTeamsProcess(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            var name = process.ProcessName.ToLowerInvariant();
            return Array.IndexOf(ProcessNames, name) >= 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort meeting name scraped from Teams window titles, which take
    /// the form "&lt;name&gt; | Microsoft Teams".
    /// </summary>
    public static string? GetMeetingTitle() => GetBestMeetingWindow()?.Name;

    /// <summary>Window handle of the same best-guess meeting window, for screenshotting. IntPtr.Zero if none is found.</summary>
    public static IntPtr GetMeetingWindowHandle() => GetBestMeetingWindow()?.Hwnd ?? IntPtr.Zero;

    private static (IntPtr Hwnd, string Name)? GetBestMeetingWindow()
    {
        var candidates = GetMeetingWindowCandidates();

        // Prefer something that looks like a meeting over a chat window.
        foreach (var c in candidates)
        {
            var lower = c.Name.ToLowerInvariant();
            if (lower.Contains("meeting") || lower.Contains("call")) return c;
        }
        foreach (var c in candidates)
        {
            if (!c.Name.StartsWith("Chat |", StringComparison.OrdinalIgnoreCase)) return c;
        }
        return candidates.Count > 0 ? candidates[0] : null;
    }

    private static List<(IntPtr Hwnd, string Name)> GetMeetingWindowCandidates()
    {
        var candidates = new List<(IntPtr Hwnd, string Name)>();
        foreach (var (hwnd, title) in EnumerateWindows())
        {
            var lower = title.ToLowerInvariant();
            if (!lower.Contains("microsoft teams")) continue;

            foreach (var suffix in new[] { " | microsoft teams", " - microsoft teams" })
            {
                if (lower.EndsWith(suffix, StringComparison.Ordinal))
                {
                    var name = title[..^suffix.Length].Trim();
                    var nameLower = name.ToLowerInvariant();
                    foreach (var prefix in TransitionalPrefixes)
                    {
                        if (nameLower.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            name = name[prefix.Length..].Trim();
                            break;
                        }
                    }
                    if (name.Length > 0) candidates.Add((hwnd, name));
                    break;
                }
            }
        }
        return candidates;
    }

    private static IEnumerable<(IntPtr Hwnd, string Title)> EnumerateWindows()
    {
        var windows = new List<(IntPtr, string)>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var length = GetWindowTextLength(hwnd);
            if (length == 0) return true;
            var sb = new StringBuilder(length + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            windows.Add((hwnd, sb.ToString()));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}
