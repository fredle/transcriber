import { NextFunction, Request, Response } from "express";
import { auth } from "./firebase";

export interface AuthedRequest extends Request<any> {
  uid: string;
  email?: string;
}

/** Pulls the verified uid off a request handled after requireAuth. */
export function uidOf(req: Request<any, any, any, any>): string {
  return (req as unknown as AuthedRequest).uid;
}

/**
 * Verifies the Firebase ID token on every request. Firestore/Storage are
 * never reached from the client directly, so this is the only place
 * multi-tenant isolation is enforced - every route below must scope its
 * reads/writes to req.uid.
 */
export async function requireAuth(req: Request, res: Response, next: NextFunction) {
  const header = req.header("authorization") ?? "";
  const match = /^Bearer (.+)$/.exec(header);
  if (!match) {
    res.status(401).json({ error: "Missing bearer token." });
    return;
  }

  try {
    const decoded = await auth.verifyIdToken(match[1]);
    (req as AuthedRequest).uid = decoded.uid;
    (req as AuthedRequest).email = decoded.email;
    next();
  } catch (err) {
    res.status(401).json({ error: "Invalid or expired token." });
  }
}
