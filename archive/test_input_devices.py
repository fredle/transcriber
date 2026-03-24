import sounddevice as sd
import soundfile as sf
import numpy as np
import time

SAMPLE_RATE = 48000
DURATION = 5  # seconds to record from each device

print("Enumerating all input devices...")
devices = sd.query_devices()

# Explicitly test only device ID 26
input_devices = [26] 

print(f"Found {len(input_devices)} input devices.")

for idx in input_devices:
    device = devices[idx]
    print(f"\nTesting device {idx}: {device['name']}")
    print("  Full details:")
    for k, v in device.items():
        print(f"    {k}: {v}")
    try:
        print(f"  Recording {DURATION} seconds...")
        recording = sd.rec(int(DURATION * SAMPLE_RATE), samplerate=SAMPLE_RATE, channels=1, dtype='float32', device=idx)
        sd.wait()
        filename = f"test_device_{idx}.wav"
        sf.write(filename, recording, SAMPLE_RATE)
        print(f"  Saved to {filename}. Play this file to check if it captured your YouTube audio.")
    except Exception as e:
        print(f"  Error recording from device {idx}: {e}")
    time.sleep(1)

print("\nDone. Check the WAV files to find which device captured your system audio.")
