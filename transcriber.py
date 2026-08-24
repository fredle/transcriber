import os
os.environ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1"

import numpy as np
import sounddevice as sd
import soundfile as sf
import threading
import signal
import sys
import json
from datetime import datetime
from dotenv import load_dotenv
from faster_whisper import WhisperModel
from queue import Queue
import pyaudiowpatch as pyaudio_wp
import win32gui

load_dotenv()

# Force UTF-8 output so Whisper's Unicode transcriptions don't crash on Windows
if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
if hasattr(sys.stderr, 'reconfigure'):
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')

# Fallback device IDs (used if auto-detection fails)
FALLBACK_LOOPBACK_DEVICE_ID = 32
FALLBACK_MICROPHONE_DEVICE_ID = 1

SAMPLE_RATE = 48000  # Hz
CHANNELS = 1
DTYPE = np.int16
CHUNK_SIZE = 1024
SILENCE_THRESHOLD = 500  # Amplitude threshold for silence detection (adjustable)
SILENCE_DURATION = 1.0  # Seconds of silence before saving file


def get_teams_meeting_title():
    """
    Read the Microsoft Teams window title to extract the current meeting name.
    Returns the meeting title string, or None if Teams isn't in a meeting.
    Handles both ' | Microsoft Teams' and ' - Microsoft Teams' title formats.
    """
    titles = []

    def _enum_callback(hwnd, _):
        if win32gui.IsWindowVisible(hwnd):
            title = win32gui.GetWindowText(hwnd)
            if title:
                titles.append(title)

    try:
        win32gui.EnumWindows(_enum_callback, None)
    except Exception:
        return None

    for title in titles:
        lower = title.lower()
        if 'microsoft teams' not in lower:
            continue
        # Strip known suffixes to isolate the meeting name
        for sep in (' | Microsoft Teams', ' - Microsoft Teams',
                    ' | microsoft teams', ' - microsoft teams'):
            if title.lower().endswith(sep.lower()):
                meeting_name = title[: -len(sep)].strip()
                if meeting_name:
                    return meeting_name
    return None


def detect_teams_devices():
    """
    Detect which audio devices are currently set as the Windows Communications
    devices (mic and speaker). Teams uses these. Returns sounddevice mic ID and
    pyaudiowpatch loopback device info for the speaker.
    Falls back to hardcoded IDs if detection fails.
    """
    try:
        from pycaw.pycaw import IMMDeviceEnumerator, EDataFlow, ERole, PROPERTYKEY
        from pycaw.constants import CLSID_MMDeviceEnumerator
        from comtypes import CLSCTX_ALL, CoCreateInstance, GUID

        enumerator = CoCreateInstance(CLSID_MMDeviceEnumerator, IMMDeviceEnumerator, CLSCTX_ALL)

        def get_friendly_name(endpoint):
            store = endpoint.OpenPropertyStore(0)
            pkey = PROPERTYKEY()
            pkey.fmtid = GUID('{a45c254e-df1c-4efd-8020-67d146a850e0}')
            pkey.pid = 14
            return store.GetValue(pkey).GetValue()

        spk_ep = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender.value, ERole.eCommunications.value)
        mic_ep = enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture.value, ERole.eCommunications.value)
        spk_name = get_friendly_name(spk_ep)
        mic_name = get_friendly_name(mic_ep)

        # Find mic in sounddevice by name
        mic_device_id = None
        for i, dev in enumerate(sd.query_devices()):
            if dev['max_input_channels'] > 0 and mic_name.lower() in dev['name'].lower():
                mic_device_id = i
                break

        # Find WASAPI loopback device for the speaker using pyaudiowpatch
        p = pyaudio_wp.PyAudio()
        loopback_device_id = None
        loopback_channels = 2
        loopback_rate = SAMPLE_RATE
        try:
            for i in range(p.get_device_count()):
                dev_info = p.get_device_info_by_index(i)
                if dev_info.get('isLoopbackDevice', False) and spk_name.lower() in dev_info['name'].lower():
                    loopback_device_id = i
                    loopback_channels = int(dev_info['maxInputChannels'])
                    loopback_rate = int(dev_info['defaultSampleRate'])
                    break
        finally:
            p.terminate()

        if mic_device_id is None:
            print(f"  Warning: Could not find mic '{mic_name}' in sounddevice — using fallback ID {FALLBACK_MICROPHONE_DEVICE_ID}")
            mic_device_id = FALLBACK_MICROPHONE_DEVICE_ID
            mic_name = f"fallback (ID {FALLBACK_MICROPHONE_DEVICE_ID})"

        if loopback_device_id is None:
            print(f"  Warning: Could not find WASAPI loopback for '{spk_name}' — using fallback ID {FALLBACK_LOOPBACK_DEVICE_ID}")
            loopback_device_id = FALLBACK_LOOPBACK_DEVICE_ID
            loopback_channels = 1
            spk_name = f"fallback (ID {FALLBACK_LOOPBACK_DEVICE_ID})"

        return mic_device_id, mic_name, loopback_device_id, loopback_channels, loopback_rate, spk_name

    except Exception as e:
        print(f"  Device detection failed ({e}) — using fallback IDs")
        return (
            FALLBACK_MICROPHONE_DEVICE_ID, f"fallback (ID {FALLBACK_MICROPHONE_DEVICE_ID})",
            FALLBACK_LOOPBACK_DEVICE_ID, 1, SAMPLE_RATE, f"fallback (ID {FALLBACK_LOOPBACK_DEVICE_ID})"
        )


