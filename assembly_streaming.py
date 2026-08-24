"""
Realtime transcription via AssemblyAI's Universal-3.5 Pro streaming API —
an alternate engine to local Whisper (see transcriber.py's --engine flag),
used specifically for its diarization support. AssemblyAI's own guidance
routes live meeting-notetaking use cases to realtime streaming rather than
the pre-recorded/batch API, since diarization on independently-transcribed
short segments would reset speaker identity every segment.
"""
import threading

from assemblyai.streaming.v3 import (
    SpeechModel,
    StreamingClient,
    StreamingClientOptions,
    StreamingEvents,
    StreamingMode,
    StreamingParameters,
    TurnEvent,
)

# Realtime requires 50-1000ms of audio per WebSocket frame, sent no faster
# than real-time. Our capture blocksize (1024 frames @ 48kHz =~ 21ms) is
# smaller than that, so raw chunks are batched up to this size before send.
SEND_CHUNK_MS = 200


class AssemblyChannelStreamer:
    """One realtime connection for one audio channel (mic or speaker)."""

    def __init__(self, api_key, sample_rate, label, on_final_turn):
        """on_final_turn(text, speaker_label, start_ms, end_ms) fires once
        per finalized ("end_of_turn") Turn event."""
        self._label = label
        self._on_final_turn = on_final_turn
        self._sample_rate = sample_rate
        self._bytes_per_send = int(sample_rate * (SEND_CHUNK_MS / 1000.0)) * 2  # int16 mono
        self._pending = bytearray()
        self._pending_lock = threading.Lock()

        self._client = StreamingClient(StreamingClientOptions(api_key=api_key))
        self._client.on(StreamingEvents.Turn, self._handle_turn)
        self._client.on(StreamingEvents.Error, self._handle_error)

    def start(self):
        self._client.connect(StreamingParameters(
            sample_rate=self._sample_rate,
            speech_model=SpeechModel.universal_3_5_pro,
            mode=StreamingMode.balanced,
            speaker_labels=True,
        ))

    def feed(self, pcm_bytes):
        """Call from the audio capture callback with raw int16 mono PCM."""
        with self._pending_lock:
            self._pending.extend(pcm_bytes)
            while len(self._pending) >= self._bytes_per_send:
                chunk = bytes(self._pending[:self._bytes_per_send])
                del self._pending[:self._bytes_per_send]
                self._client.stream(chunk)

    def stop(self):
        try:
            self._client.disconnect(terminate=True)
        except Exception as e:
            print(f"[assemblyai:{self._label}] disconnect error: {e}")

    def _handle_turn(self, _client, event: TurnEvent):
        if not event.end_of_turn:
            return
        text = (event.transcript or "").strip()
        if not text:
            return
        words = event.words or []
        start_ms = words[0].start if words else 0
        end_ms = words[-1].end if words else 0
        self._on_final_turn(text, event.speaker_label, start_ms, end_ms)

    def _handle_error(self, _client, error):
        print(f"[assemblyai:{self._label}] error: {error}")
