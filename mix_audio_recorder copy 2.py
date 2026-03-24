import numpy as np
import sounddevice as sd
import soundfile as sf
import threading
import signal
import sys
import os
from datetime import datetime
from faster_whisper import WhisperModel
from queue import Queue

# Configuration
LOOPBACK_DEVICE_ID = 26  # System audio (Teams)
MICROPHONE_DEVICE_ID = 10  # Microphone
SAMPLE_RATE = 48000  # Hz
CHANNELS = 2  # Stereo
DTYPE = np.int16
CHUNK_SIZE = 1024
SILENCE_THRESHOLD = 500  # Amplitude threshold for silence detection (adjustable)
SILENCE_DURATION = 2.0  # Seconds of silence before saving file

# Shared buffers and synchronization
loopback_buffer = []
mic_buffer = []
buffer_lock = threading.Lock()
recording = True

# Output file and folder setup
session_folder = None
output_file = None
file_counter = 1
silence_start_time = None
current_audio_buffer = []
has_speech = False  # Track if current segment has any speech

# Whisper model for transcription
whisper_model = None
transcription_file = None
transcription_queue = Queue()
transcription_thread = None


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


def is_silent(audio_chunk):
    """Check if an audio chunk is silent based on amplitude threshold"""
    return np.abs(audio_chunk).mean() < SILENCE_THRESHOLD