# Shared buffers and synchronization — separate locks so mic and loopback
# callbacks never contend with each other.
loopback_buffer = []
mic_buffer = []
mic_lock      = threading.Lock()
loopback_lock = threading.Lock()
recording = True

# Output file and folder setup
session_folder = None
mic_output_file = None
speaker_output_file = None
mic_file_counter = 1
speaker_file_counter = 1
mic_silence_start_time = None
speaker_silence_start_time = None
mic_audio_buffer = []
speaker_audio_buffer = []
mic_has_speech = False  # Track if current segment has any speech
speaker_has_speech = False

# Whisper model for transcription
whisper_model = None
current_model_size = None  # set by ensure_whisper_loaded(), for display purposes
whisper_model_ready = threading.Event()
transcribe_language = None  # None = auto-detect; set to e.g. "en" via --language
transcription_file = None
transcription_queue = Queue()
transcription_thread = None


def loopback_callback(indata, frames, time_info, status):
    """Callback for loopback device (system audio)"""
    if status:
        print(f"Loopback status: {status}", file=sys.stderr)
    if recording:
        audio_data = (indata * 32767).astype(np.int16)  # outside lock
        with loopback_lock:
            loopback_buffer.append(audio_data)


def microphone_callback(indata, frames, time_info, status):
    """Callback for microphone device"""
    if status:
        print(f"Microphone status: {status}", file=sys.stderr)
    if recording:
        audio_data = (indata * 32767).astype(np.int16)  # outside lock
        with mic_lock:
            mic_buffer.append(audio_data)


def loopback_capture_thread_function(device_id, channels, sample_rate):
    """Capture WASAPI loopback audio using pyaudiowpatch in a dedicated thread"""
    p = pyaudio_wp.PyAudio()
    stream = p.open(
        format=pyaudio_wp.paInt16,
        channels=channels,
        rate=sample_rate,
        input=True,
        input_device_index=device_id,
        frames_per_buffer=CHUNK_SIZE
    )
    try:
        while recording:
            data = stream.read(CHUNK_SIZE, exception_on_overflow=False)
            audio_np = np.frombuffer(data, dtype=np.int16)
            # Mix down to mono by taking the first channel
            if channels > 1:
                audio_np = audio_np.reshape(-1, channels)[:, 0]
            audio_np = audio_np.reshape(-1, 1)
            with loopback_lock:
                loopback_buffer.append(audio_np.copy())
    finally:
        stream.stop_stream()
        stream.close()
        p.terminate()


def is_silent(audio_chunk):
    """Check if an audio chunk is silent based on amplitude threshold"""
    return np.abs(audio_chunk).mean() < SILENCE_THRESHOLD


