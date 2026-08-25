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

    /// <summary>
    /// True when Teams has an <em>active</em> WASAPI audio session.
    ///
    /// Window titles cannot tell a call from an open chat - both clients title
    /// chat windows exactly like meeting windows - but a chat never opens an
    /// audio stream while a live call always does, so the audio session is the
    /// reliable signal.
    /// </summary>
    public static bool IsInCall()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
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
                            return true;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Audio stack unavailable - report "not in a call" rather than throw.
        }
        return false;
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
    public static string? GetMeetingTitle()
    {
        var candidates = new List<string>();
        foreach (var title in EnumerateWindowTitles())
        {
            var lower = title.ToLowerInvariant();
            if (!lower.Contains("microsoft teams")) continue;

            foreach (var suffix in new[] { " | microsoft teams", " - microsoft teams" })
            {
                if (lower.EndsWith(suffix, StringComparison.Ordinal))
                {
                    var name = title[..^suffix.Length].Trim();
                    if (name.Length > 0) candidates.Add(name);
                    break;
                }
            }
        }

        // Prefer something that looks like a meeting over a chat window.
        foreach (var c in candidates)
        {
            var lower = c.ToLowerInvariant();
            if (lower.Contains("meeting") || lower.Contains("call")) return c;
        }
        foreach (var c in candidates)
        {
            if (!c.StartsWith("Chat |", StringComparison.OrdinalIgnoreCase)) return c;
        }
        return candidates.Count > 0 ? candidates[0] : null;
    }

    private static IEnumerable<string> EnumerateWindowTitles()
    {
        var titles = new List<string>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var length = GetWindowTextLength(hwnd);
            if (length == 0) return true;
            var sb = new StringBuilder(length + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            titles.Add(sb.ToString());
            return true;
        }, IntPtr.Zero);
        return titles;
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