def transcribe_audio_file(audio_filepath):
    """Transcribe an audio file using faster_whisper"""
    global whisper_model, transcription_file
    
    try:
        print(f"Transcribing {os.path.basename(audio_filepath)}...")
        
        # Transcribe the audio
        segments, info = whisper_model.transcribe(audio_filepath, beam_size=5)
        
        # Collect transcription text
        transcription_text = ""
        for segment in segments:
            transcription_text += segment.text + " "
        
        transcription_text = transcription_text.strip()
        
        if transcription_text:
            # Print to console
            print(f"\n[Transcription] {os.path.basename(audio_filepath)}:")
            print(f"  {transcription_text}\n")
            
            # Write to transcription file
            if transcription_file is not None:
                transcription_file.write(f"\n{'='*60}\n")
                transcription_file.write(f"File: {os.path.basename(audio_filepath)}\n")
                transcription_file.write(f"Timestamp: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
                transcription_file.write(f"{'='*60}\n")
                transcription_file.write(transcription_text + "\n")
                transcription_file.flush()
        else:
            print(f"[Transcription] {os.path.basename(audio_filepath)}: (no speech detected)\n")
            
    except Exception as e:
        print(f"Error transcribing {audio_filepath}: {e}")


def transcription_worker():
    """Worker thread that processes transcription queue"""
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


def create_new_file():
    """Create a new output file in the session folder"""
    global output_file, file_counter, current_audio_buffer, has_speech
    
    # Close previous file if open and queue it for transcription
    if output_file is not None and not output_file.closed:
        previous_filename = output_file.name
        
        # Only save and transcribe if there was speech in the segment
        if has_speech:
            output_file.close()
            print(f"Saved file #{file_counter - 1}")
            
            # Queue the file for transcription in background thread
            transcription_queue.put(previous_filename)
        else:
            # Discard the file if no speech detected
            output_file.close()
            try:
                os.remove(previous_filename)
                print(f"Discarded file #{file_counter - 1} (no speech detected)")
            except Exception as e:
                print(f"Error removing empty file: {e}")
    
    # Create new filename
    filename = os.path.join(session_folder, f"segment_{file_counter:04d}.wav")
    
    # Open new file
    output_file = sf.SoundFile(
        filename,
        mode='w',
        samplerate=SAMPLE_RATE,
        channels=CHANNELS,
        subtype='PCM_16'
    )
    
    print(f"Started new file: segment_{file_counter:04d}.wav")
    file_counter += 1
    current_audio_buffer = []
    has_speech = False  # Reset for new segment


def mix_and_save():
    """Mix audio from both buffers, detect silence, and save to file"""
    global output_file, silence_start_time, current_audio_buffer, has_speech
    
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
                
                # Add to current buffer
                current_audio_buffer.append(mixed)
                
                # Check for silence
                if is_silent(mixed):
                    if silence_start_time is None:
                        silence_start_time = datetime.now()
                    else:
                        # Check if silence duration exceeded
                        silence_duration = (datetime.now() - silence_start_time).total_seconds()
                        if silence_duration >= SILENCE_DURATION:
                            # Write all buffered audio to current file
                            if output_file is not None:
                                for chunk in current_audio_buffer:
                                    output_file.write(chunk)
                            
                            # Create new file
                            create_new_file()
                            silence_start_time = None
                else:
                    # Mark that we have speech in this segment
                    has_speech = True
                    
                    # Reset silence timer if sound detected
                    silence_start_time = None
                    
                    # Write to current file buffer (we'll write periodically)
                    if len(current_audio_buffer) > SAMPLE_RATE * 2:  # Write every 2 seconds of audio
                        if output_file is not None:
                            for chunk in current_audio_buffer:
                                output_file.write(chunk)
                            current_audio_buffer = []


def save_thread_function():
    """Thread function that periodically mixes and saves audio"""
    while recording:
        mix_and_save()
        threading.Event().wait(0.1)  # Save every 100ms
    
    # Final save after recording stops
    mix_and_save()
    
    # Write any remaining buffered audio
    global current_audio_buffer, output_file
    if output_file is not None and len(current_audio_buffer) > 0:
        for chunk in current_audio_buffer:
            output_file.write(chunk)


def signal_handler(sig, frame):
    """Handle Ctrl+C gracefully"""
    global recording
    print("\n\nStopping recording...")
    recording = False


def main():
    global recording, output_file, session_folder, whisper_model, transcription_file, transcription_thread
    
    # Create session folder
    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    session_folder = f"recording_{timestamp}"
    os.makedirs(session_folder, exist_ok=True)
    
    # Initialize Whisper model
    print("Loading Whisper model...")
    whisper_model = WhisperModel("base", device="cpu", compute_type="int8")
    print("Whisper model loaded.\n")
    
    # Create transcription output file
    transcription_filename = os.path.join(session_folder, "transcriptions.txt")
    transcription_file = open(transcription_filename, 'w', encoding='utf-8')
    transcription_file.write(f"Transcription Session: {timestamp}\n")
    transcription_file.write(f"{'='*60}\n\n")
    transcription_file.flush()
    
    # Start transcription worker thread
    transcription_thread = threading.Thread(target=transcription_worker, daemon=False)
    transcription_thread.start()
    
    print("=" * 60)
    print("Mixed Audio Recorder with Silence Detection")
    print("=" * 60)
    print(f"Session Folder: {session_folder}")
    print(f"Loopback Device (System Audio): {LOOPBACK_DEVICE_ID}")
    print(f"Microphone Device: {MICROPHONE_DEVICE_ID}")
    print(f"Sample Rate: {SAMPLE_RATE} Hz")
    print(f"Channels: {CHANNELS}")
    print(f"Silence Threshold: {SILENCE_THRESHOLD}")
    print(f"Silence Duration: {SILENCE_DURATION} seconds")
    print("=" * 60)
    print("\nPress Ctrl+C to stop recording\n")
    
    # Set up signal handler for graceful shutdown
    signal.signal(signal.SIGINT, signal_handler)
    
    try:
        # Create first output file
        create_new_file()
        
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
        
        # Close output file and queue final segment for transcription
        if output_file is not None:
            final_filename = output_file.name
            
            # Only save and transcribe final file if it has speech
            if has_speech:
                output_file.close()
                # Queue the final file for transcription
                transcription_queue.put(final_filename)
            else:
                # Discard the final file if no speech detected
                output_file.close()
                try:
                    os.remove(final_filename)
                    print("Discarded final file (no speech detected)")
                except Exception as e:
                    print(f"Error removing empty file: {e}")
        
        # Signal transcription thread to stop and wait for queue to complete
        print("\nWaiting for transcriptions to complete...")
        transcription_queue.put(None)  # Signal to stop
        transcription_thread.join()
        
        print(f"\nRecording saved to folder: {session_folder}")
        
        # Display info about all files
        print(f"\nTotal segments created: {file_counter - 1}")
        print("\nSegment details:")
        for filename in sorted(os.listdir(session_folder)):
            if filename.endswith('.wav'):
                filepath = os.path.join(session_folder, filename)
                info = sf.info(filepath)
                print(f"  {filename}: {info.duration:.2f} seconds, {info.frames * info.channels * 2 / 1024:.2f} KB")
        
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
        
        # Ensure transcription thread is stopped
        if transcription_thread is not None and transcription_thread.is_alive():
            transcription_queue.put(None)  # Signal to stop
            transcription_thread.join(timeout=30.0)
        
        if transcription_file is not None and not transcription_file.closed:
            transcription_file.close()
            print(f"Transcriptions saved to: {transcription_filename}")
        print("\nCleanup complete")


if __name__ == "__main__":
    main()
