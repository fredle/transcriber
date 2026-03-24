import numpy as np
import sounddevice as sd
import soundfile as sf
import threading
import signal
import sys
from datetime import datetime

# Configuration
LOOPBACK_DEVICE_ID = 26  # System audio (Teams)
MICROPHONE_DEVICE_ID = 10  # Microphone
SAMPLE_RATE = 48000  # Hz
CHANNELS = 2  # Stereo
DTYPE = np.int16
CHUNK_SIZE = 1024

# Shared buffers and synchronization
loopback_buffer = []
mic_buffer = []
buffer_lock = threading.Lock()
recording = True

# Output file setup
timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
output_filename = f"mixed_audio_{timestamp}.wav"
output_file = None


def loopback_callback(indata, frames, time_info, status):
    """Callback for loopback device (system audio)"""
    if status:
        print(f"Loopback status: {status}", file=sys.stderr)
    
    if recording:
        with buffer_lock:
            # Convert to int16 and store
            audio_data = (indata * 32767).astype(np.int16)
            loopback_buffer.append(audio_data.copy())


def microphone_callback(indata, frames, time_info, status):
    """Callback for microphone device"""
    if status:
        print(f"Microphone status: {status}", file=sys.stderr)
    
    if recording:
        with buffer_lock:
            # Convert to int16 and store
            audio_data = (indata * 32767).astype(np.int16)
            mic_buffer.append(audio_data.copy())


def mix_and_save():
    """Mix audio from both buffers and save to file"""
    global output_file
    
    with buffer_lock:
        # Determine the minimum length to process
        min_len = min(len(loopback_buffer), len(mic_buffer))
        
        if min_len > 0:
            # Get chunks to process
            loopback_chunks = loopback_buffer[:min_len]
            mic_chunks = mic_buffer[:min_len]
            
            # Remove processed chunks
            loopback_buffer[:] = loopback_buffer[min_len:]
            mic_buffer[:] = mic_buffer[min_len:]
            
            # Mix audio (average to prevent clipping)
            for i in range(min_len):
                # Convert to float for mixing, then back to int16
                mixed = (loopback_chunks[i].astype(np.float32) + 
                        mic_chunks[i].astype(np.float32)) / 2.0
                mixed = mixed.astype(np.int16)
                
                # Write to file
                if output_file is not None:
                    output_file.write(mixed)


def save_thread_function():
    """Thread function that periodically mixes and saves audio"""
    while recording:
        mix_and_save()
        threading.Event().wait(0.1)  # Save every 100ms
    
    # Final save after recording stops
    mix_and_save()


def signal_handler(sig, frame):
    """Handle Ctrl+C gracefully"""
    global recording
    print("\n\nStopping recording...")
    recording = False


def main():
    global recording, output_file
    
    print("=" * 60)
    print("Mixed Audio Recorder")
    print("=" * 60)
    print(f"Loopback Device (System Audio): {LOOPBACK_DEVICE_ID}")
    print(f"Microphone Device: {MICROPHONE_DEVICE_ID}")
    print(f"Sample Rate: {SAMPLE_RATE} Hz")
    print(f"Channels: {CHANNELS}")
    print(f"Output File: {output_filename}")
    print("=" * 60)
    print("\nPress Ctrl+C to stop recording\n")
    
    # Set up signal handler for graceful shutdown
    signal.signal(signal.SIGINT, signal_handler)
    
    try:
        # Open output file
        output_file = sf.SoundFile(
            output_filename,
            mode='w',
            samplerate=SAMPLE_RATE,
            channels=CHANNELS,
            subtype='PCM_16'
        )
        
        # Start the save thread
        save_thread = threading.Thread(target=save_thread_function, daemon=True)
        save_thread.start()
        
        # Open both input streams with callbacks
        with sd.InputStream(
            device=LOOPBACK_DEVICE_ID,
            channels=CHANNELS,
            samplerate=SAMPLE_RATE,
            callback=loopback_callback,
            blocksize=CHUNK_SIZE
        ) as loopback_stream, \
        sd.InputStream(
            device=MICROPHONE_DEVICE_ID,
            channels=CHANNELS,
            samplerate=SAMPLE_RATE,
            callback=microphone_callback,
            blocksize=CHUNK_SIZE
        ) as mic_stream:
            
            print("Recording started...")
            print("Loopback stream active:", loopback_stream.active)
            print("Microphone stream active:", mic_stream.active)
            print()
            
            # Keep the main thread alive while recording
            while recording:
                sd.sleep(100)
        
        # Wait for save thread to finish
        save_thread.join(timeout=2.0)
        
        # Close output file
        if output_file is not None:
            output_file.close()
        
        print(f"\nRecording saved to: {output_filename}")
        
        # Calculate and display file info
        info = sf.info(output_filename)
        duration = info.duration
        print(f"Duration: {duration:.2f} seconds")
        print(f"File size: {info.frames * info.channels * 2 / 1024 / 1024:.2f} MB")
        print("\nDone!")
        
    except KeyboardInterrupt:
        print("\nRecording interrupted by user")
    except Exception as e:
        print(f"\nError: {e}", file=sys.stderr)
        import traceback
        traceback.print_exc()
    finally:
        recording = False
        if output_file is not None and not output_file.closed:
            output_file.close()
        print("\nCleanup complete")


if __name__ == "__main__":
    main()
