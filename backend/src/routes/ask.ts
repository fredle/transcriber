import { Router } from "express";
import { uidOf } from "../authMiddleware";
import { meetingDoc } from "../firebase";
import { askAboutMeeting } from "../anthropic";

const router = Router({ mergeParams: true });

interface Params {
  meetingId: string;
}

router.post<Params>("/", async (req, res) => {
  const uid = uidOf(req);
  const meetingId = req.params.meetingId;
  const question = req.body?.question;
  if (typeof question !== "string" || question.trim().length === 0) {
    res.status(400).json({ error: "question is required." });
    return;
  }

  const meeting = await meetingDoc(uid, meetingId).get();
  if (!meeting.exists) {
    res.status(404).json({ error: "Meeting not found." });
    return;
  }

  const linesSnap = await meetingDoc(uid, meetingId).collection("lines").orderBy("timestamp", "asc").get();
  const lines = linesSnap.docs.map((d) => {
    const data = d.data();
    return { speaker: data.speaker as string, speakerLabel: data.speakerLabel as string | null, text: data.text as string };
  });

  if (lines.length === 0) {
    res.status(422).json({ error: "This meeting has no transcript yet." });
    return;
  }

  try {
    const answer = await askAboutMeeting((meeting.data()?.title as string) ?? "Untitled meeting", lines, question);
    res.json({ answer });
  } catch (err) {
    if (err instanceof Error && err.message.includes("ANTHROPIC_API_KEY")) {
      res.status(503).json({ error: "AI Q&A isn't set up yet on the backend." });
      return;
    }
    console.error(`Anthropic call failed for uid=${uid} meeting=${meetingId}:`, err);
    res.status(502).json({ error: "The AI model could not answer right now." });
  }
});

export default router;
