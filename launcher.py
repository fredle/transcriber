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
import matplotlib
matplotlib.use("TkAgg")
from matplotlib.figure import Figure
from matplotlib.backends.backend_tkagg import FigureCanvasTkAgg

# ── Theme ────────────────────────────────────────────────────────────────────
ctk.set_appearance_mode("dark")
ctk.set_default_color_theme("blue")

TRANSCRIBER_SCRIPT = os.path.join(os.path.dirname(__file__), "transcriber.py")
PYTHON = sys.executable

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
    def __init__(self):
        super().__init__()
        self.title("Meeting Transcriber")
        self.geometry("1100x820")
        self.minsize(900, 600)
        self.resizable(True, True)

        self._proc              = None   # long-lived transcriber standby/serving process
        self._reader_thread     = None
        self._monitor           = AudioMonitor()
        self._recording_active  = False
        self._model_ready       = False
        self._spawned_model     = None   # model size the running process was started with

        self._model_var    = ctk.StringVar(value="base")
        self._language_var = ctk.StringVar(value="Auto-detect")

        self._mic_devices      = []   # [(name, sd_id), ...]
        self._loopback_devices = []   # [(name, paw_id, ch, rate), ...]

        self._build_ui()
        self._load_devices()
        self._schedule_chart_tick()
        self._schedule_call_status_tick()
        self._spawn_transcriber_process(self._model_var.get())

    # ── UI construction ───────────────────────────────────────────────────────

    def _build_ui(self):
        self.grid_columnconfigure(0, weight=1)
        self.grid_rowconfigure(7, weight=1)   # output row expands

        pad = {"padx": 20, "pady": (10, 0)}

        # ── Teams call status ────────────────────────────────────────────────
        self._call_status_label = ctk.CTkLabel(
            self, text="○  Checking for a Teams call…",
            font=ctk.CTkFont(size=12, weight="bold"), text_color="#888888",
            anchor="w",
        )
        self._call_status_label.grid(row=0, column=0, sticky="ew", padx=20, pady=(14, 0))

        # ── Microphone ────────────────────────────────────────────────────────
        mic_frame = ctk.CTkFrame(self)
        mic_frame.grid(row=1, column=0, sticky="ew", **pad)
        mic_frame.grid_columnconfigure(0, weight=1)

        ctk.CTkLabel(mic_frame, text="Microphone  (you)",
                     font=ctk.CTkFont(size=14, weight="bold")
                     ).grid(row=0, column=0, sticky="w", padx=12, pady=(10, 2))
        self._teams_mic_label = ctk.CTkLabel(
            mic_frame, text="", font=ctk.CTkFont(size=11), text_color=COL_MIC
        )
        self._teams_mic_label.grid(row=1, column=0, sticky="w", padx=12, pady=(0, 4))
        self._mic_var  = ctk.StringVar()
        self._mic_menu = ctk.CTkOptionMenu(
            mic_frame, variable=self._mic_var, values=["Loading…"],
            width=600, anchor="w", command=self._on_mic_changed
        )
        self._mic_menu.grid(row=2, column=0, padx=12, pady=(0, 12), sticky="ew")

        # ── Speaker ───────────────────────────────────────────────────────────
        spk_frame = ctk.CTkFrame(self)
        spk_frame.grid(row=2, column=0, sticky="ew", **pad)
        spk_frame.grid_columnconfigure(0, weight=1)

        ctk.CTkLabel(spk_frame, text="Speaker  (others)",
                     font=ctk.CTkFont(size=14, weight="bold")
                     ).grid(row=0, column=0, sticky="w", padx=12, pady=(10, 2))
        self._teams_spk_label = ctk.CTkLabel(
            spk_frame, text="", font=ctk.CTkFont(size=11), text_color=COL_SPK
        )
        self._teams_spk_label.grid(row=1, column=0, sticky="w", padx=12, pady=(0, 4))
        self._loopback_var  = ctk.StringVar()
        self._loopback_menu = ctk.CTkOptionMenu(
            spk_frame, variable=self._loopback_var, values=["Loading…"],
            width=600, anchor="w", command=self._on_spk_changed
        )
        self._loopback_menu.grid(row=2, column=0, padx=12, pady=(0, 12), sticky="ew")

        # ── Level chart ───────────────────────────────────────────────────────
        chart_frame = ctk.CTkFrame(self)
        chart_frame.grid(row=3, column=0, sticky="ew", padx=20, pady=(10, 0))
        chart_frame.grid_columnconfigure(0, weight=1)

        self._chart_canvas = self._build_chart(chart_frame)
        self._chart_canvas.get_tk_widget().grid(row=0, column=0, sticky="ew", padx=4, pady=4)

        # ── Whisper settings ──────────────────────────────────────────────────
        settings_frame = ctk.CTkFrame(self)
        settings_frame.grid(row=4, column=0, sticky="ew", padx=20, pady=(10, 0))
        settings_frame.grid_columnconfigure((0, 1, 2), weight=1)

        ctk.CTkLabel(settings_frame, text="Model",
                     font=ctk.CTkFont(size=12, weight="bold")
                     ).grid(row=0, column=0, sticky="w", padx=12, pady=(8, 2))
        self._model_menu = ctk.CTkOptionMenu(
            settings_frame, variable=self._model_var,
            values=["tiny", "base", "small", "medium", "large"],
            width=120, command=self._on_model_changed,
        )
        self._model_menu.grid(row=1, column=0, padx=12, pady=(0, 2), sticky="w")
        self._model_status_label = ctk.CTkLabel(
            settings_frame, text="○  Loading model…",
            font=ctk.CTkFont(size=11), text_color="#888888",
        )
        self._model_status_label.grid(row=2, column=0, padx=12, pady=(0, 10), sticky="w")

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

        # ── Refresh ───────────────────────────────────────────────────────────
        ctk.CTkButton(
            self, text="↺  Refresh device list", width=200,
            fg_color="transparent", border_width=1,
            command=self._load_devices
        ).grid(row=5, column=0, pady=(8, 0))

        # ── Start / Stop ──────────────────────────────────────────────────────
        self._start_btn = ctk.CTkButton(
            self, text="▶  Start Recording", height=44,
            font=ctk.CTkFont(size=15, weight="bold"),
            fg_color="#2d6a4f", hover_color="#1b4332",
            command=self._toggle_recording
        )
        self._start_btn.grid(row=6, column=0, padx=20, pady=12, sticky="ew")

        # ── Output ────────────────────────────────────────────────────────────
        output_container = ctk.CTkFrame(self, fg_color="transparent")
        output_container.grid(row=7, column=0, sticky="nsew", padx=20, pady=(0, 16))
        output_container.grid_columnconfigure(0, weight=2)
        output_container.grid_columnconfigure(1, weight=1)
        output_container.grid_rowconfigure(0, weight=1)

        # Live transcription panel — parsed "ME:"/"OTHER:" lines, colour-coded
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
        self._transcript_box.tag_config("me", foreground=COL_MIC)
        self._transcript_box.tag_config("other", foreground=COL_SPK)
        self._transcript_box.tag_config("dim", foreground="#888888")

        # Raw process log panel — everything the transcriber subprocess prints
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

    # ── Chart construction ────────────────────────────────────────────────────

    def _build_chart(self, parent) -> FigureCanvasTkAgg:
        fig = Figure(figsize=(6.5, 2.2), dpi=96, facecolor=BG_DARK)
        fig.subplots_adjust(left=0.05, right=0.98, top=0.88, bottom=0.12, hspace=0.55)

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

    def _spawn_transcriber_process(self, model_size):
        """Launch transcriber.py in --serve (standby) mode so it starts
        loading the Whisper model immediately, well before Start is clicked."""
        cmd = [PYTHON, "-u", TRANSCRIBER_SCRIPT, "--serve", "--model", model_size]
        self._log(f"Starting transcriber (model={model_size})...\n{'─'*60}\n")
        self._proc = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, bufsize=1,
            cwd=os.path.dirname(TRANSCRIBER_SCRIPT),
            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP,
        )
        self._spawned_model = model_size
        self._model_ready = False
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
        if self._model_ready:
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
        self._spawn_transcriber_process(value)

    # ── Recording control ─────────────────────────────────────────────────────

    def _toggle_recording(self):
        if not self._recording_active:
            self._start_recording()
        else:
            self._stop_recording()

    def _start_recording(self):
        if self._proc is None or self._proc.poll() is not None:
            self._log("[warn] Transcriber process not running — restarting it.\n")
            self._spawn_transcriber_process(self._model_var.get())

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
        if text == "MODEL_READY":
            self._model_ready = True
            self.after(0, self._update_model_status_label)
        elif text == "SESSION_ENDED":
            self.after(0, self._on_session_ended)

    def _on_session_ended(self):
        self._recording_active = False
        self._monitor.stop()
        self._start_btn.configure(text="▶  Start Recording", state="normal",
                                  fg_color="#2d6a4f", hover_color="#1b4332")
        self._model_menu.configure(state="normal")

    def _on_process_crashed(self):
        self._proc = None
        self._recording_active = False
        self._monitor.stop()
        self._start_btn.configure(text="▶  Start Recording", state="normal",
                                  fg_color="#2d6a4f", hover_color="#1b4332")
        self._model_menu.configure(state="normal")
        self._log("\n── Transcriber process ended unexpectedly — restarting… ──\n")
        self._spawn_transcriber_process(self._model_var.get())

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

    # ── Clean shutdown ────────────────────────────────────────────────────────

    def on_close(self):
        self._monitor.stop()
        self._terminate_transcriber_process()
        self.destroy()


if __name__ == "__main__":
    app = LauncherApp()
    app.protocol("WM_DELETE_WINDOW", app.on_close)
    app.mainloop()
