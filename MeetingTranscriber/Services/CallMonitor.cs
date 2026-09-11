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

public enum CallAppKind { NativeApp, Browser }

/// <summary>One process found to be actively on a call, and how to resolve its title.</summary>
public readonly record struct CallAppInfo(int Pid, string ProcessName, CallAppKind Kind);

/// <summary>
/// Detects whether any recognised calling app (Teams, and optionally Zoom,
/// Slack, or a browser tab) is actually in a call, and what the meeting is
/// called.
/// </summary>
public static class CallMonitor
{
    private static readonly string[] TeamsProcessNames = { "ms-teams", "teams" };
    private static readonly string[] ZoomProcessNames = { "zoom" };
    private static readonly string[] SlackProcessNames = { "slack" };
    private static readonly string[] BrowserProcessNames = { "chrome", "msedge", "firefox" };

    private static readonly (string ProcessName, string WindowClass)[] BrowserWindowClasses =
    {
        ("chrome", "Chrome_WidgetWin_1"),
        ("msedge", "Chrome_WidgetWin_1"),
        ("firefox", "MozillaWindowClass"),
    };

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
    /// Best-effort diagnostics from paths that can silently degrade (e.g. a
    /// browser tab whose mic-in-use accessibility signal couldn't be found).
    /// Callers can surface this in the app log; nothing subscribes by default.
    /// </summary>
    public static event Action<string>? DiagnosticLog;

    /// <summary>
    /// Process names that count as "on a call", by user choice: Teams is
    /// always included so existing behaviour never regresses; the rest only
    /// count once the user opts in to broader detection.
    /// </summary>
    public static HashSet<string> GetAllowedProcessNames(bool includeNonTeamsApps)
    {
        var names = new HashSet<string>(TeamsProcessNames, StringComparer.OrdinalIgnoreCase);
        if (includeNonTeamsApps)
        {
            foreach (var n in ZoomProcessNames) names.Add(n);
            foreach (var n in SlackProcessNames) names.Add(n);
            foreach (var n in BrowserProcessNames) names.Add(n);
        }
        return names;
    }

