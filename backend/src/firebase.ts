import { initializeApp, applicationDefault } from "firebase-admin/app";
import { getAuth } from "firebase-admin/auth";
import { getFirestore } from "firebase-admin/firestore";
import { getStorage } from "firebase-admin/storage";

// On Cloud Run this picks up the service account attached to the revision;
// locally it uses GOOGLE_APPLICATION_CREDENTIALS.
const app = initializeApp({
  credential: applicationDefault(),
  storageBucket: process.env.STORAGE_BUCKET,
});

export const auth = getAuth(app);
export const db = getFirestore(app);
export const bucket = getStorage(app).bucket();

/** users/{uid}/meetings/{meetingId} */
export function meetingsCollection(uid: string) {
  return db.collection("users").doc(uid).collection("meetings");
}

export function meetingDoc(uid: string, meetingId: string) {
  return meetingsCollection(uid).doc(meetingId);
}
