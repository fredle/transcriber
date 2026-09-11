import express from "express";
import { requireAuth } from "./authMiddleware";
import { requireAdmin } from "./adminMiddleware";
import assemblyaiRoute from "./routes/assemblyai";
import meetingsRoute from "./routes/meetings";
import linesRoute from "./routes/lines";
import attendeesRoute from "./routes/attendees";
import screenshotsRoute from "./routes/screenshots";
import askRoute from "./routes/ask";
import adminRoute from "./routes/admin";

const ADMIN_PORTAL_ORIGINS = ["https://teeline.web.app", "http://localhost:5000"];

const app = express();
app.use(express.json({ limit: "1mb" }));

// Only the admin portal is called from a browser (the desktop app talks to
// this service directly, no Origin header, so it's untouched by this) - CORS
// is scoped to that one known origin rather than opened up generally.
app.use((req, res, next) => {
  const origin = req.header("origin");
  if (origin && ADMIN_PORTAL_ORIGINS.includes(origin)) {
    res.header("Access-Control-Allow-Origin", origin);
    res.header("Access-Control-Allow-Headers", "Authorization, Content-Type");
  }
  if (req.method === "OPTIONS") {
    res.sendStatus(204);
    return;
  }
  next();
});

// Not "/healthz": that exact path is intercepted by Google's front-end
// before it ever reaches the container (confirmed empirically - a trailing
// slash or any other path reaches Express fine, "/healthz" alone 404s at
// the edge), so it can't be used as an app-level health check here.
app.get("/status", (_req, res) => res.status(200).send("ok"));

const v1 = express.Router();
v1.use(requireAuth);
v1.use("/assemblyai", assemblyaiRoute);
v1.use("/meetings", meetingsRoute);
v1.use("/meetings/:meetingId/lines", linesRoute);
v1.use("/meetings/:meetingId/attendees", attendeesRoute);
v1.use("/meetings/:meetingId/screenshots", screenshotsRoute);
v1.use("/meetings/:meetingId/ask", askRoute);
v1.use("/admin", requireAdmin, adminRoute);
app.use("/v1", v1);

app.use((err: unknown, _req: express.Request, res: express.Response, _next: express.NextFunction) => {
  console.error(err);
  res.status(500).json({ error: "Internal error." });
});

const port = Number(process.env.PORT) || 8080;
app.listen(port, () => console.log(`Teeline backend listening on :${port}`));