def transcribe_audio_file(audio_filepath):
    """Transcribe an audio file using faster_whisper"""
    global whisper_model, transcription_file
    
    try:
        # Determine speaker from filename
        filename = os.path.basename(audio_filepath)
        if filename.startswith('mic_'):
            speaker = "[ME]"
        elif filename.startswith('speaker_'):
            speaker = "[OTHER]"
        else:
            speaker = "[UNKNOWN]"
        
        #print(f"Transcribing {filename}...")
        
        # Transcribe the audio
        segments, info = whisper_model.transcribe(
            audio_filepath,
            beam_size=2,
            language=transcribe_language,
            vad_filter=True,
        )
        
        # Collect transcription text and timing
        transcription_text = ""
        start_time = None
        end_time = None
        
        for segment in segments:
            transcription_text += segment.text + " "
            if start_time is None:
                start_time = segment.start
            end_time = segment.end
        
        transcription_text = transcription_text.strip()
        
        if transcription_text:
            # Print to console
            speaker_label = "ME" if speaker == "[ME]" else "OTHER"
            print(f"{speaker_label}: {transcription_text}")
            
            # Write to transcription file in JSONL format
            if transcription_file is not None:
                transcription_entry = {
                    "timestamp": datetime.now().isoformat(),
                    "speaker": speaker,
                    "wav_file": filename,
                    "start_time": round(start_time, 2) if start_time is not None else 0.0,
                    "end_time": round(end_time, 2) if end_time is not None else 0.0,
                    "text": transcription_text
                }
                transcription_file.write(json.dumps(transcription_entry) + "\n")
                transcription_file.flush()
        else:
            pass
            #print(f"[Transcription] {speaker} {filename}: (no speech detected)\n")
            
    except Exception as e:
        print(f"Error transcribing {audio_filepath}: {e}")


def transcription_worker():
    """Worker thread that processes transcription queue"""
    whisper_model_ready.wait()  # Block until model is loaded
    while True:
        audio_filepath = transcription_queue.get()

        # None is the signal to stop
        if audio_filepath is None:
            transcription_queue.task_done()
            break

        try:
            transcribe_audio_file(audio_filepath)
        finally:
            transcription_queue.task_done()
            remaining = transcription_queue.qsize()
            if remaining > 0:
                print(f"[Queue: {remaining} file{'s' if remaining != 1 else ''} pending]")


def create_new_mic_file():
    """Create a new output file for microphone in the session folder"""
    global mic_output_file, mic_file_counter, mic_audio_buffer, mic_has_speech
    
    # Close previous file if open and queue it for transcription
    if mic_output_file is not None and not mic_output_file.closed:
        previous_filename = mic_output_file.name
        
        # Only save and transcribe if there was speech in the segment
        if mic_has_speech:
            mic_output_file.close()
            
            # Queue the file for transcription in background thread
            transcription_queue.put(previous_filename)
        else:
            # Discard the file if no speech detected
            mic_output_file.close()
            try:
                os.remove(previous_filename)
            except Exception as e:
                print(f"Error removing empty file: {e}")
    
    # Create new filename
    filename = os.path.join(session_folder, f"mic_{mic_file_counter:04d}.wav")
    
    # Open new file
    mic_output_file = sf.SoundFile(
        filename,
        mode='w',
        samplerate=SAMPLE_RATE,
        channels=CHANNELS,
        subtype='PCM_16'
    )
    
    mic_file_counter += 1
    mic_audio_buffer = []
    mic_has_speech = False  # Reset for new segment


def create_new_speaker_file():
    """Create a new output file for speaker in the session folder"""
    global speaker_output_file, speaker_file_counter, speaker_audio_buffer, speaker_has_speech
    
    # Close previous file if open and queue it for transcription
    if speaker_output_file is not None and not speaker_output_file.closed:
        previous_filename = speaker_output_file.name
        
        # Only save and transcribe if there was speech in the segment
        if speaker_has_speech:
            speaker_output_file.close()
            
            # Queue the file for transcription in background thread
            transcription_queue.put(previous_filename)
        else:
            # Discard the file if no speech detected
            speaker_output_file.close()
            try:
                os.remove(previous_filename)
            except Exception as e:
                print(f"Error removing empty file: {e}")
    
    # Create new filename
    filename = os.path.join(session_folder, f"speaker_{speaker_file_counter:04d}.wav")
    
    # Open new file
    speaker_output_file = sf.SoundFile(
        filename,
        mode='w',
        samplerate=SAMPLE_RATE,
        channels=CHANNELS,
        subtype='PCM_16'
    )
    
    speaker_file_counter += 1
    speaker_audio_buffer = []
    speaker_has_speech = False  # Reset for new segment


