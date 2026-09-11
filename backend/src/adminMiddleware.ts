import { NextFunction, Request, Response } from "express";
import { AuthedRequest } from "./authMiddleware";

const ADMIN_EMAILS = (process.env.ADMIN_EMAILS ?? "freddie@leatham.com")
  .split(",")
  .map((e) => e.trim().toLowerCase())
  .filter(Boolean);

/** Gates admin-only routes to a fixed email allowlist - checked after requireAuth. */
export function requireAdmin(req: Request, res: Response, next: NextFunction) {
  const email = (req as AuthedRequest).email?.toLowerCase();
  if (!email || !ADMIN_EMAILS.includes(email)) {
    res.status(403).json({ error: "Not authorized." });
    return;
  }
  next();
}
