import sounddevice as sd
import soundfile as sf
import numpy as np
from threading import Thread
import os
from dotenv import load_dotenv

# Transcription imports
from faster_whisper import WhisperModel
import webrtcvad
import queue
import time

load_dotenv()
# List available devices
devices = sd.query_devices()
print("Available devices (full details):")
for i, device in enumerate(devices):
    print(f"\nDevice {i}:")
    for key, value in device.items():
        print(f"  {key}: {value}")

# Force loopback device to 26 (as found in your test)
loopback_device = 26
print(f"\nUsing device 26 for system audio: {devices[26]['name']}")

# Find microphone device - specifically Intel Smart Sound Technology
mic_device = None
for i, device in enumerate(devices):
    if 'intel' in device['name'].lower() and 'smart sound' in device['name'].lower():
        mic_device = i
        print(f"Found microphone at index {i}: {device['name']}")
        break

if mic_device is None:
    print("Intel Smart Sound microphone not found. Defaulting to first available microphone.")
    for i, device in enumerate(devices):
        if 'microphone' in device['name'].lower():
            mic_device = i
            print(f"Using microphone at index {i}: {device['name']}")
            break

if mic_device is None:
    print("No microphone found. Defaulting to input device.")
    mic_device = sd.default.device[0]






# Buffers for each source
mic_buffer = []
system_buffer = []
audio_buffer = []  # Final mixed buffer

# For system audio: use blocking sd.rec() in a background thread
import threading
system_recording_thread = None
system_recording_stop = threading.Event()

# Mixing thread control
import threading
mixing_thread = None
mixing_thread_stop = threading.Event()

# Choose which input to use: 'mic', 'system', or 'both'
INPUT_SOURCE = 'system'  # force system audio only for debugging

# For near-realtime processing
audio_queue = queue.Queue()
vad = webrtcvad.Vad(2)  # Aggressiveness: 0-3
WHISPER_MODEL = os.getenv('WHISPER_MODEL', 'tiny')
model = WhisperModel(WHISPER_MODEL, device='cpu', compute_type='int8')

# Parameters
SAMPLE_RATE = 16000
CHUNK_DURATION = 1.0  # seconds

# --- REFACTORED: Use blocking sd.rec() for system audio, no mixing/threading ---
CHUNK_SIZE = int(SAMPLE_RATE * CHUNK_DURATION)
OVERLAP = 0.5  # seconds
OVERLAP_SIZE = int(SAMPLE_RATE * OVERLAP)
STABLE_DELAY = 2  # seconds to wait before committing

def resample(audio, orig_sr, target_sr):
    if orig_sr == target_sr:
        return audio
    import librosa
    return librosa.resample(audio, orig_sr=orig_sr, target_sr=target_sr, axis=-1)

def vad_collector(audio, sample_rate, chunk_size, vad):
    vad_frame_ms = 30
    vad_frame_size = int(sample_rate * vad_frame_ms / 1000)
    voiced_frames = []
    for i in range(0, len(audio) - vad_frame_size + 1, vad_frame_size):
        frame = audio[i:i+vad_frame_size]
        is_speech = vad.is_speech((frame * 32768).astype(np.int16).tobytes(), sample_rate)
        if is_speech:
            voiced_frames.append(frame)
        elif voiced_frames:
            yield np.concatenate(voiced_frames)
            voiced_frames = []
    if voiced_frames:
        yield np.concatenate(voiced_frames)

def transcribe_stream():
    buffer = np.zeros(0, dtype=np.float32)
    last_commit_time = time.time()
    last_text = ""
    with open("transcription.txt", "a", encoding="utf-8") as f:
        while True:
            try:
                chunk = audio_queue.get(timeout=1)
            except queue.Empty:
                continue
            buffer = np.concatenate([buffer, chunk])
            if len(buffer) >= CHUNK_SIZE:
                window = buffer[-(CHUNK_SIZE+OVERLAP_SIZE):]
                for speech in vad_collector(window, SAMPLE_RATE, None, vad):
                    segments, info = model.transcribe(speech, beam_size=1)
                    text = " ".join([seg.text for seg in segments])
                    if text and text != last_text and time.time() - last_commit_time > STABLE_DELAY:
                        print(f"[TRANSCRIBED]: {text}")
                        f.write(text + "\n")
                        f.flush()
                        last_text = text
                        last_commit_time = time.time()
                buffer = buffer[-OVERLAP_SIZE:]

def mic_audio_callback(indata, frames, time_info, status):
    if status:
        print(f"Microphone status: {status}")
    mono = indata[:, 0] if indata.ndim > 1 else indata
    mono = resample(mono, 48000, SAMPLE_RATE)
    audio_queue.put(mono)
    audio_buffer.append(mono)

print("\nRecording... Press Ctrl+C to stop.")
transcribe_thread = Thread(target=transcribe_stream, daemon=True)
transcribe_thread.start()

streams = []  # Ensure streams is always defined
try:
    if INPUT_SOURCE == 'mic':
        mic_stream = sd.InputStream(
            samplerate=48000,
            device=mic_device,
            channels=1,
            dtype='float32',
            callback=mic_audio_callback,
            blocksize=CHUNK_SIZE
        )
        streams.append(mic_stream)
        for s in streams:
            s.start()
        while True:
            time.sleep(1)
    elif INPUT_SOURCE == 'system' and loopback_device is not None:
        import soundfile as sf
        while True:
            data = sd.rec(CHUNK_SIZE, samplerate=48000, channels=1, dtype='float32', device=loopback_device)
            sd.wait()
            mono = data[:, 0]
            mono = resample(mono, 48000, SAMPLE_RATE)
            audio_queue.put(mono)
            audio_buffer.append(mono)
    else:
        print("Invalid INPUT_SOURCE or device not set.")
except KeyboardInterrupt:
    print("\nTranscription stopped by user.")
except Exception as e:
    print(f"Error: {e}")
finally:
    for s in streams:
        s.stop()
        s.close()
    if audio_buffer:
        import soundfile as sf
        audio_data = np.concatenate(audio_buffer)
        sf.write("recorded_audio.flac", audio_data, SAMPLE_RATE, format="FLAC")
        print("Audio saved to recorded_audio.flac")
