import { Router } from "express";
import { FieldValue } from "firebase-admin/firestore";
import { uidOf } from "../authMiddleware";
import { meetingDoc } from "../firebase";

const router = Router({ mergeParams: true });

interface Params {
  meetingId: string;
}

interface LineInput {
  speaker: string;
  speakerLabel?: string | null;
  text: string;
  startMs?: number;
  endMs?: number;
  timestamp?: string;
}

router.post<Params>("/", async (req, res) => {
  const uid = uidOf(req);
  const meetingId = req.params.meetingId;
  const body = req.body ?? {};
  const lines: LineInput[] = Array.isArray(body.lines) ? body.lines : [body];

  const valid = lines.filter(
    (l): l is LineInput => typeof l?.speaker === "string" && typeof l?.text === "string" && l.text.length > 0,
  );
  if (valid.length === 0) {
    res.status(400).json({ error: "At least one line with speaker/text is required." });
    return;
  }

  const linesRef = meetingDoc(uid, meetingId).collection("lines");
  const batch = linesRef.firestore.batch();
  for (const line of valid) {
    batch.set(linesRef.doc(), {
      speaker: line.speaker,
      speakerLabel: line.speakerLabel ?? null,
      text: line.text,
      startMs: line.startMs ?? null,
      endMs: line.endMs ?? null,
      timestamp: line.timestamp ?? new Date().toISOString(),
      createdAt: FieldValue.serverTimestamp(),
    });
  }
  batch.set(meetingDoc(uid, meetingId), { updatedAt: FieldValue.serverTimestamp() }, { merge: true });
  await batch.commit();

  res.status(201).json({ inserted: valid.length });
});

router.get<Params>("/", async (req, res) => {
  const uid = uidOf(req);
  const snap = await meetingDoc(uid, req.params.meetingId).collection("lines").orderBy("timestamp", "asc").get();
  res.json(snap.docs.map((d) => ({ id: d.id, ...d.data() })));
});

/**
 * Replaces the entire lines subcollection with the given array, in order -
 * used to push a local edit (speaker reassignment, line deletion) back up,
 * since those change/remove existing lines rather than just appending.
 * Firestore batches cap at 500 writes, so deletes and inserts are chunked.
 */
router.put<Params>("/", async (req, res) => {
  const uid = uidOf(req);
  const meetingId = req.params.meetingId;
  const body = req.body ?? {};
  const lines: LineInput[] = Array.isArray(body.lines) ? body.lines : [];
  const valid = lines.filter(
    (l): l is LineInput => typeof l?.speaker === "string" && typeof l?.text === "string" && l.text.length > 0,
  );

  const linesRef = meetingDoc(uid, meetingId).collection("lines");
  const firestore = linesRef.firestore;
  const existing = await linesRef.listDocuments();

  const chunks = <T>(items: T[], size: number) =>
    Array.from({ length: Math.ceil(items.length / size) }, (_, i) => items.slice(i * size, i * size + size));

  for (const chunk of chunks(existing, 450)) {
    const batch = firestore.batch();
    for (const doc of chunk) batch.delete(doc);
    await batch.commit();
  }

  for (const chunk of chunks(valid, 450)) {
    const batch = firestore.batch();
    for (const line of chunk) {
      batch.set(linesRef.doc(), {
        speaker: line.speaker,
        speakerLabel: line.speakerLabel ?? null,
        text: line.text,
        startMs: line.startMs ?? null,
        endMs: line.endMs ?? null,
        timestamp: line.timestamp ?? new Date().toISOString(),
        createdAt: FieldValue.serverTimestamp(),
      });
    }
    await batch.commit();
  }

  await meetingDoc(uid, meetingId).set({ updatedAt: FieldValue.serverTimestamp() }, { merge: true });
  res.status(200).json({ count: valid.length });
});

export default router;