def process_and_save():
    """Process audio from both buffers separately, detect silence, and save to files"""
    global mic_output_file, speaker_output_file
    global mic_silence_start_time, speaker_silence_start_time
    global mic_audio_buffer, speaker_audio_buffer
    global mic_has_speech, speaker_has_speech

    # Drain each buffer under its own lock to avoid cross-stream contention
    with mic_lock:
        mic_chunks = mic_buffer[:]
        mic_buffer[:] = []
    with loopback_lock:
        speaker_chunks = loopback_buffer[:]
        loopback_buffer[:] = []

    # Process microphone chunks outside the lock so callbacks never block
    for chunk in mic_chunks:
        mic_audio_buffer.append(chunk)

        if is_silent(chunk):
            if mic_silence_start_time is None:
                mic_silence_start_time = datetime.now()
            else:
                silence_duration = (datetime.now() - mic_silence_start_time).total_seconds()
                if silence_duration >= SILENCE_DURATION:
                    if mic_output_file is not None:
                        for audio_chunk in mic_audio_buffer:
                            mic_output_file.write(audio_chunk)
                    create_new_mic_file()
                    mic_silence_start_time = None
        else:
            mic_has_speech = True
            mic_silence_start_time = None

            if len(mic_audio_buffer) > SAMPLE_RATE * 2:
                if mic_output_file is not None:
                    for audio_chunk in mic_audio_buffer:
                        mic_output_file.write(audio_chunk)
                mic_audio_buffer = []

    # Process speaker (loopback) chunks outside the lock
    for chunk in speaker_chunks:
        speaker_audio_buffer.append(chunk)

        if is_silent(chunk):
            if speaker_silence_start_time is None:
                speaker_silence_start_time = datetime.now()
            else:
                silence_duration = (datetime.now() - speaker_silence_start_time).total_seconds()
                if silence_duration >= SILENCE_DURATION:
                    if speaker_output_file is not None:
                        for audio_chunk in speaker_audio_buffer:
                            speaker_output_file.write(audio_chunk)
                    create_new_speaker_file()
                    speaker_silence_start_time = None
        else:
            speaker_has_speech = True
            speaker_silence_start_time = None

            if len(speaker_audio_buffer) > SAMPLE_RATE * 2:
                if speaker_output_file is not None:
                    for audio_chunk in speaker_audio_buffer:
                        speaker_output_file.write(audio_chunk)
                speaker_audio_buffer = []


def save_thread_function():
    """Thread function that periodically processes and saves audio"""
    while recording:
        process_and_save()
        threading.Event().wait(0.1)  # Process every 100ms
    
    # Final save after recording stops
    process_and_save()
    
    # Write any remaining buffered audio
    global mic_audio_buffer, speaker_audio_buffer, mic_output_file, speaker_output_file
    if mic_output_file is not None and len(mic_audio_buffer) > 0:
        for chunk in mic_audio_buffer:
            mic_output_file.write(chunk)
    
    if speaker_output_file is not None and len(speaker_audio_buffer) > 0:
        for chunk in speaker_audio_buffer:
            speaker_output_file.write(chunk)


def signal_handler(sig, frame):
    """Handle Ctrl+C / CTRL_BREAK gracefully"""
    global recording
    print("\n\nStopping recording...")
    recording = False


# On Windows, the launcher sends CTRL_BREAK_EVENT which maps to SIGBREAK
if hasattr(signal, 'SIGBREAK'):
    signal.signal(signal.SIGBREAK, signal_handler)


def ensure_whisper_loaded(model_size):
    """
    Kick off Whisper model loading in the background if it isn't already
    loaded (or being loaded). Safe to call once at process startup so the
    model is warm well before a recording session begins.
    """
    global whisper_model, current_model_size
    if whisper_model is not None:
        return
    current_model_size = model_size

    def _load():
        global whisper_model
        print(f"Loading Whisper model ({model_size})...")
        whisper_model = WhisperModel(model_size, device="cpu", compute_type="int8")
        print("Whisper model loaded.")
        print("MODEL_READY", flush=True)
        whisper_model_ready.set()
    threading.Thread(target=_load, daemon=True).start()


def _reset_session_globals():
    """Reset all per-session state so a standby process can run repeated
    start/stop cycles without restarting (and re-loading the model)."""
    global recording, mic_buffer, loopback_buffer
    global mic_output_file, speaker_output_file, mic_file_counter, speaker_file_counter
    global mic_silence_start_time, speaker_silence_start_time
    global mic_audio_buffer, speaker_audio_buffer, mic_has_speech, speaker_has_speech
    global transcription_queue, transcription_thread

    recording = True
    mic_buffer = []
    loopback_buffer = []
    mic_output_file = None
    speaker_output_file = None
    mic_file_counter = 1
    speaker_file_counter = 1
    mic_silence_start_time = None
    speaker_silence_start_time = None
    mic_audio_buffer = []
    speaker_audio_buffer = []
    mic_has_speech = False
    speaker_has_speech = False
    transcription_queue = Queue()
    transcription_thread = None


