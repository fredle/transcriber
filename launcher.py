"""
Transcriber Launcher UI
Select mic and loopback devices, then start/stop the transcriber.
"""
import sys
import os
import re
import json
import subprocess
import threading
import collections
import numpy as np
import customtkinter as ctk
import sounddevice as sd
import pyaudiowpatch as pyaudio_wp
import psutil
import win32gui
import win32process
from datetime import datetime
from tkinter import messagebox
import matplotlib
matplotlib.use("TkAgg")
from matplotlib.figure import Figure
from matplotlib.backends.backend_tkagg import FigureCanvasTkAgg

# ── Theme ────────────────────────────────────────────────────────────────────
ctk.set_appearance_mode("dark")
ctk.set_default_color_theme("blue")

TRANSCRIBER_SCRIPT = os.path.join(os.path.dirname(__file__), "transcriber.py")
PYTHON = sys.executable
APP_ICON = os.path.join(os.path.dirname(os.path.abspath(__file__)), "assets", "transcriber.ico")
RECORDINGS_DIR = os.path.dirname(os.path.abspath(__file__))

# Windows taskbar grouping: without an explicit AppUserModelID the taskbar
# inherits the Python interpreter's identity (and its icon), even though the
# window itself carries ours. Must be set before any window is created.
try:
    import ctypes
    ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID(
        "FreddieLeatham.MeetingTranscriber"
    )
except Exception:
    pass

# Chart constants
CHART_DURATION = 10    # seconds of history to display
CHART_HZ       = 20    # samples pushed to chart per second
CHART_POINTS   = CHART_DURATION * CHART_HZ   # 200 total points
CHART_GAIN     = 6.0   # amplitude scale (1.0 = full-scale 0 dBFS)

# Colours (dark-mode palette)
BG_DARK   = "#1e1e1e"
BG_AXES   = "#2b2b2b"
COL_MIC   = "#4ec9b0"   # teal
COL_SPK   = "#ce9178"   # orange
COL_GRID  = "#3a3a3a"

# Matches the "ME: ..." / "OTHER: ..." lines printed by transcriber.py
TRANSCRIPT_LINE_RE = re.compile(r"^(ME|OTHER):\s?(.*)$")


# ── Device enumeration ───────────────────────────────────────────────────────

def _get_enumerator():
    from pycaw.pycaw import IMMDeviceEnumerator
    from pycaw.constants import CLSID_MMDeviceEnumerator
    from comtypes import CLSCTX_ALL, CoCreateInstance
    return CoCreateInstance(CLSID_MMDeviceEnumerator, IMMDeviceEnumerator, CLSCTX_ALL)


def _endpoint_friendly_name(ep):
    from pycaw.pycaw import PROPERTYKEY
    from comtypes import GUID
    store = ep.OpenPropertyStore(0)
    pkey = PROPERTYKEY()
    pkey.fmtid = GUID('{a45c254e-df1c-4efd-8020-67d146a850e0}')
    pkey.pid = 14
    return store.GetValue(pkey).GetValue()


def _find_sd_id_for_name(name):
    """Find best sounddevice input device ID for a given endpoint name.
    Prefers MME > DirectSound > WASAPI — MME devices reliably report 1 channel
    which matches the transcriber's CHANNELS=1 setting."""
    best_id, best_priority = None, 99
    api_priority = {'MME': 0, 'Windows DirectSound': 1, 'Windows WASAPI': 2}
    for i, dev in enumerate(sd.query_devices()):
        if dev['max_input_channels'] == 0:
            continue
        dev_name = dev['name']
        if name.lower() not in dev_name.lower() and dev_name.lower() not in name.lower():
            continue
        api = sd.query_hostapis(dev['hostapi'])['name']
        p = api_priority.get(api, 3)
        if p < best_priority:
            best_priority, best_id = p, i
    return best_id


def get_mic_devices():
    """
    Enumerate unique capture endpoints via Windows Core Audio — same list Teams shows.
    Returns [(friendly_name, sounddevice_id), ...]
    """
    try:
        enumerator = _get_enumerator()
        col = enumerator.EnumAudioEndpoints(1, 0x1)   # eCapture, ACTIVE
        result = []
        for i in range(col.GetCount()):
            ep   = col.Item(i)
            name = _endpoint_friendly_name(ep)
            sid  = _find_sd_id_for_name(name)
            if sid is not None:
                result.append((name, sid))
        return result
    except Exception:
        seen, result = set(), []
        for i, dev in enumerate(sd.query_devices()):
            if dev['max_input_channels'] == 0:
                continue
            if sd.query_hostapis(dev['hostapi'])['name'] != 'MME':
                continue
            if dev['name'] not in seen:
                seen.add(dev['name'])
                result.append((dev['name'], i))
        return result


def get_loopback_devices():
    """
    Enumerate unique render endpoints via Windows Core Audio — same list Teams shows.
    Maps each to its WASAPI loopback device in pyaudiowpatch.
    Returns [(friendly_name, pyaudiowpatch_id, channels, rate), ...]
    """
    p = pyaudio_wp.PyAudio()
    try:
        paw_loopbacks = [
            (i, p.get_device_info_by_index(i))
            for i in range(p.get_device_count())
            if p.get_device_info_by_index(i).get('isLoopbackDevice')
        ]
    finally:
        p.terminate()

    try:
        enumerator = _get_enumerator()
        col = enumerator.EnumAudioEndpoints(0, 0x1)   # eRender, ACTIVE
        result = []
        for i in range(col.GetCount()):
            ep   = col.Item(i)
            name = _endpoint_friendly_name(ep)
            for paw_id, d in paw_loopbacks:
                if name.lower() in d['name'].lower():
                    result.append((name, paw_id, int(d['maxInputChannels']), int(d['defaultSampleRate'])))
                    break
        return result
    except Exception:
        return [(d['name'], paw_id, int(d['maxInputChannels']), int(d['defaultSampleRate']))
                for paw_id, d in paw_loopbacks]


_TEAMS_PROCESS_NAMES = {"ms-teams.exe", "teams.exe"}


def get_teams_call_state():
    """
    Windows-only check for whether Teams currently has an active call/meeting.

    Window titles can't tell a call apart from an open chat — both the new
    and classic Teams clients format chat windows the same way as call
    windows (e.g. "Hugo Pereira | Microsoft Teams" is a 1:1 chat, not a
    call, but is indistinguishable by title from "Meeting with X |
    Microsoft Teams"). Instead, check whether the Teams process has an
    *active* WASAPI audio session — a chat window never opens an audio
    stream, but a live call always does (mic capture and/or speaker
    render), so this is a much more reliable signal than title text.

    Returns (in_call: bool, meeting_title: str | None). meeting_title is a
    best-effort label only (from window titles) and does not affect in_call.
    """
    from pycaw.pycaw import AudioUtilities

    try:
        sessions = AudioUtilities.GetAllSessions()
    except Exception:
        return False, None

    in_call = False
    for session in sessions:
        proc = session.Process
        if proc is None:
            continue
        try:
            proc_name = proc.name().lower()
        except Exception:
            continue
        if proc_name in _TEAMS_PROCESS_NAMES and session.State == 1:  # AudioSessionStateActive
            in_call = True
            break

    if not in_call:
        return False, None

    return True, _guess_teams_call_title()


