import { Router } from "express";
import { uidOf } from "../authMiddleware";

const router = Router();

/**
 * Mints a short-lived, single-use AssemblyAI realtime token so the raw
 * account key never reaches the desktop client. The client connects to
 * AssemblyAI's WebSocket directly with this token, exactly as it does today
 * with the raw key - only the auth material changes.
 */
router.post("/token", async (req, res) => {
  const uid = uidOf(req);
  const expiresInSeconds = Math.min(600, Math.max(1, Number(req.body?.expiresInSeconds) || 60));

  const resp = await fetch(
    `https://streaming.assemblyai.com/v3/token?expires_in_seconds=${expiresInSeconds}`,
    { headers: { Authorization: process.env.ASSEMBLYAI_API_KEY ?? "" } },
  );

  if (!resp.ok) {
    console.error(`AssemblyAI token mint failed for uid=${uid}: ${resp.status}`);
    res.status(502).json({ error: "Could not mint a transcription token." });
    return;
  }

  const body = (await resp.json()) as { token: string };
  res.json({ token: body.token, expiresInSeconds });
});

export default router;
