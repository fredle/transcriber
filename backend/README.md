# Teeline backend

Cloud Run service that authenticates users (Firebase Auth), mints short-lived
AssemblyAI tokens so the account key never reaches the desktop client, stores
transcripts/attendees in Firestore and screenshots in Cloud Storage, and
answers meeting questions via the Anthropic API.

## One-time GCP/Firebase setup

```bash
gcloud config set project YOUR_PROJECT_ID

gcloud services enable \
  run.googleapis.com \
  firestore.googleapis.com \
  storage.googleapis.com \
  secretmanager.googleapis.com \
  identitytoolkit.googleapis.com

# Firestore, native mode (pick your region)
gcloud firestore databases create --location=us-central1

# Bucket for screenshots
gcloud storage buckets create gs://YOUR_PROJECT_ID-teeline --location=us-central1

# Enable Firebase Auth's Google sign-in provider from the Firebase console
# (console.firebase.google.com -> Authentication -> Sign-in method -> Google),
# and create an OAuth client of type "Desktop app" under
# APIs & Services -> Credentials for the client's loopback flow.

# Secrets
printf '%s' 'YOUR_ASSEMBLYAI_KEY' | gcloud secrets create ASSEMBLYAI_API_KEY --data-file=-
printf '%s' 'YOUR_ANTHROPIC_KEY' | gcloud secrets create ANTHROPIC_API_KEY --data-file=-
```

Grant the Cloud Run service account access:

```bash
PROJECT_NUMBER=$(gcloud projects describe YOUR_PROJECT_ID --format='value(projectNumber)')
SA="$PROJECT_NUMBER-compute@developer.gserviceaccount.com"

gcloud projects add-iam-policy-binding YOUR_PROJECT_ID --member="serviceAccount:$SA" --role="roles/datastore.user"
gcloud secrets add-iam-policy-binding ASSEMBLYAI_API_KEY --member="serviceAccount:$SA" --role="roles/secretmanager.secretAccessor"
gcloud secrets add-iam-policy-binding ANTHROPIC_API_KEY --member="serviceAccount:$SA" --role="roles/secretmanager.secretAccessor"
gcloud storage buckets add-iam-policy-binding gs://YOUR_PROJECT_ID-teeline --member="serviceAccount:$SA" --role="roles/storage.objectAdmin"
```

## Deploy

```bash
gcloud run deploy teeline-backend \
  --source . \
  --region us-central1 \
  --no-allow-unauthenticated=false \
  --set-env-vars STORAGE_BUCKET=YOUR_PROJECT_ID-teeline,ADMIN_EMAILS=you@example.com \
  --set-secrets ASSEMBLYAI_API_KEY=ASSEMBLYAI_API_KEY:latest,ANTHROPIC_API_KEY=ANTHROPIC_API_KEY:latest
```

`ADMIN_EMAILS` is a comma-separated allowlist gating the `/v1/admin/*` routes
(defaults to `freddie@leatham.com` if unset - see `adminMiddleware.ts`).

Cloud Run's own IAM stays open (`allow-unauthenticated`) because every route
under `/v1` does its own auth via `requireAuth` (Firebase ID token
verification) - that's the actual authentication boundary, not IAM. Note the
`--no-allow-unauthenticated=false` above is equivalent to
`--allow-unauthenticated`; written that way to make the "app-level auth, not
IAM" decision explicit.

## Local development

```bash
npm install
gcloud auth application-default login   # local credentials for firebase-admin
export STORAGE_BUCKET=YOUR_PROJECT_ID-teeline
export ASSEMBLYAI_API_KEY=...
export ANTHROPIC_API_KEY=...
npm run dev
```

## Endpoints

All under `/v1`, all require `Authorization: Bearer <Firebase ID token>`.

| Method | Path | Purpose |
|---|---|---|
| POST | `/assemblyai/token` | Mint a short-lived AssemblyAI realtime token |
| POST | `/meetings` | Create/update meeting metadata |
| PATCH | `/meetings/:id` | Update title/group |
| GET | `/meetings` | List meetings for the caller |
| GET | `/meetings/:id` | Read one meeting |
| DELETE | `/meetings/:id` | Delete a meeting, its lines/attendees, and its screenshots |
| POST | `/meetings/:id/lines` | Append transcript line(s) |
| GET | `/meetings/:id/lines` | Read a meeting's transcript |
| POST | `/meetings/:id/attendees` | Append a join/leave event |
| GET | `/meetings/:id/attendees` | Read a meeting's attendee events |
| POST | `/meetings/:id/screenshots` | Get a signed upload URL |
| GET | `/meetings/:id/screenshots` | List screenshots (signed read URLs) |
| POST | `/meetings/:id/ask` | Ask a question about the transcript (Claude) |
| GET | `/admin/overview` | Users + usage summary (requires an `ADMIN_EMAILS` account) |