    /// <summary>
    /// Every allowed process currently holding an <em>active</em> WASAPI
    /// render (speaker) session, one entry per distinct process id.
    ///
    /// Window titles cannot tell a call from an open chat/tab - both can be
    /// titled just like a meeting - but a chat never opens an audio stream
    /// while a live call always does (even self-muted, since the app still
    /// renders remote audio), so the render session is the reliable signal
    /// for a dedicated calling app (Teams/Zoom/Slack never render audio for
    /// any other reason). A general-purpose browser is a different story: it
    /// opens and closes render sessions constantly for ordinary browsing
    /// (a notification sound, an autoplaying video, a random tab's audio) -
    /// confirmed live, this produced a real false positive (auto-recording
    /// triggered by an inbox tab). So a render session alone is never enough
    /// to call a browser "on a call"; it additionally requires a tab actually
    /// showing the microphone-in-use signal (see FindMicActiveTab).
    /// </summary>
    public static List<CallAppInfo> FindActiveCaptureApps(IReadOnlySet<string> allowedProcessNames)
    {
        var apps = new List<CallAppInfo>();
        var seenPids = new HashSet<int>();
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
                        if (session.State != AudioSessionState.AudioSessionStateActive) continue;
                        var pid = (int)session.GetProcessID;
                        if (pid <= 0 || !seenPids.Add(pid)) continue;

                        var name = TryGetProcessName(pid);
                        if (name == null || !allowedProcessNames.Contains(name)) continue;

                        var kind = Array.IndexOf(BrowserProcessNames, name) >= 0
                            ? CallAppKind.Browser : CallAppKind.NativeApp;
                        var app = new CallAppInfo(pid, name, kind);
                        if (kind == CallAppKind.Browser && FindMicActiveTab(app) == null) continue;
                        apps.Add(app);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Audio stack unavailable - report "nothing detected" rather than throw.
        }
        return apps;
    }

    /// <summary>
    /// Chooses which of possibly-several simultaneously active apps counts as
    /// "the" call: sticks with whichever one was picked last time, for as
    /// long as it is still active, rather than flapping between them.
    /// </summary>
    public static CallAppInfo? PickActiveApp(List<CallAppInfo> apps)
    {
        lock (StickyLock)
        {
            if (apps.Count == 0)
            {
                _stickyAppPid = 0;
                return null;
            }

            var chosen = apps.FirstOrDefault(a => a.Pid == _stickyAppPid, apps[0]);
            _stickyAppPid = chosen.Pid;
            return chosen;
        }
    }

    /// <summary>Id of the capture (microphone) endpoint the given app currently has an active audio session on, or null.</summary>
    public static string? GetMicDeviceId(CallAppInfo app) => FindDeviceIdForPid(DataFlow.Capture, app.Pid);

    /// <summary>Id of the render (speaker) endpoint the given app currently has an active audio session on, or null.</summary>
    public static string? GetSpeakerDeviceId(CallAppInfo app) => FindDeviceIdForPid(DataFlow.Render, app.Pid);

    private static string? FindDeviceIdForPid(DataFlow flow, int pid)
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
                        if (session.State != AudioSessionState.AudioSessionStateActive) continue;
                        if ((int)session.GetProcessID == pid) return device.ID;
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

    private static string? TryGetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.ToLowerInvariant();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort meeting name for the given app. For a native app this is
    /// scraped from its own window title (with Teams' known quirks cleaned
    /// up); for a browser it's the title of whichever tab appears to be
    /// using the microphone, falling back to the browser's plain window
    /// title if that can't be identified.
    /// </summary>
    public static string? GetMeetingTitle(CallAppInfo app)
    {
        if (app.Kind == CallAppKind.Browser)
            return GetBrowserTabTitle(app) ?? GetBrowserWindowTitleFallback(app);

        var window = GetBestMeetingWindow(app);
        return window is { IsShell: false } ? window.Value.Name : null;
    }

    /// <summary>
    /// Window handle of the same best-guess meeting window/app window, for
    /// screenshotting. IntPtr.Zero if none is found. For a browser this is
    /// the browser's top-level window, not the specific tab - screenshotting
    /// a single tab in isolation isn't meaningful anyway.
    /// </summary>
    public static IntPtr GetMeetingWindowHandle(CallAppInfo app)
    {
        if (app.Kind == CallAppKind.Browser)
            return GetBrowserMainWindow(app)?.Hwnd ?? IntPtr.Zero;

        return GetBestMeetingWindow(app)?.Hwnd ?? IntPtr.Zero;
    }

    /// <summary>
    /// Forget which window was locked onto as "the meeting", so the next
    /// call picks afresh. Called when a call ends: the next meeting may well
    /// run in a different window, or a different app entirely.
    /// </summary>
    public static void ResetMeetingWindow()
    {
        lock (StickyLock)
        {
            _sticky = (0, IntPtr.Zero);
            _stickyAppPid = 0;
        }
    }

    // Trailing relationship label Teams appends to a tile's accessible name
    // for contacts outside the org, e.g. "Tyler Cloherty External unfamiliar".
    private static readonly Regex ExternalSuffix = new(@"\s+External(\s+unfamiliar)?$", RegexOptions.Compiled);

    /// <summary>
    /// Best-effort roster of remote participants, read from the call app's
    /// own accessibility tree. Teams and Zoom each have their own bespoke
    /// walk (differently-shaped trees); anything else (Slack, browsers)
    /// returns an empty list rather than guessing.
    /// </summary>
    public static List<string> GetParticipants(CallAppInfo app)
    {
        if (Array.IndexOf(TeamsProcessNames, app.ProcessName) >= 0) return GetTeamsParticipants(app);
        if (Array.IndexOf(ZoomProcessNames, app.ProcessName) >= 0) return GetZoomParticipants(app);
        return new List<string>();
    }

    private static List<string> GetTeamsParticipants(CallAppInfo app)
    {
        var names = new List<string>();
        try
        {
            var hwnd = GetMeetingWindowHandle(app);
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

    // Marks the local user's own tile, e.g. "Freddie Leatham,(Host, me),
    // Computer audio unmuted,Video on" - confirmed live against Zoom.
    private static readonly Regex ZoomSelfMarker = new(@"\(.*\bme\b.*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Zoom exposes its participant panel as a List named "Participant
    /// list..." containing one ListItem per attendee, each named
    /// "&lt;name&gt;,&lt;role/state flags...&gt;" - confirmed live. Requires
    /// the panel to actually be open (it isn't by default), same constraint
    /// as reading Teams' roster from its window.
    /// </summary>
    private static List<string> GetZoomParticipants(CallAppInfo app)
    {
        var names = new List<string>();
        try
        {
            var hwnd = GetMeetingWindowHandle(app);
            if (hwnd == IntPtr.Zero) return names;

            var root = AutomationElement.FromHandle(hwnd);
            var list = root?.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List));
            if (list == null) return names;

            var tiles = list.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));

            foreach (AutomationElement tile in tiles)
            {
                var raw = tile.Current.Name;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (ZoomSelfMarker.IsMatch(raw)) continue;  // exclude self, matching Teams' "remote participants" scope

                var name = raw.Split(',')[0].Trim();
                if (name.Length > 0) names.Add(name);
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

    // The window/app last identified as the meeting, keyed by which process
    // it belongs to. Held on to so that whatever else comes and goes in
    // front of it, the meeting stays put for the life of the call, and so a
    // different app's call starting doesn't inherit a stale window. Guarded
    // because the UI timer and the recording's title watcher both poll this
    // class from their own threads.
    private static readonly object StickyLock = new();
    private static (int Pid, IntPtr Hwnd) _sticky;
    private static int _stickyAppPid;

    /// <summary>
    /// The window taken to be the current meeting for the given app, and
    /// whether it is really just a shell page (Teams only - i.e. no meeting
    /// window could be found).
    ///
    /// Sticky: once a window has been identified as the meeting it keeps that
    /// role for as long as it is still around and still named like a meeting,
    /// regardless of which window of that app happens to be in front.
    /// </summary>
    private static (IntPtr Hwnd, string Name, bool IsShell)? GetBestMeetingWindow(CallAppInfo app)
    {
        var candidates = GetMeetingWindowCandidates(app);
        if (candidates.Count == 0)
        {
            lock (StickyLock) _sticky = (0, IntPtr.Zero);
            return null;
        }

        lock (StickyLock)
        {
            if (_sticky.Pid == app.Pid && _sticky.Hwnd != IntPtr.Zero)
            {
                foreach (var c in candidates)
                {
                    if (c.Hwnd != _sticky.Hwnd) continue;
                    // Navigating the window we locked onto away to a shell
                    // page means the meeting isn't there any more - fall
                    // through and pick again (the compact-view window that
                    // Teams leaves behind is the usual answer).
                    if (IsShellPage(c.Name)) break;
                    return (c.Hwnd, c.Name, false);
                }
            }

            var best = PickMeetingWindow(candidates);
            _sticky = best.IsShell ? (0, IntPtr.Zero) : (app.Pid, best.Hwnd);
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

    /// <summary>
    /// Visible top-level windows belonging to the given app, cleaned up per
    /// its known title quirks. Matched by process <em>name</em> rather than
    /// the specific PID that owns the mic session: Teams' new client (and
    /// some other apps) spreads across several processes that share one
    /// name, and the one holding the audio session is often not the one that
    /// owns the meeting window - confirmed live, this is not hypothetical.
    /// </summary>
    private static List<(IntPtr Hwnd, string Name)> GetMeetingWindowCandidates(CallAppInfo app)
    {
        var candidates = new List<(IntPtr, string)>();
        var isTeams = Array.IndexOf(TeamsProcessNames, app.ProcessName) >= 0;

        foreach (var (hwnd, title) in EnumerateWindows())
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (TryGetProcessName((int)pid) != app.ProcessName) continue;

            if (!isTeams)
            {
                var trimmed = title.Trim();
                if (trimmed.Length > 0) candidates.Add((hwnd, trimmed));
                continue;
            }

            var cleaned = StripTeamsSuffix(title);
            if (cleaned != null) candidates.Add((hwnd, cleaned));
        }
        return candidates;
    }

    /// <summary>
    /// Teams windows/tabs take the form "&lt;name&gt; | Microsoft Teams";
    /// null if <paramref name="title"/> doesn't match that shape at all
    /// (i.e. isn't a Teams meeting title, transitional or otherwise).
    /// </summary>
    private static string? StripTeamsSuffix(string title)
    {
        var lower = title.ToLowerInvariant();
        foreach (var suffix in new[] { " | microsoft teams", " - microsoft teams" })
        {
            if (!lower.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var name = title[..^suffix.Length].Trim();

            // Teams-web can lead with a breadcrumb naming the page the
            // meeting was joined from, e.g. "Calendar | <meeting name>" -
            // confirmed live. Strip it the same way a bare shell-page
            // window title is recognised elsewhere.
            var segments = name.Split('|');
            if (segments.Length > 1 && ShellPages.Contains(segments[0].Trim()))
                name = string.Join('|', segments.Skip(1)).Trim();

            var nameLower = name.ToLowerInvariant();
            foreach (var prefix in TransitionalPrefixes)
            {
                if (nameLower.StartsWith(prefix, StringComparison.Ordinal))
                {
                    name = name[prefix.Length..].Trim();
                    break;
                }
            }
            return name.Length > 0 ? name : null;
        }
        return null;
    }

    // ── Browser tabs ────────────────────────────────────────────────────
    //
    // A browser's mic session is owned by a per-tab renderer process (site
    // isolation), which - unlike a native app's process - owns no top-level
    // window of its own, so the PID-based lookup above doesn't apply. This
    // instead finds the browser's main window by process name/window class,
    // then walks its tab strip via UI Automation looking for the tab whose
    // accessible Name mentions "Microphone" - confirmed live against Edge:
    // Chromium browsers don't expose mic/audio state as a separate
    // ItemStatus/HelpText property, they bake a state summary straight into
    // the tab's Name (see StripChromeTabStateSuffix). This is still an
    // undocumented implementation detail rather than a stable API, so it may
    // need adjusting as Chromium/Firefox change how they word it; if it
    // doesn't match, this quietly falls back to the browser's plain window
    // title rather than failing outright.

    private static (IntPtr Hwnd, string Title)? GetBrowserMainWindow(CallAppInfo app)
    {
        var expectedClass = BrowserWindowClasses
            .FirstOrDefault(b => b.ProcessName == app.ProcessName).WindowClass;
        if (expectedClass == null) return null;

        foreach (var (hwnd, title) in EnumerateWindows())
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (TryGetProcessName((int)pid) != app.ProcessName) continue;
            if (GetWindowClassName(hwnd) != expectedClass) continue;
            return (hwnd, title);
        }
        return null;
    }

    /// <summary>
    /// Chromium doesn't expose a tab's mic/audio state as a separate
    /// ItemStatus/HelpText property (confirmed live - both come back empty).
    /// Instead it appends a state summary straight onto the tab's accessible
    /// Name after an en dash, e.g. "&lt;page title&gt; – Microphone
    /// recording - High memory usage - 1.3 GB", alongside an optional
    /// leading "(N) " tab-group/pinned-count marker. So the Name itself is
    /// both the signal and (once that's stripped back off) the title.
    /// </summary>
    private static AutomationElement? FindMicActiveTab(CallAppInfo app)
    {
        try
        {
            var window = GetBrowserMainWindow(app);
            if (window == null) return null;

            var root = AutomationElement.FromHandle(window.Value.Hwnd);
            if (root == null) return null;

            var tabs = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

            foreach (AutomationElement tab in tabs)
            {
                var rawName = tab.Current.Name ?? "";
                if (rawName.Contains("microphone", StringComparison.OrdinalIgnoreCase)) return tab;
            }
        }
        catch (Exception)
        {
            // UI Automation over a browser's tab strip is inherently best-effort.
        }
        return null;
    }

    private static string? GetBrowserTabTitle(CallAppInfo app)
    {
        var tab = FindMicActiveTab(app);
        if (tab == null) return null;

        var pageTitle = StripChromeTabStateSuffix(tab.Current.Name ?? "");
        if (pageTitle.Length == 0) return null;
        return StripTeamsSuffix(pageTitle) ?? pageTitle;
    }

    /// <summary>
    /// False when a screenshot right now would silently capture the wrong
    /// thing: a browser only renders whichever tab is currently visible, so
    /// if the tab using the microphone isn't the selected one, a screenshot
    /// of the browser window shows unrelated content instead. Always true
    /// for a native app - once its window is found, that window is what's
    /// on screen, there's no separate "which tab" question.
    /// </summary>
    public static bool IsMeetingTabForeground(CallAppInfo app)
    {
        if (app.Kind != CallAppKind.Browser) return true;

        try
        {
            var tab = FindMicActiveTab(app);
            if (tab == null) return false;
            if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
                return ((SelectionItemPattern)pattern).Current.IsSelected;
        }
        catch (Exception)
        {
            // Best-effort: treat a failed read as "not visible" rather than
            // risk offering a screenshot that captures the wrong tab.
        }
        return false;
    }

    /// <summary>
    /// Drops a tab's leading "(N) " group/pinned-position marker and its
    /// trailing " – &lt;state summary&gt;" (memory usage, audio/mic
    /// state, ...), leaving just the page's own title.
    /// </summary>
    private static string StripChromeTabStateSuffix(string rawName)
    {
        var name = rawName;
        var groupPrefix = Regex.Match(name, @"^\(\d+\)\s+");
        if (groupPrefix.Success) name = name[groupPrefix.Length..];

        var dashIndex = name.IndexOf('–');
        if (dashIndex >= 0) name = name[..dashIndex];

        return name.Trim();
    }

    private static string? GetBrowserWindowTitleFallback(CallAppInfo app)
    {
        var window = GetBrowserMainWindow(app);
        if (window == null) return null;
        DiagnosticLog?.Invoke(
            $"Couldn't identify the mic-active tab in {app.ProcessName}; using its window title instead.");
        return window.Value.Title;
    }

    private static object? SafeGetProperty(AutomationElement element, AutomationProperty property)
    {
        try { return element.GetCurrentPropertyValue(property); }
        catch (Exception) { return null; }
    }

    private static string? GetWindowClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : null;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}
