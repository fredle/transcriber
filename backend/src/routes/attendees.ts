import { Router } from "express";
import { FieldValue } from "firebase-admin/firestore";
import { uidOf } from "../authMiddleware";
import { meetingDoc } from "../firebase";

const router = Router({ mergeParams: true });

interface Params {
  meetingId: string;
}

router.post<Params>("/", async (req, res) => {
  const uid = uidOf(req);
  const { name, joined, timestamp } = req.body ?? {};
  if (typeof name !== "string" || name.trim().length === 0 || typeof joined !== "boolean") {
    res.status(400).json({ error: "name (string) and joined (boolean) are required." });
    return;
  }

  const doc = meetingDoc(uid, req.params.meetingId);
  const batch = doc.firestore.batch();
  batch.set(doc.collection("attendees").doc(), {
    name: name.trim(),
    joined,
    timestamp: timestamp ?? new Date().toISOString(),
    createdAt: FieldValue.serverTimestamp(),
  });
  batch.set(doc, { updatedAt: FieldValue.serverTimestamp() }, { merge: true });
  await batch.commit();

  res.status(201).end();
});

router.get<Params>("/", async (req, res) => {
  const uid = uidOf(req);
  const snap = await meetingDoc(uid, req.params.meetingId)
    .collection("attendees")
    .orderBy("timestamp", "asc")
    .get();
  res.json(snap.docs.map((d) => ({ id: d.id, ...d.data() })));
});

export default router;