def _guess_teams_call_title():
    """Best-effort label for the active call, scraped from Teams window
    titles. Purely cosmetic — prefers a title that looks like a meeting,
    then falls back to any non-chat-looking title, then anything."""
    candidates = []

    def _enum_callback(hwnd, _):
        if not win32gui.IsWindowVisible(hwnd):
            return
        title = win32gui.GetWindowText(hwnd)
        if not title:
            return
        try:
            _, pid = win32process.GetWindowThreadProcessId(hwnd)
            proc_name = psutil.Process(pid).name().lower()
        except Exception:
            return
        if proc_name not in _TEAMS_PROCESS_NAMES:
            return
        lower = title.lower()
        for sep in (" | microsoft teams", " - microsoft teams"):
            if lower.endswith(sep):
                name = title[: -len(sep)].strip()
                if name:
                    candidates.append(name)
                return

    try:
        win32gui.EnumWindows(_enum_callback, None)
    except Exception:
        return None

    for c in candidates:
        if "meeting" in c.lower() or "call" in c.lower():
            return c
    for c in candidates:
        if not c.lower().startswith("chat |"):
            return c
    return candidates[0] if candidates else None


def detect_teams_defaults():
    """
    Return (mic_name, spk_name) for the Windows Communications devices.
    Returns (None, None) on failure.
    """
    try:
        from pycaw.pycaw import EDataFlow, ERole
        enumerator = _get_enumerator()
        mic_name = _endpoint_friendly_name(
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture.value, ERole.eCommunications.value)
        )
        spk_name = _endpoint_friendly_name(
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender.value, ERole.eCommunications.value)
        )
        return mic_name, spk_name
    except Exception:
        return None, None


# ── Meeting history ──────────────────────────────────────────────────────────

MEETING_LIST_LIMIT = 40


def _parse_session_started(meta, folder_name):
    """Prefer the metadata's session_start; fall back to parsing the folder
    name (recording_YYYYMMDD_HHMMSS) so folders whose transcript is missing
    or truncated still sort and display correctly."""
    raw = (meta or {}).get("session_start")
    if raw:
        try:
            return datetime.fromisoformat(raw)
        except ValueError:
            pass
    stamp = folder_name[len("recording_"):]
    try:
        return datetime.strptime(stamp, "%Y%m%d_%H%M%S")
    except ValueError:
        return None


def _read_session_summary(folder_path):
    """Return (metadata_dict, turn_count) from a recording's transcriptions.jsonl.
    Returns ({}, 0) when the file is absent or unreadable — a session that
    crashed before writing still shows up in the list, just without detail."""
    jsonl = os.path.join(folder_path, "transcriptions.jsonl")
    if not os.path.isfile(jsonl):
        return {}, 0

    meta, turns = {}, 0
    try:
        with open(jsonl, encoding="utf-8") as f:
            for i, line in enumerate(f):
                line = line.strip()
                if not line:
                    continue
                if i == 0:
                    try:
                        first = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    if first.get("type") == "session_metadata":
                        meta = first
                    else:
                        turns += 1   # older file with no metadata header
                else:
                    turns += 1
    except OSError:
        return meta, turns
    return meta, turns


def get_recent_meetings(limit=MEETING_LIST_LIMIT):
    """List past recording sessions, newest first, for the Recent Meetings panel."""
    try:
        entries = os.listdir(RECORDINGS_DIR)
    except OSError:
        return []

    meetings = []
    for name in entries:
        if not name.startswith("recording_"):
            continue
        path = os.path.join(RECORDINGS_DIR, name)
        if not os.path.isdir(path):
            continue
        meta, turns = _read_session_summary(path)
        meetings.append({
            "folder": name,
            "path": path,
            "title": (meta.get("meeting_title") or "").strip() or "Untitled meeting",
            "started": _parse_session_started(meta, name),
            # sessions recorded before the engine switch predate this field
            "engine": meta.get("engine", "whisper"),
            "turns": turns,
        })

    meetings.sort(key=lambda m: m["started"] or datetime.min, reverse=True)
    return meetings[:limit]


def move_to_recycle_bin(path):
    """Delete a file or folder to the Recycle Bin rather than permanently, so
    a mistaken delete is recoverable from Explorer.
    Returns (ok, error_message)."""
    try:
        from win32com.shell import shell, shellcon
    except ImportError as e:
        return False, f"pywin32 unavailable ({e})"
    try:
        result, aborted = shell.SHFileOperation((
            0, shellcon.FO_DELETE, os.path.abspath(path), None,
            shellcon.FOF_ALLOWUNDO | shellcon.FOF_NOCONFIRMATION | shellcon.FOF_SILENT,
            None, None,
        ))
    except Exception as e:
        return False, str(e)
    if aborted:
        return False, "cancelled by the shell"
    if result != 0:
        return False, f"shell error code {result}"
    return True, None


def format_meeting_when(started):
    """Compact, human relative timestamp for a meeting row."""
    if started is None:
        return "unknown date"
    today = datetime.now().date()
    delta = (today - started.date()).days
    if delta == 0:
        return f"Today {started:%H:%M}"
    if delta == 1:
        return f"Yesterday {started:%H:%M}"
    if started.year == datetime.now().year:
        return f"{started:%d %b %H:%M}"
    return f"{started:%d %b %Y}"


# ── Audio level monitor ───────────────────────────────────────────────────────

class AudioMonitor:
    """
    Continuously captures audio from mic (sounddevice) and speaker (pyaudiowpatch
    loopback) and accumulates RMS levels for the chart.
    Call tick() at CHART_HZ to push one sample into the history deques.
    """

    def __init__(self):
        self._lock = threading.Lock()
        zeros = [0.0] * CHART_POINTS
        self._mic_history = collections.deque(zeros, maxlen=CHART_POINTS)
        self._spk_history = collections.deque(zeros, maxlen=CHART_POINTS)
        self._mic_acc: list[float] = []
        self._spk_acc: list[float] = []

        self._mic_stream = None
        self._spk_stop   = threading.Event()
        self._spk_thread = None

    # ── mic ───────────────────────────────────────────────────────────────────

    def start_mic(self, device_id: int):
        self._stop_mic()
        def _cb(indata, frames, time_info, status):
            arr = indata.astype(np.float32).ravel()
            rms = float(np.sqrt(np.mean(arr ** 2))) / 32768.0
            with self._lock:
                self._mic_acc.append(rms)
        try:
            self._mic_stream = sd.InputStream(
                device=device_id, channels=1, samplerate=48000,
                dtype=np.int16, blocksize=1024, callback=_cb
            )
            self._mic_stream.start()
        except Exception as e:
            print(f"[monitor] Mic open failed ({e})")
            self._mic_stream = None

    def _stop_mic(self):
        if self._mic_stream is not None:
            try:
                self._mic_stream.stop()
                self._mic_stream.close()
            except Exception:
                pass
            self._mic_stream = None

    # ── speaker loopback ──────────────────────────────────────────────────────

    def start_speaker(self, device_id: int, channels: int, rate: int):
        # Signal existing thread to stop and wait briefly
        self._spk_stop.set()
        if self._spk_thread is not None:
            self._spk_thread.join(timeout=1.5)
        self._spk_stop.clear()

        stop_event = self._spk_stop   # capture ref for closure

        def _run():
            p = pyaudio_wp.PyAudio()
            try:
                stream = p.open(
                    format=pyaudio_wp.paInt16, channels=channels,
                    rate=rate, input=True, input_device_index=device_id,
                    frames_per_buffer=1024
                )
                while not stop_event.is_set():
                    try:
                        data = stream.read(1024, exception_on_overflow=False)
                    except Exception:
                        continue
                    arr = np.frombuffer(data, dtype=np.int16).astype(np.float32)
                    rms = float(np.sqrt(np.mean(arr ** 2))) / 32768.0
                    with self._lock:
                        self._spk_acc.append(rms)
                stream.stop_stream()
                stream.close()
            except Exception as e:
                print(f"[monitor] Speaker open failed ({e})")
            finally:
                p.terminate()

        self._spk_thread = threading.Thread(target=_run, daemon=True)
        self._spk_thread.start()

    # ── tick / read ───────────────────────────────────────────────────────────

    def tick(self):
        """Drain accumulators and push one RMS sample into each history deque."""
        with self._lock:
            mic_rms = float(np.mean(self._mic_acc)) if self._mic_acc else 0.0
            spk_rms = float(np.mean(self._spk_acc)) if self._spk_acc else 0.0
            self._mic_acc.clear()
            self._spk_acc.clear()
        self._mic_history.append(min(mic_rms * CHART_GAIN, 1.0))
        self._spk_history.append(min(spk_rms * CHART_GAIN, 1.0))

    def get_histories(self):
        return list(self._mic_history), list(self._spk_history)

    def stop(self):
        self._stop_mic()
        self._spk_stop.set()


