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
    /// the form "&lt;name&gt; | Microsoft Teams". Null while the only Teams
    /// windows on screen are shell pages (Calendar, Chat, ...) rather than a
    /// meeting, so bringing the main window to the front never reads as a
    /// differently-named meeting.
    /// </summary>
    public static string? GetMeetingTitle()
    {
        var window = GetBestMeetingWindow();
        return window is { IsShell: false } ? window.Value.Name : null;
    }

    /// <summary>
    /// Window handle of the same best-guess meeting window, for
    /// screenshotting. IntPtr.Zero if none is found. Unlike
    /// <see cref="GetMeetingTitle"/> this falls back to the Teams shell
    /// window when no meeting window can be identified - a screenshot of the
    /// wrong Teams window is a minor annoyance, whereas a shell page's title
    /// would look like a whole new meeting.
    /// </summary>
    public static IntPtr GetMeetingWindowHandle() => GetBestMeetingWindow()?.Hwnd ?? IntPtr.Zero;

    /// <summary>
    /// Forget which window was locked onto as "the meeting", so the next
    /// call picks afresh. Called when a call ends: the next meeting may well
    /// run in a different window.
    /// </summary>
    public static void ResetMeetingWindow()
    {
        lock (StickyLock) _stickyHwnd = IntPtr.Zero;
    }

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


    // Pages of the Teams shell - its main window, titled
    // "<page> | <account> | Microsoft Teams" - as opposed to a meeting.
    // Meetings run in their own window, so a shell page is never the
    // meeting: without this, simply clicking back to the main Teams window
    // put it at the top of the Z-order (which is the order EnumWindows
    // reports) and its title read as a brand-new meeting, splitting the
    // recording in two.
    private static readonly HashSet<string> ShellPages = new(StringComparer.OrdinalIgnoreCase)
    {
        "activity", "apps", "calendar", "calls", "chat", "chats", "communities",
        "files", "help", "microsoft teams", "onenote", "planner", "search",
        "settings", "store", "tasks", "teams", "viva",
    };

    // The window last identified as the meeting. Held on to so that whatever
    // else comes and goes in front of it, the meeting stays put for the life
    // of the call. Guarded because the UI timer and the recording's title
    // watcher both poll this class from their own threads.
    private static readonly object StickyLock = new();
    private static IntPtr _stickyHwnd = IntPtr.Zero;

    /// <summary>
    /// The Teams window taken to be the current meeting, and whether it is
    /// really just a shell page (i.e. no meeting window could be found).
    ///
    /// Sticky: once a window has been identified as the meeting it keeps that
    /// role for as long as it is still around and still named like a meeting,
    /// regardless of which Teams window happens to be in front. A genuine
    /// move to another meeting still comes through, either as a new title on
    /// that same window or - if it closed - as a fresh pick.
    /// </summary>
    private static (IntPtr Hwnd, string Name, bool IsShell)? GetBestMeetingWindow()
    {
        var candidates = GetMeetingWindowCandidates();
        if (candidates.Count == 0)
        {
            lock (StickyLock) _stickyHwnd = IntPtr.Zero;
            return null;
        }

        lock (StickyLock)
        {
            if (_stickyHwnd != IntPtr.Zero)
            {
                foreach (var c in candidates)
                {
                    if (c.Hwnd != _stickyHwnd) continue;
                    // Navigating the window we locked onto away to a shell
                    // page means the meeting isn't there any more - fall
                    // through and pick again (the compact-view window that
                    // Teams leaves behind is the usual answer).
                    if (IsShellPage(c.Name)) break;
                    return (c.Hwnd, c.Name, false);
                }
            }

            var best = PickMeetingWindow(candidates);
            _stickyHwnd = best.IsShell ? IntPtr.Zero : best.Hwnd;
            return best;
        }
    }

    private static (IntPtr Hwnd, string Name, bool IsShell) PickMeetingWindow(
        List<(IntPtr Hwnd, string Name)> candidates)
    {
        var meetingWindows = candidates.Where(c => !IsShellPage(c.Name)).ToList();
        if (meetingWindows.Count == 0)
            return (candidates[0].Hwnd, candidates[0].Name, true);

        // Prefer a window that says outright that it is a meeting or a call.
        foreach (var c in meetingWindows)
        {
            var lower = c.Name.ToLowerInvariant();
            if (lower.Contains("meeting") || lower.Contains("call")) return (c.Hwnd, c.Name, false);
        }
        return (meetingWindows[0].Hwnd, meetingWindows[0].Name, false);
    }

    /// <summary>
    /// True for a title whose leading segment names a page of the Teams
    /// shell, e.g. "Calendar | (External)" or "Chat | Contoso".
    /// </summary>
    private static bool IsShellPage(string name)
    {
        var firstSegment = name.Split('|')[0].Trim();
        return ShellPages.Contains(firstSegment);
    }

    private static List<(IntPtr Hwnd, string Name)> GetMeetingWindowCandidates()
    {
        var candidates = new List<(IntPtr Hwnd, string Name)>();
        foreach (var (hwnd, title) in EnumerateWindows())
        {
            var lower = title.ToLowerInvariant();
            if (!lower.Contains("microsoft teams")) continue;
            // Rules out a browser tab or Outlook window that merely mentions
            // Teams in its title.
            if (!IsTeamsWindow(hwnd)) continue;

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

    private static bool IsTeamsWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return IsTeamsProcess((int)pid);
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

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
