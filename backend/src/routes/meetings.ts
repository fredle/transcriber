import { Router } from "express";
import { FieldValue } from "firebase-admin/firestore";
import { uidOf } from "../authMiddleware";
import { bucket, meetingDoc, meetingsCollection } from "../firebase";

const router = Router();

router.post("/", async (req, res) => {
  const uid = uidOf(req);
  const { meetingId, title, started, engine, group } = req.body ?? {};
  if (!meetingId || typeof meetingId !== "string") {
    res.status(400).json({ error: "meetingId is required." });
    return;
  }

  await meetingDoc(uid, meetingId).set(
    {
      title: title ?? "Untitled meeting",
      started: started ?? new Date().toISOString(),
      engine: engine ?? "assemblyai",
      group: group ?? "",
      updatedAt: FieldValue.serverTimestamp(),
    },
    { merge: true },
  );

  res.status(201).json({ meetingId });
});

router.patch("/:id", async (req, res) => {
  const uid = uidOf(req);
  const { title, group, notes } = req.body ?? {};
  const patch: Record<string, unknown> = { updatedAt: FieldValue.serverTimestamp() };
  if (typeof title === "string") patch.title = title;
  if (typeof group === "string") patch.group = group;
  // notes is the RTF bytes, base64-encoded; null clears them (user cleared the notes box).
  if (typeof notes === "string" || notes === null) patch.notes = notes;

  await meetingDoc(uid, req.params.id).set(patch, { merge: true });
  res.status(204).end();
});

router.get("/", async (req, res) => {
  const uid = uidOf(req);
  const limit = Math.min(500, Number(req.query.limit) || 100);
  const snap = await meetingsCollection(uid).orderBy("started", "desc").limit(limit).get();
  // notes can be large-ish and isn't needed for the list view - only for GET /:id.
  res.json(
    snap.docs.map((d) => {
      const { notes: _notes, ...rest } = d.data();
      return { id: d.id, ...rest };
    }),
  );
});

router.get("/:id", async (req, res) => {
  const uid = uidOf(req);
  const doc = await meetingDoc(uid, req.params.id).get();
  if (!doc.exists) {
    res.status(404).json({ error: "Not found." });
    return;
  }
  res.json({ id: doc.id, ...doc.data() });
});

/**
 * Deletes a meeting and everything under it - the doc itself, its lines and
 * attendees subcollections, and any screenshots in storage - so a local
 * delete doesn't get undone by the next cloud pull re-downloading it.
 */
router.delete("/:id", async (req, res) => {
  const uid = uidOf(req);
  const doc = meetingDoc(uid, req.params.id);

  for (const sub of ["lines", "attendees"]) {
    const refs = await doc.collection(sub).listDocuments();
    const chunks = Array.from({ length: Math.ceil(refs.length / 450) }, (_, i) => refs.slice(i * 450, i * 450 + 450));
    for (const chunk of chunks) {
      const batch = doc.firestore.batch();
      for (const ref of chunk) batch.delete(ref);
      await batch.commit();
    }
  }

  const [files] = await bucket.getFiles({ prefix: `users/${uid}/meetings/${req.params.id}/` });
  await Promise.all(files.map((f) => f.delete()));

  await doc.delete();
  res.status(204).end();
});

export default router;
