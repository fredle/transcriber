import { randomUUID } from "crypto";
import { Router } from "express";
import { uidOf } from "../authMiddleware";
import { bucket } from "../firebase";

const router = Router({ mergeParams: true });

interface Params {
  meetingId: string;
}

/**
 * Returns a signed URL the client can PUT the PNG straight to, so screenshot
 * bytes never transit Cloud Run. The client uploads with
 * Content-Type: image/png matching what's signed here.
 */
router.post<Params>("/", async (req, res) => {
  const uid = uidOf(req);
  const meetingId = req.params.meetingId;
  const id = randomUUID();
  const objectPath = `users/${uid}/meetings/${meetingId}/screenshots/${id}.png`;

  const [uploadUrl] = await bucket.file(objectPath).getSignedUrl({
    version: "v4",
    action: "write",
    expires: Date.now() + 5 * 60 * 1000,
    contentType: "image/png",
  });

  res.json({ id, uploadUrl, objectPath });
});

router.get<Params>("/", async (req, res) => {
  const uid = uidOf(req);
  const prefix = `users/${uid}/meetings/${req.params.meetingId}/screenshots/`;
  const [files] = await bucket.getFiles({ prefix });

  const items = await Promise.all(
    files.map(async (f) => {
      const [url] = await f.getSignedUrl({ version: "v4", action: "read", expires: Date.now() + 15 * 60 * 1000 });
      return { objectPath: f.name, url };
    }),
  );
  res.json(items);
});

export default router;
