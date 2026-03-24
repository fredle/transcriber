from faster_whisper import WhisperModel
import time

# Initialize the model (using "base" model, you can change to "tiny", "small", "medium", "large-v2", etc.)
print("Loading Whisper model...")
model = WhisperModel("base", device="cpu", compute_type="int8")

# Path to the audio file
audio_file = "mixed_audio_20260127_222658.wav"

print(f"Transcribing {audio_file}...")
start_time = time.time()

# Transcribe the audio
segments, info = model.transcribe(audio_file, beam_size=5)

print(f"Detected language '{info.language}' with probability {info.language_probability}")
print("\nTranscription:")
print("-" * 80)

# Print and collect all segments
full_transcription = []
for segment in segments:
    print(f"[{segment.start:.2f}s -> {segment.end:.2f}s] {segment.text}")
    full_transcription.append(segment.text)

print("-" * 80)
end_time = time.time()
print(f"\nTranscription completed in {end_time - start_time:.2f} seconds")

# Save to file
output_file = "transcription_output.txt"
with open(output_file, "w", encoding="utf-8") as f:
    f.write(f"Transcription of: {audio_file}\n")
    f.write(f"Language: {info.language} (probability: {info.language_probability:.2f})\n")
    f.write(f"Duration: {info.duration:.2f} seconds\n")
    f.write("-" * 80 + "\n\n")
    
    for segment in model.transcribe(audio_file, beam_size=5)[0]:
        f.write(f"[{segment.start:.2f}s -> {segment.end:.2f}s] {segment.text}\n")
    
    f.write("\n" + "-" * 80 + "\n")
    f.write("Full transcription:\n")
    f.write(" ".join(full_transcription))

print(f"\nTranscription saved to: {output_file}")
