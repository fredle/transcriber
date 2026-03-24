import numpy as np
import sounddevice as sd
import soundfile as sf
import threading
import signal
import sys
import os
import json
from datetime import datetime
from faster_whisper import WhisperModel
from queue import Queue

# Configuration
LOOPBACK_DEVICE_ID = None  # System audio (Teams) - will be selected by user
MICROPHONE_DEVICE_ID = None  # Microphone - will be selected by user
SAMPLE_RATE = 48000  # Hz
CHANNELS = 2  # Stereo
DTYPE = np.int16
CHUNK_SIZE = 1024
SILENCE_THRESHOLD = 500  # Amplitude threshold for silence detection (adjustable)
SILENCE_DURATION = 1.0  # Seconds of silence before saving file

# Shared buffers and synchronization
loopback_buffer = []
mic_buffer = []
buffer_lock = threading.Lock()
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
        segments, info = whisper_model.transcribe(audio_filepath, beam_size=5)
        
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
    
    with buffer_lock:
        # Process microphone buffer
        if len(mic_buffer) > 0:
            mic_chunks = mic_buffer[:]
            mic_buffer[:] = []
            
            for chunk in mic_chunks:
                # Add to current buffer
                mic_audio_buffer.append(chunk)
                
                # Check for silence
                if is_silent(chunk):
                    if mic_silence_start_time is None:
                        mic_silence_start_time = datetime.now()
                    else:
                        # Check if silence duration exceeded
                        silence_duration = (datetime.now() - mic_silence_start_time).total_seconds()
                        if silence_duration >= SILENCE_DURATION:
                            # Write all buffered audio to current file
                            if mic_output_file is not None:
                                for audio_chunk in mic_audio_buffer:
                                    mic_output_file.write(audio_chunk)
                            
                            # Create new file
                            create_new_mic_file()
                            mic_silence_start_time = None
                else:
                    # Mark that we have speech in this segment
                    mic_has_speech = True
                    
                    # Reset silence timer if sound detected
                    mic_silence_start_time = None
                    
                    # Write to current file buffer periodically
                    if len(mic_audio_buffer) > SAMPLE_RATE * 2:  # Write every 2 seconds of audio
                        if mic_output_file is not None:
                            for audio_chunk in mic_audio_buffer:
                                mic_output_file.write(audio_chunk)
                            mic_audio_buffer = []
        
        # Process speaker (loopback) buffer
        if len(loopback_buffer) > 0:
            speaker_chunks = loopback_buffer[:]
            loopback_buffer[:] = []
            
            for chunk in speaker_chunks:
                # Add to current buffer
                speaker_audio_buffer.append(chunk)
                
                # Check for silence
                if is_silent(chunk):
                    if speaker_silence_start_time is None:
                        speaker_silence_start_time = datetime.now()
                    else:
                        # Check if silence duration exceeded
                        silence_duration = (datetime.now() - speaker_silence_start_time).total_seconds()
                        if silence_duration >= SILENCE_DURATION:
                            # Write all buffered audio to current file
                            if speaker_output_file is not None:
                                for audio_chunk in speaker_audio_buffer:
                                    speaker_output_file.write(audio_chunk)
                            
                            # Create new file
                            create_new_speaker_file()
                            speaker_silence_start_time = None
                else:
                    # Mark that we have speech in this segment
                    speaker_has_speech = True
                    
                    # Reset silence timer if sound detected
                    speaker_silence_start_time = None
                    
                    # Write to current file buffer periodically
                    if len(speaker_audio_buffer) > SAMPLE_RATE * 2:  # Write every 2 seconds of audio
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
    """Handle Ctrl+C gracefully"""
    global recording
    print("\n\nStopping recording...")
    recording = False