# ── Main UI ───────────────────────────────────────────────────────────────────

class LauncherApp(ctk.CTk):
    TAB_RECORD = "Record"
    TAB_MEETINGS = "Recent Meetings"

    def __init__(self):
        super().__init__()
        self.title("Meeting Transcriber")
        self.geometry("1400x960")
        self.minsize(1100, 700)
        self.resizable(True, True)
        if os.path.exists(APP_ICON):
            self.iconbitmap(APP_ICON)
            self.after(200, self._reapply_icon)

        self._proc              = None   # long-lived transcriber standby/serving process
        self._reader_thread     = None
        self._monitor           = AudioMonitor()
        self._recording_active  = False
        self._model_ready       = False
        self._spawned_model     = None   # model size the running process was started with
        self._spawned_engine    = None   # "whisper" or "assemblyai"
        self._selected_meeting  = None   # meeting shown in the history viewer
        self._meeting_jsonl     = None   # transcript file backing that viewer
        self._row_source_lines  = {}     # viewer line -> .jsonl line index
        self._selected_rows     = set()  # viewer lines picked for deletion
        self._anchor_row        = None   # shift-click range anchor

        self._model_var    = ctk.StringVar(value="base")
        self._language_var = ctk.StringVar(value="Auto-detect")
        self._engine_var   = ctk.StringVar(value="Whisper (local)")

        self._mic_devices      = []   # [(name, sd_id), ...]
        self._loopback_devices = []   # [(name, paw_id, ch, rate), ...]

        self._build_ui()
        self._load_devices()
        self._load_meetings()
        self._schedule_chart_tick()
        self._schedule_call_status_tick()
        self._spawn_transcriber_process(self._model_var.get(), self._engine_key())

    def _reapply_icon(self):
        """Force our icon onto the window, including the taskbar button.

        Tk assigns its window *class* icon (the Python logo) and leaves
        WM_SETICON unset, which is what the Windows taskbar and Alt-Tab read
        — so iconbitmap alone changes the title bar but not the taskbar.
        Set the class icon and the window icons explicitly.
        """
        try:
            self.iconbitmap(default=APP_ICON)
        except Exception:
            pass
        try:
            import ctypes
            user32 = ctypes.windll.user32
            user32.GetParent.restype = ctypes.c_void_p
            user32.GetParent.argtypes = [ctypes.c_void_p]
            user32.LoadImageW.restype = ctypes.c_void_p
            user32.LoadImageW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p,
                                          ctypes.c_uint, ctypes.c_int,
                                          ctypes.c_int, ctypes.c_uint]
            user32.SendMessageW.restype = ctypes.c_void_p
            user32.SendMessageW.argtypes = [ctypes.c_void_p, ctypes.c_uint,
                                            ctypes.c_void_p, ctypes.c_void_p]
            # SetClassLongPtrW only exists on 64-bit; 32-bit keeps the old name.
            set_class = getattr(user32, "SetClassLongPtrW", None) or user32.SetClassLongW
            set_class.restype = ctypes.c_void_p
            set_class.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p]

            IMAGE_ICON, LR_LOADFROMFILE, WM_SETICON = 1, 0x0010, 0x0080
            ICON_SMALL, ICON_BIG = 0, 1
            GCLP_HICON, GCLP_HICONSM = -14, -34

            # winfo_id() is Tk's inner window; the taskbar tracks its parent.
            hwnd = user32.GetParent(self.winfo_id()) or self.winfo_id()

            for size, which, class_slot in ((32, ICON_BIG, GCLP_HICON),
                                            (16, ICON_SMALL, GCLP_HICONSM)):
                handle = user32.LoadImageW(None, APP_ICON, IMAGE_ICON,
                                           size, size, LR_LOADFROMFILE)
                if handle:
                    user32.SendMessageW(hwnd, WM_SETICON, which, handle)
                    set_class(hwnd, class_slot, handle)
        except Exception:
            pass

    # ── UI construction ───────────────────────────────────────────────────────

    def _build_ui(self):
        """Top-level tabs span the whole window: one for recording, one for
        browsing past meetings."""
        self.grid_columnconfigure(0, weight=1)
        self.grid_rowconfigure(0, weight=1)

        self._tabview = ctk.CTkTabview(self, anchor="nw")
        self._tabview.grid(row=0, column=0, sticky="nsew", padx=12, pady=(4, 10))
        self._build_record_tab(self._tabview.add(self.TAB_RECORD))
        self._build_meetings_tab(self._tabview.add(self.TAB_MEETINGS))
        self._tabview.set(self.TAB_RECORD)

    # ── Record tab ────────────────────────────────────────────────────────────

    def _build_record_tab(self, parent):
        parent.grid_columnconfigure(0, weight=1)
        parent.grid_rowconfigure(7, weight=1, minsize=220)   # output row expands

        pad = {"padx": 8, "pady": (8, 0)}

        # ── Teams call status ────────────────────────────────────────────────
        self._call_status_label = ctk.CTkLabel(
            parent, text="○  Checking for a Teams call…",
            font=ctk.CTkFont(size=12, weight="bold"), text_color="#888888",
            anchor="w",
        )
        self._call_status_label.grid(row=0, column=0, sticky="ew", padx=8, pady=(4, 0))

        # ── Microphone ────────────────────────────────────────────────────────
        mic_frame = ctk.CTkFrame(parent)
        mic_frame.grid(row=1, column=0, sticky="ew", **pad)
        mic_frame.grid_columnconfigure(0, weight=1)

        ctk.CTkLabel(mic_frame, text="Microphone  (you)",
                     font=ctk.CTkFont(size=14, weight="bold")
                     ).grid(row=0, column=0, sticky="w", padx=12, pady=(8, 2))
        self._teams_mic_label = ctk.CTkLabel(
            mic_frame, text="", font=ctk.CTkFont(size=11), text_color=COL_MIC
        )
        self._teams_mic_label.grid(row=1, column=0, sticky="w", padx=12, pady=(0, 4))
        self._mic_var  = ctk.StringVar()
        self._mic_menu = ctk.CTkOptionMenu(
            mic_frame, variable=self._mic_var, values=["Loading…"],
            width=600, anchor="w", command=self._on_mic_changed
        )
        self._mic_menu.grid(row=2, column=0, padx=12, pady=(0, 10), sticky="ew")

        # ── Speaker ───────────────────────────────────────────────────────────
        spk_frame = ctk.CTkFrame(parent)
        spk_frame.grid(row=2, column=0, sticky="ew", **pad)
        spk_frame.grid_columnconfigure(0, weight=1)

        ctk.CTkLabel(spk_frame, text="Speaker  (others)",
                     font=ctk.CTkFont(size=14, weight="bold")
                     ).grid(row=0, column=0, sticky="w", padx=12, pady=(8, 2))
        self._teams_spk_label = ctk.CTkLabel(
            spk_frame, text="", font=ctk.CTkFont(size=11), text_color=COL_SPK
        )
        self._teams_spk_label.grid(row=1, column=0, sticky="w", padx=12, pady=(0, 4))
        self._loopback_var  = ctk.StringVar()
        self._loopback_menu = ctk.CTkOptionMenu(
            spk_frame, variable=self._loopback_var, values=["Loading…"],
            width=600, anchor="w", command=self._on_spk_changed
        )
        self._loopback_menu.grid(row=2, column=0, padx=12, pady=(0, 10), sticky="ew")

        # ── Level chart ───────────────────────────────────────────────────────
        chart_frame = ctk.CTkFrame(parent)
        chart_frame.grid(row=3, column=0, sticky="ew", padx=8, pady=(8, 0))
        chart_frame.grid_columnconfigure(0, weight=1)

        self._chart_canvas = self._build_chart(chart_frame)
        self._chart_canvas.get_tk_widget().grid(row=0, column=0, sticky="ew", padx=4, pady=4)

        # ── Whisper settings ──────────────────────────────────────────────────
        settings_frame = ctk.CTkFrame(parent)
        settings_frame.grid(row=4, column=0, sticky="ew", padx=8, pady=(8, 0))
        settings_frame.grid_columnconfigure((0, 1, 2), weight=1)

        ctk.CTkLabel(settings_frame, text="Model",
                     font=ctk.CTkFont(size=12, weight="bold")
                     ).grid(row=0, column=0, sticky="w", padx=12, pady=(8, 2))
        self._model_menu = ctk.CTkOptionMenu(
            settings_frame, variable=self._model_var,
            values=["tiny", "base", "small", "medium", "large"],
            width=120, command=self._on_model_changed,
        )
        self._model_menu.grid(row=1, column=0, padx=12, pady=(0, 10), sticky="w")

        ctk.CTkLabel(settings_frame, text="Language",
                     font=ctk.CTkFont(size=12, weight="bold")
                     ).grid(row=0, column=1, sticky="w", padx=12, pady=(8, 2))
        ctk.CTkOptionMenu(
            settings_frame, variable=self._language_var,
            values=[
                "Auto-detect", "English", "French", "German", "Spanish",
                "Italian", "Portuguese", "Dutch", "Polish", "Russian",
                "Chinese", "Japanese", "Korean", "Arabic", "Hindi",
            ],
            width=160,
        ).grid(row=1, column=1, padx=12, pady=(0, 10), sticky="w")

        ctk.CTkLabel(settings_frame, text="Engine",
                     font=ctk.CTkFont(size=12, weight="bold")
                     ).grid(row=0, column=2, sticky="w", padx=12, pady=(8, 2))
        self._engine_menu = ctk.CTkOptionMenu(
            settings_frame, variable=self._engine_var,
            values=["Whisper (local)", "AssemblyAI (cloud, diarized)"],
            width=200, command=self._on_engine_changed,
        )
        self._engine_menu.grid(row=1, column=2, padx=12, pady=(0, 2), sticky="w")
        self._model_status_label = ctk.CTkLabel(
            settings_frame, text="○  Loading…",
            font=ctk.CTkFont(size=11), text_color="#888888",
        )
        self._model_status_label.grid(row=2, column=2, padx=12, pady=(0, 10), sticky="w")

        # ── Refresh ───────────────────────────────────────────────────────────
        ctk.CTkButton(
            parent, text="↺  Refresh device list", width=200,
            fg_color="transparent", border_width=1,
            command=self._load_devices
        ).grid(row=5, column=0, pady=(8, 0))

        # ── Start / Stop ──────────────────────────────────────────────────────
        self._start_btn = ctk.CTkButton(
            parent, text="▶  Start Recording", height=44,
            font=ctk.CTkFont(size=15, weight="bold"),
            fg_color="#2d6a4f", hover_color="#1b4332",
            command=self._toggle_recording
        )
        self._start_btn.grid(row=6, column=0, padx=8, pady=10, sticky="ew")

        # ── Output: live transcript beside the raw process log ───────────────
        output_container = ctk.CTkFrame(parent, fg_color="transparent")
        output_container.grid(row=7, column=0, sticky="nsew", padx=8, pady=(0, 8))
        output_container.grid_columnconfigure(0, weight=3)
        output_container.grid_columnconfigure(1, weight=2)
        output_container.grid_rowconfigure(0, weight=1)

        transcript_frame = ctk.CTkFrame(output_container)
        transcript_frame.grid(row=0, column=0, sticky="nsew", padx=(0, 8))
        transcript_frame.grid_columnconfigure(0, weight=1)
        transcript_frame.grid_rowconfigure(1, weight=1)

        ctk.CTkLabel(transcript_frame, text="Live Transcription",
                     font=ctk.CTkFont(size=12, weight="bold")
                     ).grid(row=0, column=0, sticky="w", padx=10, pady=(6, 0))
        self._transcript_box = ctk.CTkTextbox(
            transcript_frame, font=ctk.CTkFont(size=13),
            wrap="word", state="disabled"
        )
        self._transcript_box.grid(row=1, column=0, sticky="nsew", padx=8, pady=(2, 8))
        self._apply_transcript_tags(self._transcript_box)

        log_frame = ctk.CTkFrame(output_container)
        log_frame.grid(row=0, column=1, sticky="nsew", padx=(8, 0))
        log_frame.grid_columnconfigure(0, weight=1)
        log_frame.grid_rowconfigure(1, weight=1)

        ctk.CTkLabel(log_frame, text="Log",
                     font=ctk.CTkFont(size=12, weight="bold")
                     ).grid(row=0, column=0, sticky="w", padx=10, pady=(6, 0))
        self._output_box = ctk.CTkTextbox(
            log_frame, font=ctk.CTkFont(family="Consolas", size=12),
            wrap="word", state="disabled"
        )
        self._output_box.grid(row=1, column=0, sticky="nsew", padx=8, pady=(2, 8))

    # ── Recent meetings tab ───────────────────────────────────────────────────

    def _build_meetings_tab(self, parent):
        """Master-detail: the meeting list on the left, the selected meeting's
        transcript on the right, so browsing history never disturbs the live view."""
        parent.grid_columnconfigure(0, weight=0, minsize=340)
        parent.grid_columnconfigure(1, weight=1)
        parent.grid_rowconfigure(0, weight=1)

        # ── Left: the list ────────────────────────────────────────────────────
        list_frame = ctk.CTkFrame(parent)
        list_frame.grid(row=0, column=0, sticky="nsew", padx=(8, 8), pady=8)
        list_frame.grid_columnconfigure(0, weight=1)
        list_frame.grid_rowconfigure(1, weight=1)

        list_header = ctk.CTkFrame(list_frame, fg_color="transparent")
        list_header.grid(row=0, column=0, sticky="ew", padx=10, pady=(8, 0))
        list_header.grid_columnconfigure(0, weight=1)
        ctk.CTkLabel(list_header, text="Recent Meetings",
                     font=ctk.CTkFont(size=13, weight="bold"), anchor="w"
                     ).grid(row=0, column=0, sticky="ew")
        ctk.CTkButton(
            list_header, text="↺", width=30, height=24,
            fg_color="transparent", border_width=1,
            command=self._load_meetings,
        ).grid(row=0, column=1, sticky="e")

        self._meetings_list = ctk.CTkScrollableFrame(list_frame, fg_color="transparent")
        self._meetings_list.grid(row=1, column=0, sticky="nsew", padx=4, pady=(4, 8))
        self._meetings_list.grid_columnconfigure(0, weight=1)

        # ── Right: the selected meeting's transcript ──────────────────────────
        view_frame = ctk.CTkFrame(parent)
        view_frame.grid(row=0, column=1, sticky="nsew", padx=(0, 8), pady=8)
        view_frame.grid_columnconfigure(0, weight=1)
        view_frame.grid_rowconfigure(1, weight=1)

        view_header = ctk.CTkFrame(view_frame, fg_color="transparent")
        view_header.grid(row=0, column=0, sticky="ew", padx=10, pady=(8, 0))
        view_header.grid_columnconfigure(0, weight=1)
        self._meeting_view_title = ctk.CTkLabel(
            view_header, text="No meeting selected",
            font=ctk.CTkFont(size=13, weight="bold"), anchor="w",
        )
        self._meeting_view_title.grid(row=0, column=0, sticky="ew")
        # Revealed once a row is selected — deletes that single transcript line
        self._delete_row_btn = ctk.CTkButton(
            view_header, text="Delete line", width=100, height=24,
            fg_color="transparent", border_width=1,
            text_color="#e06c75", hover_color="#4a2228",
            command=self._delete_selected_rows,
        )
        # Revealed once a meeting is open — opens its folder of wav files
        self._open_folder_btn = ctk.CTkButton(
            view_header, text="Open folder", width=110, height=24,
            fg_color="transparent", border_width=1,
            command=self._open_selected_meeting_folder,
        )

        self._meeting_box = ctk.CTkTextbox(
            view_frame, font=ctk.CTkFont(size=13), wrap="word", state="disabled"
        )
        self._meeting_box.grid(row=1, column=0, sticky="nsew", padx=8, pady=(4, 8))
        self._apply_transcript_tags(self._meeting_box)
        self._meeting_box.tag_config("rowsel", background="#3a4a63")
        # Click to pick lines (Ctrl toggles, Shift extends); Delete removes them.
        self._meeting_box.bind("<Button-1>", self._on_meeting_box_click)
        self._meeting_box.bind("<Control-Button-1>",
                               lambda e: self._on_meeting_box_click(e, "toggle"))
        self._meeting_box.bind("<Shift-Button-1>",
                               lambda e: self._on_meeting_box_click(e, "range"))
        self._meeting_box.bind("<Delete>", lambda _e: self._delete_selected_rows())
        self._meeting_box.bind("<Control-a>", lambda _e: self._select_all_rows())
        self._meeting_box.bind("<Control-A>", lambda _e: self._select_all_rows())

    @staticmethod
    def _apply_transcript_tags(box):
        box.tag_config("me", foreground=COL_MIC)
        box.tag_config("other", foreground=COL_SPK)
        box.tag_config("dim", foreground="#888888")

    def _build_chart(self, parent) -> FigureCanvasTkAgg:
        fig = Figure(figsize=(6.5, 1.5), dpi=96, facecolor=BG_DARK)
        fig.subplots_adjust(left=0.05, right=0.98, top=0.84, bottom=0.16, hspace=0.95)

        x = np.linspace(-CHART_DURATION, 0, CHART_POINTS)
        zeros = np.zeros(CHART_POINTS)

        # ── Mic subplot ───────────────────────────────────────────────────────
        self._ax_mic = fig.add_subplot(2, 1, 1)
        self._ax_mic.set_facecolor(BG_AXES)
        self._ax_mic.set_title("Mic", color=COL_MIC, fontsize=9, loc="left", pad=3)
        self._ax_mic.set_xlim(-CHART_DURATION, 0)
        self._ax_mic.set_ylim(0, 1)
        self._ax_mic.set_yticks([])
        self._ax_mic.set_xticks(range(-CHART_DURATION, 1, 2))
        self._ax_mic.set_xticklabels(
            [f"{t}s" for t in range(-CHART_DURATION, 1, 2)],
            color="#888888", fontsize=7
        )
        self._ax_mic.tick_params(length=0)
        for spine in self._ax_mic.spines.values():
            spine.set_visible(False)
        self._ax_mic.yaxis.grid(True, color=COL_GRID, linewidth=0.5)
        self._ax_mic.set_axisbelow(True)

        self._mic_fill = self._ax_mic.fill_between(x, zeros, zeros,
                                                    color=COL_MIC, alpha=0.35)
        self._mic_line, = self._ax_mic.plot(x, zeros, color=COL_MIC, linewidth=1.2)

        # ── Speaker subplot ───────────────────────────────────────────────────
        self._ax_spk = fig.add_subplot(2, 1, 2)
        self._ax_spk.set_facecolor(BG_AXES)
        self._ax_spk.set_title("Speaker", color=COL_SPK, fontsize=9, loc="left", pad=3)
        self._ax_spk.set_xlim(-CHART_DURATION, 0)
        self._ax_spk.set_ylim(0, 1)
        self._ax_spk.set_yticks([])
        self._ax_spk.set_xticks(range(-CHART_DURATION, 1, 2))
        self._ax_spk.set_xticklabels(
            [f"{t}s" for t in range(-CHART_DURATION, 1, 2)],
            color="#888888", fontsize=7
        )
        self._ax_spk.tick_params(length=0)
        for spine in self._ax_spk.spines.values():
            spine.set_visible(False)
        self._ax_spk.yaxis.grid(True, color=COL_GRID, linewidth=0.5)
        self._ax_spk.set_axisbelow(True)

        self._spk_fill = self._ax_spk.fill_between(x, zeros, zeros,
                                                    color=COL_SPK, alpha=0.35)
        self._spk_line, = self._ax_spk.plot(x, zeros, color=COL_SPK, linewidth=1.2)

        self._chart_x = x
        canvas = FigureCanvasTkAgg(fig, master=parent)
        canvas.draw()
        return canvas

    # ── Chart update loop ─────────────────────────────────────────────────────

    def _schedule_chart_tick(self):
        self._monitor.tick()
        mic_hist, spk_hist = self._monitor.get_histories()

        mic_arr = np.array(mic_hist)
        spk_arr = np.array(spk_hist)

        # Update mic
        self._mic_line.set_ydata(mic_arr)
        self._mic_fill.remove()
        self._mic_fill = self._ax_mic.fill_between(
            self._chart_x, 0, mic_arr, color=COL_MIC, alpha=0.35
        )

        # Update speaker
        self._spk_line.set_ydata(spk_arr)
        self._spk_fill.remove()
        self._spk_fill = self._ax_spk.fill_between(
            self._chart_x, 0, spk_arr, color=COL_SPK, alpha=0.35
        )

        self._chart_canvas.draw_idle()
        self.after(1000 // CHART_HZ, self._schedule_chart_tick)

    # ── Teams call status ─────────────────────────────────────────────────────

    CALL_STATUS_POLL_MS = 2000

    def _schedule_call_status_tick(self):
        in_call, meeting_title = get_teams_call_state()
        if in_call:
            label = f"●  On a Teams call — {meeting_title}" if meeting_title else "●  On a Teams call"
            self._call_status_label.configure(text=label, text_color="#2d9d5f")
        else:
            self._call_status_label.configure(
                text="○  Not currently on a Teams call", text_color="#888888"
            )
        self.after(self.CALL_STATUS_POLL_MS, self._schedule_call_status_tick)

    # ── Device loading ────────────────────────────────────────────────────────

    def _load_devices(self):
        self._mic_devices      = get_mic_devices()
        self._loopback_devices = get_loopback_devices()
        teams_mic_name, teams_spk_name = detect_teams_defaults()

        # Mic dropdown
        mic_labels = [name for name, _ in self._mic_devices] or ["No input devices found"]
        self._mic_menu.configure(values=mic_labels)
        selected_mic = next(
            (name for name, _ in self._mic_devices if name == teams_mic_name),
            mic_labels[0]
        )
        self._mic_var.set(selected_mic)
        self._teams_mic_label.configure(
            text=f"● Teams using: {teams_mic_name}" if teams_mic_name else "● Teams not detected"
        )

        # Speaker dropdown
        spk_labels = [name for name, *_ in self._loopback_devices] or ["No loopback devices found"]
        self._loopback_menu.configure(values=spk_labels)
        selected_spk = next(
            (name for name, *_ in self._loopback_devices if name == teams_spk_name),
            spk_labels[0]
        )
        self._loopback_var.set(selected_spk)
        self._teams_spk_label.configure(
            text=f"● Teams using: {teams_spk_name}" if teams_spk_name else "● Teams not detected"
        )

    def _start_monitors(self):
        mic_id = self._selected_mic_id()
        lb     = self._selected_loopback_entry()
        if mic_id is not None:
            self._monitor.start_mic(mic_id)
        if lb is not None:
            _, paw_id, ch, rate = lb
            self._monitor.start_speaker(paw_id, ch, rate)

    def _on_mic_changed(self, _value):
        pass

    def _on_spk_changed(self, _value):
        pass

    # ── Selection helpers ─────────────────────────────────────────────────────

    def _selected_mic_id(self):
        label = self._mic_var.get()
        return next((sid for name, sid in self._mic_devices if name == label), None)

    def _selected_loopback_entry(self):
        label = self._loopback_var.get()
        return next((e for e in self._loopback_devices if e[0] == label), None)

    def _selected_loopback_id(self):
        e = self._selected_loopback_entry()
        return e[1] if e else None

    # ── Transcriber process (persistent standby, preloads the model) ─────────

    LANGUAGE_MAP = {
        "Auto-detect": None, "English": "en", "French": "fr",
        "German": "de", "Spanish": "es", "Italian": "it",
        "Portuguese": "pt", "Dutch": "nl", "Polish": "pl",
        "Russian": "ru", "Chinese": "zh", "Japanese": "ja",
        "Korean": "ko", "Arabic": "ar", "Hindi": "hi",
    }

    def _engine_key(self):
        return "assemblyai" if self._engine_var.get().startswith("AssemblyAI") else "whisper"

    def _spawn_transcriber_process(self, model_size, engine):
        """Launch transcriber.py in --serve (standby) mode. For the Whisper
        engine this starts loading the local model immediately, well before
        Start is clicked; the AssemblyAI engine has no local model to warm
        up (transcription runs in AssemblyAI's cloud)."""
        cmd = [PYTHON, "-u", TRANSCRIBER_SCRIPT, "--serve", "--engine", engine]
        if engine == "whisper":
            cmd += ["--model", model_size]
        self._log(f"Starting transcriber (engine={engine}"
                  + (f", model={model_size}" if engine == "whisper" else "")
                  + f")...\n{'─'*60}\n")
        self._proc = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, bufsize=1,
            cwd=os.path.dirname(TRANSCRIBER_SCRIPT),
            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP,
        )
        self._spawned_model = model_size
        self._spawned_engine = engine
        self._model_ready = (engine != "whisper")  # cloud engine has no load delay
        self._update_model_status_label()
        self._reader_thread = threading.Thread(target=self._read_output, daemon=True)
        self._reader_thread.start()

    def _terminate_transcriber_process(self):
        proc, self._proc = self._proc, None
        if proc is None:
            return
        self._send_control(proc, {"cmd": "quit"})

        def _wait():
            try:
                proc.wait(timeout=5)
            except Exception:
                try:
                    proc.terminate()
                except Exception:
                    pass
        threading.Thread(target=_wait, daemon=True).start()

    def _send_control(self, proc, msg):
        try:
            proc.stdin.write(json.dumps(msg) + "\n")
            proc.stdin.flush()
        except Exception as e:
            self._log(f"[control] Failed to send {msg.get('cmd')}: {e}\n")

    def _update_model_status_label(self):
        if self._spawned_engine != "whisper":
            self._model_status_label.configure(
                text="●  AssemblyAI (cloud, diarized)", text_color="#2d9d5f"
            )
        elif self._model_ready:
            self._model_status_label.configure(
                text=f"●  Model ready ({self._spawned_model})", text_color="#2d9d5f"
            )
        else:
            self._model_status_label.configure(
                text=f"○  Loading model ({self._spawned_model})…", text_color="#888888"
            )

    def _on_model_changed(self, value):
        if self._recording_active or value == self._spawned_model:
            return
        self._terminate_transcriber_process()
        self._spawn_transcriber_process(value, self._spawned_engine)

    def _on_engine_changed(self, _value):
        engine = self._engine_key()
        if self._recording_active:
            return
        self._model_menu.configure(state=("normal" if engine == "whisper" else "disabled"))
        if engine == self._spawned_engine:
            return
        self._terminate_transcriber_process()
        self._spawn_transcriber_process(self._model_var.get(), engine)

    # ── Recording control ─────────────────────────────────────────────────────

    def _toggle_recording(self):
        if not self._recording_active:
            self._start_recording()
        else:
            self._stop_recording()

    def _start_recording(self):
        if self._proc is None or self._proc.poll() is not None:
            self._log("[warn] Transcriber process not running — restarting it.\n")
            self._spawn_transcriber_process(self._model_var.get(), self._engine_key())

        mic_id        = self._selected_mic_id()
        loopback_id   = self._selected_loopback_id()
        language_code = self.LANGUAGE_MAP.get(self._language_var.get())

        self._clear_transcript()
        self._log(
            f"Starting recording (mic={mic_id}, loopback={loopback_id}, "
            f"language={language_code or 'auto'})\n{'─'*60}\n"
        )
        self._send_control(self._proc, {
            "cmd": "start", "mic_id": mic_id, "loopback_id": loopback_id,
            "language": language_code,
        })

        self._recording_active = True
        self._start_monitors()
        self._model_menu.configure(state="disabled")
        self._engine_menu.configure(state="disabled")
        self._start_btn.configure(text="■  Stop Recording",
                                  fg_color="#6b2737", hover_color="#3d0c15")

    def _stop_recording(self):
        if not self._recording_active or self._proc is None:
            return

        self._send_control(self._proc, {"cmd": "stop"})
        self._monitor.stop()
        self._start_btn.configure(
            text="⏳  Finishing transcription…", state="disabled",
            fg_color="#555555", hover_color="#555555",
        )
        self._log("\n── Stopping — waiting for transcription queue to drain… ──\n")
        # Button/model-menu state is restored in _on_session_ended once the
        # transcriber reports SESSION_ENDED — the process itself stays alive.

    def _read_output(self):
        proc = self._proc
        try:
            for line in proc.stdout:
                self._log(line)
                self._route_transcript_line(line)
                self._route_control_line(line)
        except Exception:
            pass
        # Only treat this as a crash if nothing has replaced/cleared _proc
        # in the meantime (i.e. this wasn't an intentional quit/restart).
        if self._proc is proc:
            self.after(0, self._on_process_crashed)

    def _route_transcript_line(self, line):
        match = TRANSCRIPT_LINE_RE.match(line.strip())
        if not match:
            return
        speaker, text = match.group(1), match.group(2)
        if not text:
            return
        self._log_transcript(speaker, text)

    def _route_control_line(self, line):
        text = line.strip()
        if text.startswith("NEW_SESSION "):
            folder = text.split(" ", 1)[1]
            self.after(0, lambda: self._on_new_session(folder))
        elif text == "MODEL_READY":
            self._model_ready = True
            self.after(0, self._update_model_status_label)
        elif text == "SESSION_ENDED":
            self.after(0, self._on_session_ended)

    def _on_new_session(self, folder):
        """The transcriber rolled into a new recording because the Teams
        meeting changed. Mark the break in the live view and pick up the
        newly created meeting in the history list."""
        self._transcript_divider(f"New meeting — recording into {folder}")
        self._load_meetings()

    def _transcript_divider(self, label):
        def _do():
            self._transcript_box.configure(state="normal")
            self._transcript_box.insert("end", f"\n── {label} ──\n", "dim")
            self._transcript_box.see("end")
            self._transcript_box.configure(state="disabled")
        self.after(0, _do)

    def _on_session_ended(self):
        self._recording_active = False
        self._monitor.stop()
        self._start_btn.configure(text="▶  Start Recording", state="normal",
                                  fg_color="#2d6a4f", hover_color="#1b4332")
        self._model_menu.configure(state=("normal" if self._spawned_engine == "whisper" else "disabled"))
        self._engine_menu.configure(state="normal")
        self._load_meetings()   # the session just written is now a past meeting

    def _on_process_crashed(self):
        self._proc = None
        self._recording_active = False
        self._monitor.stop()
        self._start_btn.configure(text="▶  Start Recording", state="normal",
                                  fg_color="#2d6a4f", hover_color="#1b4332")
        self._model_menu.configure(state=("normal" if self._spawned_engine == "whisper" else "disabled"))
        self._engine_menu.configure(state="normal")
        self._log("\n── Transcriber process ended unexpectedly — restarting… ──\n")
        self._spawn_transcriber_process(self._model_var.get(), self._spawned_engine)

    # ── Output helper ─────────────────────────────────────────────────────────

    def _log(self, text):
        def _do():
            self._output_box.configure(state="normal")
            self._output_box.insert("end", text)
            self._output_box.see("end")
            self._output_box.configure(state="disabled")
        self.after(0, _do)

    def _log_transcript(self, speaker, text):
        tag = "me" if speaker == "ME" else "other"
        timestamp = datetime.now().strftime("%H:%M:%S")
        def _do():
            self._transcript_box.configure(state="normal")
            self._transcript_box.insert("end", f"[{timestamp}] ", "dim")
            self._transcript_box.insert("end", f"{speaker}: ", tag)
            self._transcript_box.insert("end", f"{text}\n")
            self._transcript_box.see("end")
            self._transcript_box.configure(state="disabled")
        self.after(0, _do)

    def _clear_transcript(self):
        self._transcript_box.configure(state="normal")
        self._transcript_box.delete("1.0", "end")
        self._transcript_box.configure(state="disabled")

    # ── Recent meetings panel ─────────────────────────────────────────────────

    def _load_meetings(self):
        """(Re)build the Recent Meetings list from the recordings on disk."""
        for child in self._meetings_list.winfo_children():
            child.destroy()

        meetings = get_recent_meetings()
        if not meetings:
            ctk.CTkLabel(
                self._meetings_list, text="No recordings yet",
                font=ctk.CTkFont(size=11), text_color="#888888",
            ).grid(row=0, column=0, sticky="w", padx=8, pady=8)
            return

        for row, meeting in enumerate(meetings):
            self._build_meeting_row(row, meeting)

    def _build_meeting_row(self, row, meeting):
        """One clickable meeting entry: title on top, details underneath."""
        card = ctk.CTkFrame(self._meetings_list, fg_color="#2b2b2b", corner_radius=6)
        card.grid(row=row, column=0, sticky="ew", padx=2, pady=3)
        card.grid_columnconfigure(0, weight=1)

        engine_colour = COL_SPK if meeting["engine"] == "assemblyai" else COL_MIC
        engine_label = "AssemblyAI" if meeting["engine"] == "assemblyai" else "Whisper"

        title = ctk.CTkLabel(
            card, text=meeting["title"], anchor="w", justify="left",
            font=ctk.CTkFont(size=12, weight="bold"), wraplength=250,
        )
        title.grid(row=0, column=0, sticky="ew", padx=8, pady=(6, 0))

        detail = ctk.CTkLabel(
            card, anchor="w", justify="left",
            text=f"{format_meeting_when(meeting['started'])}  ·  {meeting['turns']} lines",
            font=ctk.CTkFont(size=10), text_color="#9a9a9a",
        )
        detail.grid(row=1, column=0, sticky="ew", padx=8, pady=0)

        engine = ctk.CTkLabel(
            card, text=engine_label, anchor="w",
            font=ctk.CTkFont(size=10), text_color=engine_colour,
        )
        engine.grid(row=2, column=0, sticky="ew", padx=8, pady=(0, 6))

        # Deliberately its own widget in a separate column, so a click to
        # delete can never be mistaken for a click to open.
        delete_btn = ctk.CTkButton(
            card, text="✕", width=26, height=26,
            fg_color="transparent", hover_color="#4a2228", text_color="#9a9a9a",
            command=lambda m=meeting: self._confirm_delete_meeting(m),
        )
        delete_btn.grid(row=0, column=1, rowspan=3, sticky="ne", padx=(0, 6), pady=6)

        # Bind the whole card (and its labels, which would otherwise swallow
        # the click) so the entire row is one hit target.
        for w in (card, title, detail, engine):
            w.bind("<Button-1>", lambda _e, m=meeting: self._show_meeting(m))
            w.bind("<Enter>", lambda _e, c=card: c.configure(fg_color="#3a3a3a"))
            w.bind("<Leave>", lambda _e, c=card: c.configure(fg_color="#2b2b2b"))
            w.configure(cursor="hand2")

    def _show_meeting(self, meeting):
        """Load a past meeting's transcript into the viewer beside the list."""
        jsonl = os.path.join(meeting["path"], "transcriptions.jsonl")
        self._selected_meeting = meeting
        self._meeting_jsonl = jsonl
        # Maps a display line number in the viewer to its line index in the
        # .jsonl, so deleting a row edits exactly the right source line.
        self._row_source_lines = {}
        self._selected_rows = set()
        self._anchor_row = None
        self._delete_row_btn.grid_remove()
        self._clear_meeting_view()
        self._meeting_view_title.configure(text=meeting["title"])
        self._open_folder_btn.grid(row=0, column=2, sticky="e", padx=(6, 0))

        if not os.path.isfile(jsonl):
            self._append_meeting_note("No transcript file in " + meeting["folder"] + ".")
            return

        shown = 0
        try:
            with open(jsonl, encoding="utf-8") as f:
                for source_index, line in enumerate(f):
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        entry = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    if entry.get("type") == "session_metadata":
                        continue
                    text = (entry.get("text") or "").strip()
                    if not text:
                        continue
                    speaker = "ME" if entry.get("speaker") == "[ME]" else "OTHER"
                    label = entry.get("speaker_label")
                    if speaker == "OTHER" and label and label != "PENDING":
                        text = f"[{label}] {text}"
                    stamp = self._format_entry_time(entry.get("timestamp"))
                    display_line = self._append_meeting_line(stamp, speaker, text)
                    self._row_source_lines[display_line] = source_index
                    shown += 1
        except OSError as e:
            self._append_meeting_note(f"Could not read transcript: {e}")
            return

        if shown == 0:
            self._append_meeting_note("This session recorded no speech.")

    # ── Deleting a whole recording ────────────────────────────────────────────

    def _confirm_delete_meeting(self, meeting):
        """Delete a recording folder — transcript and wav segments — after
        confirming. Goes to the Recycle Bin so it can be restored."""
        ok = messagebox.askyesno(
            "Delete recording",
            f"Delete this recording?\n\n"
            f"{meeting['title']}\n"
            f"{format_meeting_when(meeting['started'])}  ·  {meeting['turns']} lines\n"
            f"Folder: {meeting['folder']}\n\n"
            "The transcript and its audio segments go to the Recycle Bin.",
            icon="warning", default="no", parent=self,
        )
        if not ok:
            return

        deleted, error = move_to_recycle_bin(meeting["path"])
        if not deleted:
            messagebox.showerror(
                "Delete failed",
                f"Could not delete {meeting['folder']}:\n\n{error}",
                parent=self,
            )
            return

        self._log(f"Deleted recording {meeting['folder']} (moved to Recycle Bin)\n")
        # If the viewer was showing it, there is nothing left to show.
        if self._selected_meeting and self._selected_meeting["path"] == meeting["path"]:
            self._reset_meeting_view()
        self._load_meetings()

    def _reset_meeting_view(self):
        self._selected_meeting = None
        self._meeting_jsonl = None
        self._row_source_lines = {}
        self._selected_rows = set()
        self._anchor_row = None
        self._clear_meeting_view()
        self._meeting_view_title.configure(text="No meeting selected")
        self._open_folder_btn.grid_remove()
        self._delete_row_btn.grid_remove()

    # ── Deleting transcript lines (single or multi-select) ────────────────────

    def _on_meeting_box_click(self, event, mode="set"):
        """Pick transcript lines. Plain click selects one, Ctrl+click toggles
        one, Shift+click extends a range from the anchor — the usual list
        conventions. Returns "break" so Tk's own text selection does not fight
        our row highlight."""
        self._meeting_box.focus_set()
        if not self._row_source_lines:
            return "break"
        line = int(self._meeting_box.index(f"@{event.x},{event.y}").split(".")[0])
        if line not in self._row_source_lines:
            return "break"

        if mode == "toggle":
            self._selected_rows.symmetric_difference_update({line})
            self._anchor_row = line
        elif mode == "range" and self._anchor_row is not None:
            lo, hi = sorted((self._anchor_row, line))
            self._selected_rows = {n for n in self._row_source_lines if lo <= n <= hi}
        else:
            self._selected_rows = {line}
            self._anchor_row = line

        self._refresh_row_selection()
        return "break"

    def _refresh_row_selection(self):
        """Repaint the highlight and keep the delete button in step."""
        self._meeting_box.tag_remove("rowsel", "1.0", "end")
        for line in self._selected_rows:
            self._meeting_box.tag_add("rowsel", f"{line}.0", f"{line}.end+1c")

        count = len(self._selected_rows)
        if count:
            label = "Delete line" if count == 1 else f"Delete {count} lines"
            self._delete_row_btn.configure(text=label)
            self._delete_row_btn.grid(row=0, column=1, sticky="e", padx=(6, 0))
        else:
            self._delete_row_btn.grid_remove()

    def _select_all_rows(self):
        if not self._row_source_lines:
            return "break"
        self._selected_rows = set(self._row_source_lines)
        self._refresh_row_selection()
        return "break"

    def _delete_selected_rows(self):
        """Remove every selected line from the meeting's transcriptions.jsonl."""
        if not self._selected_rows or not self._meeting_jsonl:
            return
        targets = sorted(self._selected_rows)
        source_indices = [self._row_source_lines[n] for n in targets
                          if n in self._row_source_lines]
        if not source_indices:
            return

        preview_lines = []
        for line in targets[:5]:
            text = self._meeting_box.get(f"{line}.0", f"{line}.end").strip()
            preview_lines.append(text[:97] + "…" if len(text) > 100 else text)
        if len(targets) > 5:
            preview_lines.append(f"…and {len(targets) - 5} more")

        count = len(source_indices)
        heading = "Delete this transcript line?" if count == 1 else f"Delete {count} transcript lines?"
        if not messagebox.askyesno(
            "Delete lines",
            heading + "\n\n" + "\n".join(preview_lines)
            + "\n\nThis edits the saved transcript and cannot be undone.",
            icon="warning", default="no", parent=self,
        ):
            return

        try:
            with open(self._meeting_jsonl, encoding="utf-8") as f:
                lines = f.readlines()
            if any(not 0 <= i < len(lines) for i in source_indices):
                raise IndexError("transcript changed on disk")
            # Highest index first, so each removal cannot shift the next one.
            for index in sorted(source_indices, reverse=True):
                del lines[index]
            # Write to a sibling temp file then swap, so an interrupted write
            # cannot leave a half-truncated transcript behind.
            tmp = self._meeting_jsonl + ".tmp"
            with open(tmp, "w", encoding="utf-8", newline="\n") as f:
                f.writelines(lines)
            os.replace(tmp, self._meeting_jsonl)
        except (OSError, IndexError) as e:
            messagebox.showerror("Delete failed",
                                 f"Could not update the transcript:\n\n{e}",
                                 parent=self)
            return

        self._log(f"Deleted {count} transcript line(s) from "
                  f"{self._selected_meeting['folder']}\n")
        self._show_meeting(self._selected_meeting)   # re-render from the edited file
        self._load_meetings()                        # list line counts are now stale

    def _open_selected_meeting_folder(self):
        """Reveal the meeting's folder — the wav segments live alongside the transcript."""
        if not self._selected_meeting:
            return
        try:
            os.startfile(self._selected_meeting["path"])
        except OSError as e:
            self._append_meeting_note(f"Could not open folder: {e}")

    @staticmethod
    def _format_entry_time(raw):
        if not raw:
            return "--:--:--"
        try:
            return datetime.fromisoformat(raw).strftime("%H:%M:%S")
        except ValueError:
            return "--:--:--"

    def _clear_meeting_view(self):
        self._meeting_box.configure(state="normal")
        self._meeting_box.delete("1.0", "end")
        self._meeting_box.configure(state="disabled")

    def _append_meeting_line(self, stamp, speaker, text):
        """Synchronous write into the history viewer (always on the UI thread).
        Returns the display line number the row was written to."""
        tag = "me" if speaker == "ME" else "other"
        self._meeting_box.configure(state="normal")
        display_line = int(self._meeting_box.index("end-1c").split(".")[0])
        self._meeting_box.insert("end", f"[{stamp}] ", "dim")
        self._meeting_box.insert("end", f"{speaker}: ", tag)
        self._meeting_box.insert("end", text + "\n")
        self._meeting_box.configure(state="disabled")
        return display_line

    def _append_meeting_note(self, text):
        self._meeting_box.configure(state="normal")
        self._meeting_box.insert("end", text + "\n", "dim")
        self._meeting_box.configure(state="disabled")

    # ── Clean shutdown ────────────────────────────────────────────────────────

    def on_close(self):
        self._monitor.stop()
        self._terminate_transcriber_process()
        self.destroy()


if __name__ == "__main__":
    app = LauncherApp()
    app.protocol("WM_DELETE_WINDOW", app.on_close)
    app.mainloop()