def _resolve_devices(override_mic_id=None, override_loopback_id=None):
    """Detect Teams' active mic/speaker devices, applying any manual overrides.
    Returns (mic_device_id, mic_name, loopback_device_id, loopback_channels, loopback_rate, spk_name)."""
    print("Detecting Teams audio devices...")
    mic_device_id, mic_name, loopback_device_id, loopback_channels, loopback_rate, spk_name = detect_teams_devices()

    if override_mic_id is not None:
        mic_device_id = override_mic_id
        devs = sd.query_devices()
        mic_name = devs[mic_device_id]['name'] if mic_device_id < len(devs) else f"ID {mic_device_id}"
    if override_loopback_id is not None:
        p = pyaudio_wp.PyAudio()
        try:
            dev_info = p.get_device_info_by_index(override_loopback_id)
            loopback_device_id = override_loopback_id
            loopback_channels = int(dev_info['maxInputChannels'])
            loopback_rate = int(dev_info['defaultSampleRate'])
            spk_name = dev_info['name']
        finally:
            p.terminate()

    print(f"  Mic:     [{mic_device_id}] {mic_name}")
    print(f"  Speaker: [{loopback_device_id}] {spk_name} (WASAPI loopback, {loopback_channels}ch @ {loopback_rate}Hz)")
    print()
    return mic_device_id, mic_name, loopback_device_id, loopback_channels, loopback_rate, spk_name


