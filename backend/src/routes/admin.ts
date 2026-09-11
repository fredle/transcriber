import { Router } from "express";
import { auth, bucket, meetingsCollection } from "../firebase";
import type { UserRecord } from "firebase-admin/auth";

const router = Router();

interface UserSummary {
  uid: string;
  email: string | null;
  displayName: string | null;
  createdAt: string | null;
  lastSignInAt: string | null;
  disabled: boolean;
  meetingCount: number;
  lastMeetingAt: string | null;
  storageBytes: number;
}

async function listAllAuthUsers(): Promise<UserRecord[]> {
  const users: UserRecord[] = [];
  let pageToken: string | undefined;
  do {
    const page = await auth.listUsers(1000, pageToken);
    users.push(...page.users);
    pageToken = page.pageToken;
  } while (pageToken);
  return users;
}

/**
 * One combined snapshot for the admin portal: every Firebase Auth user
 * merged with their Firestore/Storage footprint. Fine to fan out a handful
 * of reads per user here - this project's user count is small - but this
 * would need batching/pagination before it'd hold up at real scale.
 */
router.get("/overview", async (_req, res) => {
  const authUsers = await listAllAuthUsers();

  const users: UserSummary[] = await Promise.all(
    authUsers.map(async (u) => {
      const [countSnap, lastMeetingSnap, [files]] = await Promise.all([
        meetingsCollection(u.uid).count().get(),
        meetingsCollection(u.uid).orderBy("updatedAt", "desc").limit(1).get(),
        bucket.getFiles({ prefix: `users/${u.uid}/` }),
      ]);

      const storageBytes = files.reduce((sum, f) => sum + Number(f.metadata.size ?? 0), 0);
      const updatedAt = lastMeetingSnap.docs[0]?.data()?.updatedAt;

      return {
        uid: u.uid,
        email: u.email ?? null,
        displayName: u.displayName ?? null,
        createdAt: u.metadata.creationTime ?? null,
        lastSignInAt: u.metadata.lastSignInTime ?? null,
        disabled: u.disabled,
        meetingCount: countSnap.data().count,
        lastMeetingAt: updatedAt?.toDate ? updatedAt.toDate().toISOString() : null,
        storageBytes,
      };
    }),
  );

  users.sort((a, b) => (b.lastMeetingAt ?? "").localeCompare(a.lastMeetingAt ?? ""));

  const thirtyDaysAgo = Date.now() - 30 * 24 * 60 * 60 * 1000;
  const summary = {
    totalUsers: users.length,
    totalMeetings: users.reduce((sum, u) => sum + u.meetingCount, 0),
    totalStorageBytes: users.reduce((sum, u) => sum + u.storageBytes, 0),
    activeUsersLast30d: users.filter((u) => u.lastMeetingAt && Date.parse(u.lastMeetingAt) >= thirtyDaysAgo).length,
  };

  res.json({ summary, users });
});

export default router;