def list_audio_devices():
    """List all available audio devices in table format (filtered for relevance)"""
    print("\n" + "=" * 120)
    print("Available Audio Devices (Filtered):")
    print("=" * 120)
    
    devices = sd.query_devices()
    
    # Filter devices to show only relevant ones
    seen_names = set()
    input_devices = []
    output_devices = []
    
    # Exclude generic/system devices
    exclude_keywords = [
        'Sound Mapper', 'Primary Sound', 'Sound Driver',
        'PC Speaker', 'Headphones ()'
    ]
    
    for idx, device in enumerate(devices):
        device_name = device['name']
        
        # Skip excluded devices
        if any(keyword in device_name for keyword in exclude_keywords):
            continue
        
        # Create a clean name for deduplication (remove API-specific suffixes)
        clean_name = device_name.split('(')[0].strip()
        
        # Prefer 48000 Hz devices, skip if we've seen this device at a different sample rate
        device_key = (clean_name, 'INPUT' if device['max_input_channels'] > 0 else 'OUTPUT')
        
        # Only add if we haven't seen this exact device or if this one is 48000 Hz
        if device_key not in seen_names or device['default_samplerate'] == 48000:
            if device_key in seen_names and device['default_samplerate'] != 48000:
                continue  # Skip non-48kHz duplicates
                
            seen_names.add(device_key)
            
            if device['max_input_channels'] > 0:
                input_devices.append((idx, device))
            if device['max_output_channels'] > 0:
                output_devices.append((idx, device))
    
    # Display INPUT devices
    if input_devices:
        print("\n📍 INPUT DEVICES (Microphones & Loopback):")
        print("-" * 120)
        header = f"{'ID':<4} {'Device Name':<60} {'Channels':<10} {'Sample Rate':<12}"
        print(header)
        print("-" * 120)
        
        for idx, device in input_devices:
            device_name = device['name']
            if len(device_name) > 60:
                device_name = device_name[:57] + "..."
            
            # Highlight special device types
            tag = ""
            if 'Stereo Mix' in device['name'] or 'Loopback' in device['name']:
                tag = " 🔊 [LOOPBACK]"
            elif 'Microphone' in device['name'] or 'Mic' in device['name']:
                tag = " 🎤 [MIC]"
            
            row = f"{idx:<4} {device_name:<60} {device['max_input_channels']:<10} {int(device['default_samplerate']):<12}{tag}"
            print(row)
    
    # Display OUTPUT devices
    if output_devices:
        print("\n📍 OUTPUT DEVICES (Speakers):")
        print("-" * 120)
        header = f"{'ID':<4} {'Device Name':<60} {'Channels':<10} {'Sample Rate':<12}"
        print(header)
        print("-" * 120)
        
        for idx, device in output_devices:
            device_name = device['name']
            if len(device_name) > 60:
                device_name = device_name[:57] + "..."
            
            row = f"{idx:<4} {device_name:<60} {device['max_output_channels']:<10} {int(device['default_samplerate']):<12}"
            print(row)
    
    print("=" * 120)
    print("\nTIP: For loopback/system audio, look for 'Stereo Mix' or similar devices marked with 🔊")
    print("     For microphone, look for your physical microphone device marked with 🎤")


def select_device(device_type):
    """Prompt user to select an audio device"""
    while True:
        try:
            device_id = input(f"\nEnter device ID for {device_type}: ").strip()
            device_id = int(device_id)
            
            # Verify device exists and has input channels
            device_info = sd.query_devices(device_id)
            if device_info['max_input_channels'] == 0:
                print(f"Error: Device {device_id} has no input channels. Please select an input device.")
                continue
            
            print(f"Selected: [{device_id}] {device_info['name']}")
            return device_id
        except ValueError:
            print("Error: Please enter a valid number.")
        except Exception as e:
            print(f"Error: {e}. Please try again.")


def main():
    global recording, output_file, session_folder, whisper_model, transcription_file, transcription_thread
    global LOOPBACK_DEVICE_ID, MICROPHONE_DEVICE_ID
    
    print("=" * 60)
    print("Split Audio Recorder with Silence Detection")
    print("=" * 60)
    print()
    
    # List available devices
    list_audio_devices()
    
    # Let user select devices
    print("Please select your audio devices:")
    print("\nLOOPBACK/SYSTEM AUDIO: This captures system audio (e.g., Teams, Zoom speakers)")
    print("Look for devices like 'Stereo Mix', 'Wave', 'Loopback', or virtual audio cables.")
    LOOPBACK_DEVICE_ID = select_device("LOOPBACK/SYSTEM AUDIO (Speaker)")
    
    print("\nMICROPHONE: This captures your microphone input")
    print("Look for your physical microphone device.")
    MICROPHONE_DEVICE_ID = select_device("MICROPHONE")
    
    # Create session folder
    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    session_folder = f"recording_{timestamp}"
    os.makedirs(session_folder, exist_ok=True)
    
    # Initialize Whisper model
    print("Loading Whisper model...")
    whisper_model = WhisperModel("base", device="cpu", compute_type="int8")
    print("Whisper model loaded.\n")
    
    # Create transcription output file
    transcription_filename = os.path.join(session_folder, "transcriptions.jsonl")
    transcription_file = open(transcription_filename, 'w', encoding='utf-8')
    # Write session metadata as first line
    session_entry = {
        "session_start": datetime.now().isoformat(),
        "session_id": timestamp,
        "type": "session_metadata"
    }
    transcription_file.write(json.dumps(session_entry) + "\n")
    transcription_file.flush()
    
    # Start transcription worker thread
    transcription_thread = threading.Thread(target=transcription_worker, daemon=False)
    transcription_thread.start()
    
    print("\n" + "=" * 60)
    print("Recording Configuration")
    print("=" * 60)
    print(f"Session Folder: {session_folder}")
    
    # Display selected device names
    loopback_info = sd.query_devices(LOOPBACK_DEVICE_ID)
    mic_info = sd.query_devices(MICROPHONE_DEVICE_ID)
    
    print(f"Loopback Device (System Audio/Speaker): [{LOOPBACK_DEVICE_ID}] {loopback_info['name']}")
    print(f"Microphone Device: [{MICROPHONE_DEVICE_ID}] {mic_info['name']}")
    print(f"Sample Rate: {SAMPLE_RATE} Hz")
    print(f"Channels: {CHANNELS}")
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


if __name__ == "__main__":
    main()