def _run_session(override_mic_id=None, override_loopback_id=None, language=None):
    global recording, output_file, session_folder, transcription_file, transcription_thread
    global transcribe_language
    transcribe_language = language  # None = Whisper auto-detects

    mic_device_id, mic_name, loopback_device_id, loopback_channels, loopback_rate, spk_name = \
        _resolve_devices(override_mic_id, override_loopback_id)

    # Create session folder
    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    session_folder = f"recording_{timestamp}"
    os.makedirs(session_folder, exist_ok=True)

    # Try to read the active Teams meeting title from the window title bar
    meeting_title = get_teams_meeting_title()
    if meeting_title:
        print(f"  Meeting: {meeting_title}")
    else:
        print("  Meeting: (not detected — Teams not open or not in a meeting)")
    print()

    # Create transcription output file
    transcription_filename = os.path.join(session_folder, "transcriptions.jsonl")
    transcription_file = open(transcription_filename, 'w', encoding='utf-8')
    # Write session metadata as first line
    session_entry = {
        "session_start": datetime.now().isoformat(),
        "session_id": timestamp,
        "type": "session_metadata",
        "meeting_title": meeting_title,
    }
    transcription_file.write(json.dumps(session_entry) + "\n")
    transcription_file.flush()

    # Start transcription worker thread
    transcription_thread = threading.Thread(target=transcription_worker, daemon=False)
    transcription_thread.start()

    print("=" * 60)
    print("Split Audio Recorder with Silence Detection")
    print("=" * 60)
    print(f"Session Folder:  {session_folder}")
    if meeting_title:
        print(f"Meeting Title:   {meeting_title}")
    print(f"Mic device:      [{mic_device_id}] {mic_name}")
    print(f"Speaker device:  [{loopback_device_id}] {spk_name} (WASAPI loopback)")
    print(f"Whisper model:   {current_model_size}")
    print(f"Language:        {language if language else 'auto-detect'}")
    print(f"Sample Rate:     {SAMPLE_RATE} Hz")
    print(f"Silence Threshold: {SILENCE_THRESHOLD}")
    print(f"Silence Duration: {SILENCE_DURATION} seconds")
    print("=" * 60)
    print("Recording microphone as [ME] and speaker as [OTHER]")
    print("Press Ctrl+C to stop recording\n")

    # Set up signal handler for graceful shutdown
    signal.signal(signal.SIGINT, signal_handler)

    try:
        # Create first output files for both mic and speaker
        create_new_mic_file()
        create_new_speaker_file()

        # Start the save thread
        save_thread = threading.Thread(target=save_thread_function, daemon=True)
        save_thread.start()

        # Start loopback capture thread (WASAPI loopback via pyaudiowpatch)
        loopback_thread = threading.Thread(
            target=loopback_capture_thread_function,
            args=(loopback_device_id, loopback_channels, loopback_rate),
            daemon=True
        )
        loopback_thread.start()

        # Open microphone input stream with sounddevice callback
        with sd.InputStream(
            device=mic_device_id,
            channels=CHANNELS,
            samplerate=SAMPLE_RATE,
            callback=microphone_callback,
            blocksize=CHUNK_SIZE,
            latency='high'
        ) as mic_stream:

            print("Recording started...")
            print("Loopback thread active:", loopback_thread.is_alive())
            print("Microphone stream active:", mic_stream.active)
            print()
            
            # Keep the main thread alive while recording
            while recording:
                sd.sleep(100)

        # Wait for loopback and save threads to finish
        loopback_thread.join(timeout=2.0)
        save_thread.join(timeout=2.0)
        
        # Close mic output file and queue final segment for transcription
        if mic_output_file is not None:
            final_mic_filename = mic_output_file.name
            
            # Only save and transcribe final file if it has speech
            if mic_has_speech:
                mic_output_file.close()
                # Queue the final file for transcription
                transcription_queue.put(final_mic_filename)
            else:
                # Discard the final file if no speech detected
                mic_output_file.close()
                try:
                    os.remove(final_mic_filename)
                    print("Discarded final mic file (no speech detected)")
                except Exception as e:
                    print(f"Error removing empty file: {e}")
        
        # Close speaker output file and queue final segment for transcription
        if speaker_output_file is not None:
            final_speaker_filename = speaker_output_file.name
            
            # Only save and transcribe final file if it has speech
            if speaker_has_speech:
                speaker_output_file.close()
                # Queue the final file for transcription
                transcription_queue.put(final_speaker_filename)
            else:
                # Discard the final file if no speech detected
                speaker_output_file.close()
                try:
                    os.remove(final_speaker_filename)
                    print("Discarded final speaker file (no speech detected)")
                except Exception as e:
                    print(f"Error removing empty file: {e}")
        
        # Signal transcription thread to stop and wait for queue to complete
        print("\nWaiting for transcriptions to complete...")
        transcription_queue.put(None)  # Signal to stop
        transcription_thread.join()
        
        print(f"\nRecording saved to folder: {session_folder}")
        
        # Display info about all files
        print(f"\nTotal mic segments created: {mic_file_counter - 1}")
        print(f"Total speaker segments created: {speaker_file_counter - 1}")
        print("\nSegment details:")
        mic_files = []
        speaker_files = []
        for filename in sorted(os.listdir(session_folder)):
            if filename.endswith('.wav'):
                if filename.startswith('mic_'):
                    mic_files.append(filename)
                elif filename.startswith('speaker_'):
                    speaker_files.append(filename)
        
        if mic_files:
            print("\n  Microphone (ME):")
            for filename in mic_files:
                filepath = os.path.join(session_folder, filename)
                info = sf.info(filepath)
                print(f"    {filename}: {info.duration:.2f} seconds, {info.frames * info.channels * 2 / 1024:.2f} KB")
        
        if speaker_files:
            print("\n  Speaker (OTHER):")
            for filename in speaker_files:
                filepath = os.path.join(session_folder, filename)
                info = sf.info(filepath)
                print(f"    {filename}: {info.duration:.2f} seconds, {info.frames * info.channels * 2 / 1024:.2f} KB")
        
        print("\nDone!")
        
    except KeyboardInterrupt:
        print("\nRecording interrupted by user")
    except Exception as e:
        print(f"\nError: {e}", file=sys.stderr)
        import traceback
        traceback.print_exc()
    finally:
        recording = False
        if mic_output_file is not None and not mic_output_file.closed:
            mic_output_file.close()
        if speaker_output_file is not None and not speaker_output_file.closed:
            speaker_output_file.close()
        
        # Ensure transcription thread is stopped
        if transcription_thread is not None and transcription_thread.is_alive():
            transcription_queue.put(None)  # Signal to stop
            transcription_thread.join(timeout=30.0)
        
        if transcription_file is not None and not transcription_file.closed:
            transcription_file.close()
            print(f"Transcriptions saved to: {transcription_filename}")
        print("\nCleanup complete")

    print("SESSION_ENDED", flush=True)


