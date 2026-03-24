import sounddevice as sd
import soundfile as sf
import numpy as np
import time

SAMPLE_RATE = 48000
DURATION = 5  # seconds to record from each device

print("Enumerating all input devices...")
devices = sd.query_devices()


device_idx = 26
print(f"\nTesting device {device_idx}: {devices[device_idx]['name']}")
print("  Full details:")

try:
    print(f"  Recording {DURATION} seconds...")
    recording = sd.rec(int(DURATION * SAMPLE_RATE), samplerate=SAMPLE_RATE, channels=1, dtype='float32', device=device_idx)
    sd.wait()
    filename = f"output_mix.wav"
    sf.write(filename, recording, SAMPLE_RATE)
    print(f"  Saved to {filename}. Play this file to check if it captured your YouTube audio.")
except Exception as e:
    print(f"  Error recording from device {device_idx}: {e}")
time.sleep(1)

print("\nDone. Check the WAV files to find which device captured your system audio.")
