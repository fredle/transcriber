import sounddevice as sd
import numpy as np

import wave

# Set parameters
SAMPLE_RATE = 44100
CHANNELS = 1
BLOCKSIZE = 1024

def mix_streams(indata1, indata2):
    # Mix and avoid clipping
    mixed = indata1.astype(np.float32) + indata2.astype(np.float32)
    mixed = np.clip(mixed, -1.0, 1.0)
    return mixed.astype(np.float32)

def main():
    print("Available devices:")
    print(sd.query_devices())

    mic_index = 10
    sys_index = 26

    def callback(indata, frames, time, status):
        # This callback is not used directly, see below
        pass

    # Open mic input as normal
    mic_stream = sd.InputStream(device=mic_index, channels=CHANNELS, samplerate=SAMPLE_RATE, blocksize=BLOCKSIZE)

    # Open system audio as WASAPI loopback only
    try:
        sys_stream = sd.InputStream(device=sys_index, channels=CHANNELS, samplerate=SAMPLE_RATE, blocksize=BLOCKSIZE, dtype='float32', latency='low', extra_settings=sd.WasapiSettings(loopback=True))
    except Exception as e:
        print(f"ERROR: Could not open device {sys_index} as WASAPI loopback: {e}\nMake sure you select a WASAPI output device and that your system supports loopback capture.")
        return


    # Prepare WAV file for writing
    wav_filename = "mixed_output.wav"
    wf = wave.open(wav_filename, 'wb')
    wf.setnchannels(CHANNELS)
    wf.setsampwidth(2)  # 16-bit audio
    wf.setframerate(SAMPLE_RATE)

    with mic_stream, sys_stream, wf:
        print("Mixing streams and saving to file. Press Ctrl+C to stop.")
        try:
            while True:
                mic_data, _ = mic_stream.read(BLOCKSIZE)
                sys_data, _ = sys_stream.read(BLOCKSIZE)
                mixed = mix_streams(mic_data, sys_data)
                # Convert float32 [-1, 1] to int16 for WAV
                mixed_int16 = np.int16(mixed * 32767)
                wf.writeframes(mixed_int16.tobytes())
        except KeyboardInterrupt:
            print(f"Stopped. Mixed audio saved to {wav_filename}")

if __name__ == "__main__":
    main()