def _run_assemblyai_session(override_mic_id=None, override_loopback_id=None, language=None):
    """
    Realtime engine: stream mic + speaker audio straight to AssemblyAI's
    Universal-3.5 Pro API (with diarization) instead of local Whisper — no
    WAV segmenting, no silence detection, transcripts arrive live. Requires
    ASSEMBLY_AI_TOKEN in .env. Diarized speaker labels (e.g. "Speaker A")
    are attached to the [OTHER] channel, which typically mixes multiple
    remote participants; the mic channel is always a single speaker so its
    label isn't shown.
    """
    global session_folder, transcription_file

    api_key = os.environ.get("ASSEMBLY_AI_TOKEN")
    if not api_key:
        print("Error: ASSEMBLY_AI_TOKEN not set in .env — cannot use the AssemblyAI engine.", file=sys.stderr)
        print("SESSION_ENDED", flush=True)
        return

    from assembly_streaming import AssemblyChannelStreamer

    mic_device_id, mic_name, loopback_device_id, loopback_channels, loopback_rate, spk_name = \
        _resolve_devices(override_mic_id, override_loopback_id)

    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    session_folder = f"recording_{timestamp}"
    os.makedirs(session_folder, exist_ok=True)

    meeting_title = get_teams_meeting_title()
    if meeting_title:
        print(f"  Meeting: {meeting_title}")
    else:
        print("  Meeting: (not detected — Teams not open or not in a meeting)")
    print()

    transcription_filename = os.path.join(session_folder, "transcriptions.jsonl")
    transcription_file = open(transcription_filename, 'w', encoding='utf-8')
    transcription_file.write(json.dumps({
        "session_start": datetime.now().isoformat(),
        "session_id": timestamp,
        "type": "session_metadata",
        "meeting_title": meeting_title,
        "engine": "assemblyai",
    }) + "\n")
    transcription_file.flush()

    def _make_turn_handler(speaker_tag):
        def _on_final_turn(text, speaker_label, start_ms, end_ms):
            display_text = f"[{speaker_label}] {text}" if (speaker_tag == "OTHER" and speaker_label) else text
            print(f"{speaker_tag}: {display_text}")
            entry = {
                "timestamp": datetime.now().isoformat(),
                "speaker": f"[{speaker_tag}]",
                "speaker_label": speaker_label,
                "start_time": round(start_ms / 1000.0, 2),
                "end_time": round(end_ms / 1000.0, 2),
                "text": text,
            }
            transcription_file.write(json.dumps(entry) + "\n")
            transcription_file.flush()
        return _on_final_turn

    mic_streamer = AssemblyChannelStreamer(
        api_key=api_key, sample_rate=SAMPLE_RATE, label="mic",
        on_final_turn=_make_turn_handler("ME"),
    )
    speaker_streamer = AssemblyChannelStreamer(
        api_key=api_key, sample_rate=loopback_rate, label="speaker",
        on_final_turn=_make_turn_handler("OTHER"),
    )

    print("=" * 60)
    print("AssemblyAI Realtime Transcriber (Universal-3.5 Pro, diarized)")
    print("=" * 60)
    print(f"Session Folder:  {session_folder}")
    if meeting_title:
        print(f"Meeting Title:   {meeting_title}")
    print(f"Mic device:      [{mic_device_id}] {mic_name}")
    print(f"Speaker device:  [{loopback_device_id}] {spk_name} (WASAPI loopback)")
    print(f"Language:        {language if language else 'auto-detect (code-switching)'}")
    print("=" * 60)
    print("Streaming microphone as [ME] and speaker as [OTHER] to AssemblyAI")
    print("Press Ctrl+C to stop recording\n")

    signal.signal(signal.SIGINT, signal_handler)

    def _mic_callback(indata, frames, time_info, status):
        if status:
            print(f"Microphone status: {status}", file=sys.stderr)
        if recording:
            mic_streamer.feed((indata * 32767).astype(np.int16).tobytes())

    def _loopback_stream_thread():
        try:
            p = pyaudio_wp.PyAudio()
            stream = p.open(
                format=pyaudio_wp.paInt16, channels=loopback_channels,
                rate=loopback_rate, input=True, input_device_index=loopback_device_id,
                frames_per_buffer=CHUNK_SIZE,
            )
        except Exception as e:
            print(f"Loopback device open failed: {e}", file=sys.stderr)
            return
        try:
            while recording:
                data = stream.read(CHUNK_SIZE, exception_on_overflow=False)
                if loopback_channels > 1:
                    data = np.frombuffer(data, dtype=np.int16).reshape(-1, loopback_channels)[:, 0].tobytes()
                speaker_streamer.feed(data)
        finally:
            stream.stop_stream()
            stream.close()
            p.terminate()

    try:
        mic_streamer.start()
        speaker_streamer.start()

        loopback_thread = threading.Thread(target=_loopback_stream_thread, daemon=True)
        loopback_thread.start()

        with sd.InputStream(
            device=mic_device_id, channels=CHANNELS, samplerate=SAMPLE_RATE,
            callback=_mic_callback, blocksize=CHUNK_SIZE, latency='high',
        ) as mic_stream:
            print("Recording started...")
            print("Loopback thread active:", loopback_thread.is_alive())
            print("Microphone stream active:", mic_stream.active)
            print()
            while recording:
                sd.sleep(100)

        loopback_thread.join(timeout=2.0)

    except KeyboardInterrupt:
        print("\nRecording interrupted by user")
    except Exception as e:
        print(f"\nError: {e}", file=sys.stderr)
        import traceback
        traceback.print_exc()
    finally:
        print("\nStopping AssemblyAI streams (finalizing diarization)...")
        mic_streamer.stop()
        speaker_streamer.stop()
        if transcription_file is not None and not transcription_file.closed:
            transcription_file.close()
            print(f"Transcriptions saved to: {transcription_filename}")
        print(f"\nRecording saved to folder: {session_folder}")
        print("\nCleanup complete")

    print("SESSION_ENDED", flush=True)


