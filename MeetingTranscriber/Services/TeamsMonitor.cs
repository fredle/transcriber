using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
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

    // Trailing relationship label Teams appends to a tile's accessible name
    // for contacts outside the org, e.g. "Tyler Cloherty External unfamiliar".
    private static readonly Regex ExternalSuffix = new(@"\s+External(\s+unfamiliar)?$", RegexOptions.Compiled);

    /// <summary>
    /// Best-effort roster of remote participants, read from the Teams meeting
    /// window's accessibility tree rather than OCR/screenshots - this also
    /// surfaces participants who are paginated out of the visible video
    /// gallery. Each participant tile is exposed as a MenuItem whose Name is
    /// "&lt;name&gt;[ External unfamiliar], &lt;state flags...&gt;"; the self
    /// tile (an Image, not a MenuItem) and small relationship-label sub
    /// elements (Groups) are excluded by the ControlType filter alone.
    /// Returns an empty list if Teams isn't found or has no roster yet -
    /// including while its accessibility tree is still spinning up, which can
    /// take a few seconds after the window first appears.
    /// </summary>
    public static List<string> GetParticipants()
    {
        var names = new List<string>();
        try
        {
            var hwnd = GetMeetingWindowHandle();
            if (hwnd == IntPtr.Zero) return names;

            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return names;

            var tiles = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));

            foreach (AutomationElement tile in tiles)
            {
                var raw = tile.Current.Name;
                if (string.IsNullOrWhiteSpace(raw)) continue;

                // A screen-share tile is also a MenuItem, named "Content
                // shared by <name>" - it duplicates that person's own tile
                // rather than naming a distinct attendee, so skip it.
                if (raw.StartsWith("Content shared by ", StringComparison.OrdinalIgnoreCase)) continue;

                var name = raw.Split(',')[0].Trim();
                name = ExternalSuffix.Replace(name, "").Trim();

                // Teams surfaces a "System" pseudo-tile (e.g. recording/consent
                // notices) as a MenuItem too, and it flickers in and out rather
                // than staying put or leaving cleanly - not a real attendee.
                if (name.Length > 0 && !string.Equals(name, "System", StringComparison.OrdinalIgnoreCase))
                    names.Add(name);
            }
        }
        catch (Exception)
        {
            // Best-effort: report nothing found rather than throw.
        }
        return names.Distinct().ToList();
    }

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
