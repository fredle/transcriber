"""
Test script to verify pyannote speaker diarization setup
"""
import os
import torch
from dotenv import load_dotenv
from pyannote.audio import Pipeline

# Load environment variables from .env file
load_dotenv()

print("=" * 60)
print("Pyannote Speaker Diarization Test")
print("=" * 60)

# Check if token is loaded
token = os.getenv("HF_TOKEN")
if token:
    print(f"✓ HF_TOKEN found: {token[:10]}...{token[-4:]}")
else:
    print("✗ HF_TOKEN not found in environment")
    print("  Make sure your .env file contains: HF_TOKEN=hf_...")
    exit(1)

# Check GPU availability
if torch.cuda.is_available():
    print(f"✓ GPU available: {torch.cuda.get_device_name(0)}")
    device = "cuda"
else:
    print("✓ Using CPU (no GPU detected)")
    device = "cpu"

print("\n" + "=" * 60)
print("Loading pyannote/speaker-diarization-3.1...")
print("=" * 60)

try:
    # Load the pipeline
    pipeline = Pipeline.from_pretrained(
        "pyannote/speaker-diarization-3.1",
        token=token
    )
    
    # Move to appropriate device
    pipeline.to(torch.device(device))
    
    print("✓ Pipeline loaded successfully!")
    print(f"✓ Running on: {device}")
    
    print("\n" + "=" * 60)
    print("Test Result: SUCCESS")
    print("=" * 60)
    print("\nYour pyannote setup is working correctly!")
    print("You can now use speaker diarization in your transcription.")
    
except Exception as e:
    print(f"\n✗ Error loading pipeline: {e}")
    print("\n" + "=" * 60)
    print("Test Result: FAILED")
    print("=" * 60)
    print("\nTroubleshooting:")
    print("1. Visit https://hf.co/pyannote/speaker-diarization-3.1 and accept terms")
    print("2. Visit https://hf.co/pyannote/segmentation-3.0 and accept terms")
    print("3. Verify your token at https://hf.co/settings/tokens")
    print("4. Ensure .env file has: HF_TOKEN=hf_your_token_here")
    exit(1)
