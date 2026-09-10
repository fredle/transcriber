namespace MeetingTranscriber.Services;

/// <summary>
/// Build-time configuration for the Teeline backend this app talks to. None
/// of these values are secret in the sense of needing to be hidden from end
/// users - a Firebase Web API key and an installed-app OAuth client id/secret
/// are both designed to live in distributed client binaries (that's Google's
/// own guidance for "Desktop app" OAuth clients) - but they're still not
/// committed here as plaintext, since a public repo makes them trivially
/// scrapeable at a scale individual end users publishing scraped-key spam
/// don't operate at, and GitHub's push protection rightly blocks it anyway.
///
/// The real values are injected by .github/workflows/release.yml (from
/// repository secrets) immediately before `dotnet publish`, so the values
/// baked into a released binary are real - only the source checked into git
/// has placeholders. For local development, fill these in on your own
/// machine and keep the change unstaged (`git update-index --skip-worktree
/// MeetingTranscriber/Services/CloudConfig.cs` avoids accidentally
/// committing them back).
/// </summary>
public static class CloudConfig
{
    public const string BackendUrl = "__TEELINE_BACKEND_URL__";
    public const string FirebaseApiKey = "__TEELINE_FIREBASE_API_KEY__";
    public const string GoogleClientId = "__TEELINE_GOOGLE_CLIENT_ID__";
    public const string GoogleClientSecret = "__TEELINE_GOOGLE_CLIENT_SECRET__";

    /// <summary>False when the placeholders above haven't been substituted - e.g. a local dev build straight from source.</summary>
    public static bool IsConfigured => !GoogleClientId.StartsWith("__TEELINE_");
}
