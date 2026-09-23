import express, { Router } from "express";
import { uidOf } from "../authMiddleware";

const router = Router();

// The index-level express.json() only consumes application/json bodies, so
// this raw parser (scoped to this route) is what turns the uploaded WAV into
// req.body as a Buffer. 25mb comfortably covers a single pause-segmented
// turn at 16-bit mono even at a high sample rate.
router.use(express.raw({ type: "audio/wav", limit: "25mb" }));

const POLL_INTERVAL_MS = 1500;
const POLL_TIMEOUT_MS = 60_000;

interface AssemblyAiWord {
  start: number;
  end: number;
}

interface AssemblyAiUtterance {
  speaker: string;
  text: string;
  start: number;
  end: number;
}

interface AssemblyAiTranscript {
  id: string;
  status: "queued" | "processing" | "completed" | "error";
  error?: string;
  text?: string;
  words?: AssemblyAiWord[];
  utterances?: AssemblyAiUtterance[];
  audio_duration?: number;
}

/**
 * The realtime and async APIs don't share a model-name vocabulary: realtime
 * takes "universal-streaming-english" / "universal-streaming-multilingual" /
 * "universal-3-5-pro", while async's `speech_models` only accepts
 * "universal-3-5-pro" or "universal-2". Settings.SpeechModel on the client
 * only knows the realtime names, so map here rather than teaching the client
 * two vocabularies.
 */
function toAsyncSpeechModel(realtimeModel: string): "universal-3-5-pro" | "universal-2" {
  return realtimeModel === "universal-3-5-pro" ? "universal-3-5-pro" : "universal-2";
}

/**
 * Pause-segmented batch transcription: the client buffers one turn's worth
 * of audio (silence-delimited) and POSTs it here as a WAV file. This proxies
 * upload -> submit -> poll against AssemblyAI's async API so the account key
 * never reaches the desktop client, then waits for the result before
 * responding - AssemblyAI's async turnaround is typically single-digit
 * seconds and rarely exceeds ~45s, so a synchronous round trip is simpler
 * for the client than a submit/poll protocol of its own.
 */
router.post("/", async (req, res) => {
  const uid = uidOf(req);
  const diarization = req.query.diarization !== "false";
  const speechModel = toAsyncSpeechModel(String(req.query.speechModel ?? "universal-streaming-english"));
  const apiKey = process.env.ASSEMBLYAI_API_KEY ?? "";

  const audio = req.body as Buffer;
  if (!Buffer.isBuffer(audio) || audio.length === 0) {
    res.status(400).json({ error: "No audio received." });
    return;
  }

  try {
    const uploadResp = await fetch("https://api.assemblyai.com/v2/upload", {
      method: "POST",
      headers: { Authorization: apiKey, "Content-Type": "application/octet-stream" },
      body: audio,
    });
    if (!uploadResp.ok) {
      console.error(`AssemblyAI upload failed for uid=${uid}: ${uploadResp.status}`);
      res.status(502).json({ error: "Transcription service is temporarily unavailable. Please try again." });
      return;
    }
    const { upload_url } = (await uploadResp.json()) as { upload_url: string };

    const submitResp = await fetch("https://api.assemblyai.com/v2/transcript", {
      method: "POST",
      headers: { Authorization: apiKey, "Content-Type": "application/json" },
      body: JSON.stringify({
        audio_url: upload_url,
        speaker_labels: diarization,
        punctuate: true, // speaker_labels requires this
        speech_models: [speechModel],
      }),
    });
    if (!submitResp.ok) {
      console.error(`AssemblyAI transcript submit failed for uid=${uid}: ${submitResp.status}`);
      res.status(502).json({ error: "Transcription service is temporarily unavailable. Please try again." });
      return;
    }
    const submitted = (await submitResp.json()) as AssemblyAiTranscript;

    const deadline = Date.now() + POLL_TIMEOUT_MS;
    let transcript = submitted;
    while (transcript.status !== "completed" && transcript.status !== "error") {
      if (Date.now() > deadline) {
        console.error(`AssemblyAI transcript poll timed out for uid=${uid} id=${submitted.id}`);
        res.status(504).json({ error: "Transcription is taking longer than expected. Please try again." });
        return;
      }
      await new Promise((r) => setTimeout(r, POLL_INTERVAL_MS));
      const pollResp = await fetch(`https://api.assemblyai.com/v2/transcript/${submitted.id}`, {
        headers: { Authorization: apiKey },
      });
      if (!pollResp.ok) {
        console.error(`AssemblyAI transcript poll failed for uid=${uid} id=${submitted.id}: ${pollResp.status}`);
        res.status(502).json({ error: "Transcription service is temporarily unavailable. Please try again." });
        return;
      }
      transcript = (await pollResp.json()) as AssemblyAiTranscript;
    }

    if (transcript.status === "error") {
      console.error(`AssemblyAI transcription errored for uid=${uid} id=${submitted.id}: ${transcript.error}`);
      res.status(502).json({ error: "Transcription failed for that segment." });
      return;
    }

    const utterances = transcript.utterances?.length
      ? transcript.utterances.map((u) => ({
          text: u.text,
          speaker: u.speaker,
          startMs: u.start,
          endMs: u.end,
        }))
      : transcript.text
        ? [
            {
              text: transcript.text,
              speaker: null,
              startMs: transcript.words?.[0]?.start ?? 0,
              endMs: transcript.words?.[transcript.words.length - 1]?.end ?? Math.round((transcript.audio_duration ?? 0) * 1000),
            },
          ]
        : [];

    res.json({ utterances });
  } catch (err) {
    console.error(`Batch transcription failed for uid=${uid}: ${err}`);
    res.status(502).json({ error: "Transcription service is temporarily unavailable. Please try again." });
  }
});

export default router;