def main(override_mic_id=None, override_loopback_id=None, model_size="base", language=None, engine="whisper"):
    """Standalone single-shot entry point (direct CLI use): run one recording
    session using the selected engine, then exit."""
    _reset_session_globals()
    if engine == "whisper":
        ensure_whisper_loaded(model_size)
        _run_session(override_mic_id, override_loopback_id, language)
    else:
        _run_assemblyai_session(override_mic_id, override_loopback_id, language)


def serve(initial_model_size, engine="whisper"):
    """
    Persistent standby mode used by the launcher: for the "whisper" engine,
    preloads the Whisper model immediately; the "assemblyai" engine has no
    local model to warm up (transcription runs in AssemblyAI's cloud), so
    it's ready as soon as the API key is confirmed present. Either way,
    waits for JSON control lines on stdin so a recording session can start:
      {"cmd": "start", "mic_id": int|null, "loopback_id": int|null, "language": str|null}
      {"cmd": "stop"}   — ends the current session; process stays alive
      {"cmd": "quit"}   — ends any current session and exits
    """
    if engine == "whisper":
        print("Standby mode — preloading Whisper model in background...")
        ensure_whisper_loaded(initial_model_size)
    else:
        print("Standby mode — engine=assemblyai, no local model to preload.")
        if not os.environ.get("ASSEMBLY_AI_TOKEN"):
            print("Warning: ASSEMBLY_AI_TOKEN not set in .env — recording will fail to start.", file=sys.stderr)
        print("MODEL_READY", flush=True)

    control_queue = Queue()

    def _stdin_reader():
        global recording
        for raw_line in sys.stdin:
            line = raw_line.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue
            cmd = msg.get("cmd")
            if cmd == "start":
                control_queue.put(msg)
            elif cmd == "stop":
                recording = False
            elif cmd == "quit":
                recording = False
                control_queue.put(None)
                break

    threading.Thread(target=_stdin_reader, daemon=True).start()
    print("STANDBY_READY", flush=True)

    while True:
        msg = control_queue.get()
        if msg is None:
            break
        _reset_session_globals()
        if engine == "whisper":
            _run_session(
                override_mic_id=msg.get("mic_id"),
                override_loopback_id=msg.get("loopback_id"),
                language=msg.get("language"),
            )
        else:
            _run_assemblyai_session(
                override_mic_id=msg.get("mic_id"),
                override_loopback_id=msg.get("loopback_id"),
                language=msg.get("language"),
            )

    print("Standby mode exiting.")


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description="Split mic/speaker transcriber")
    parser.add_argument("--mic-id", type=int, default=None, help="sounddevice mic device ID (overrides auto-detect)")
    parser.add_argument("--loopback-id", type=int, default=None, help="pyaudiowpatch loopback device ID (overrides auto-detect)")
    parser.add_argument("--model", default="base", choices=["tiny", "base", "small", "medium", "large"], help="Whisper model size")
    parser.add_argument("--language", default=None, help="Language code e.g. 'en', or omit for auto-detect")
    parser.add_argument("--engine", default="whisper", choices=["whisper", "assemblyai"],
                         help="Transcription engine: local faster-whisper, or AssemblyAI realtime streaming with diarization")
    parser.add_argument("--serve", action="store_true",
                         help="Run in persistent standby mode, controlled via JSON lines on stdin (used by launcher.py)")
    args = parser.parse_args()
    if args.serve:
        serve(args.model, engine=args.engine)
    else:
        main(override_mic_id=args.mic_id, override_loopback_id=args.loopback_id,
             model_size=args.model, language=args.language, engine=args.engine)
