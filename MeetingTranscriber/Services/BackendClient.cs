using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MeetingTranscriber.Services;

/// <summary>
/// Thin client for the Teeline backend. Every call attaches the current
/// Firebase ID token and retries once after a silent refresh on a 401 -
/// callers don't need to think about token lifetime.
/// </summary>
public sealed class BackendClient
{
    private readonly AuthService _auth;
    private readonly HttpClient _http = new();
    private static readonly string BaseUrl = CloudConfig.BackendUrl.TrimEnd('/');

    public BackendClient(AuthService auth)
    {
        _auth = auth;
    }

    public async Task<string> MintAssemblyAiTokenAsync(CancellationToken cancel = default)
    {
        var resp = await SendAsync(HttpMethod.Post, "/v1/assemblyai/token", new { }, cancel).ConfigureAwait(false);
        var body = await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancel).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Backend returned no token.");
        return body.Token;
    }

    public async Task CreateOrUpdateMeetingAsync(string meetingId, string title, DateTime? started, string engine, string group, CancellationToken cancel = default)
    {
        await SendAsync(HttpMethod.Post, "/v1/meetings", new
        {
            meetingId,
            title,
            started = started?.ToString("o"),
            engine,
            group,
        }, cancel).ConfigureAwait(false);
    }

    public async Task AppendLineAsync(string meetingId, string speaker, string? speakerLabel, string text, int startMs, int endMs, CancellationToken cancel = default)
    {
        await SendAsync(HttpMethod.Post, $"/v1/meetings/{Uri.EscapeDataString(meetingId)}/lines", new
        {
            speaker,
            speakerLabel,
            text,
            startMs,
            endMs,
        }, cancel).ConfigureAwait(false);
    }

    public async Task UpdateMeetingTitleAsync(string meetingId, string title, CancellationToken cancel = default)
    {
        await SendAsync(HttpMethod.Patch, $"/v1/meetings/{Uri.EscapeDataString(meetingId)}", new { title }, cancel).ConfigureAwait(false);
    }

    public async Task UpdateMeetingGroupAsync(string meetingId, string group, CancellationToken cancel = default)
    {
        await SendAsync(HttpMethod.Patch, $"/v1/meetings/{Uri.EscapeDataString(meetingId)}", new { group }, cancel).ConfigureAwait(false);
    }

    /// <summary>Deletes a meeting from the cloud so it isn't pulled back down on the next sync.</summary>
    public async Task DeleteMeetingAsync(string meetingId, CancellationToken cancel = default)
    {
        await SendAsync(HttpMethod.Delete, $"/v1/meetings/{Uri.EscapeDataString(meetingId)}", null, cancel).ConfigureAwait(false);
    }

    /// <summary>Pushes a meeting's RTF notes, or clears them remotely if rtfBytes is null.</summary>
    public async Task UpdateMeetingNotesAsync(string meetingId, byte[]? rtfBytes, CancellationToken cancel = default)
    {
        var notes = rtfBytes == null ? null : Convert.ToBase64String(rtfBytes);
        await SendAsync(HttpMethod.Patch, $"/v1/meetings/{Uri.EscapeDataString(meetingId)}", new { notes }, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the whole remote transcript with the given lines, in order -
    /// used to push a local edit (speaker reassignment, line deletion) back
    /// up, since those change or remove existing lines rather than appending.
    /// </summary>
    public async Task ReplaceLinesAsync(string meetingId, IEnumerable<TranscriptLine> lines, CancellationToken cancel = default)
    {
        var payload = new
        {
            lines = lines.Select(l => new
            {
                speaker = l.Speaker,
                speakerLabel = l.SpeakerLabel,
                text = l.Text,
                timestamp = l.Timestamp?.ToString("o"),
            }),
        };
        await SendAsync(HttpMethod.Put, $"/v1/meetings/{Uri.EscapeDataString(meetingId)}/lines", payload, cancel).ConfigureAwait(false);
    }

    public async Task AppendAttendeeEventAsync(string meetingId, string name, bool joined, DateTime at, CancellationToken cancel = default)
    {
        await SendAsync(HttpMethod.Post, $"/v1/meetings/{Uri.EscapeDataString(meetingId)}/attendees", new
        {
            name,
            joined,
            timestamp = at.ToString("o"),
        }, cancel).ConfigureAwait(false);
    }

    public async Task<(string Id, string UploadUrl)> RequestScreenshotUploadUrlAsync(string meetingId, CancellationToken cancel = default)
    {
        var resp = await SendAsync(HttpMethod.Post, $"/v1/meetings/{Uri.EscapeDataString(meetingId)}/screenshots", new { }, cancel).ConfigureAwait(false);
        var body = await resp.Content.ReadFromJsonAsync<ScreenshotUrlResponse>(cancellationToken: cancel).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Backend returned no upload URL.");
        return (body.Id, body.UploadUrl);
    }

    public async Task UploadScreenshotAsync(string uploadUrl, byte[] png, CancellationToken cancel = default)
    {
        using var content = new ByteArrayContent(png);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        using var resp = await _http.PutAsync(uploadUrl, content, cancel).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<string> AskAsync(string meetingId, string question, CancellationToken cancel = default)
    {
        var resp = await SendAsync(HttpMethod.Post, $"/v1/meetings/{Uri.EscapeDataString(meetingId)}/ask", new { question }, cancel).ConfigureAwait(false);
        var body = await resp.Content.ReadFromJsonAsync<AskResponse>(cancellationToken: cancel).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Backend returned no answer.");
        return body.Answer;
    }

    /// <summary>Every meeting this account has in the cloud, newest first - the source list for pulling down what's missing locally. Notes are omitted here (fetch with GetMeetingAsync) to keep the list light.</summary>
    public async Task<List<RemoteMeeting>> GetMeetingsAsync(int limit = 500, CancellationToken cancel = default)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/v1/meetings?limit={limit}", null, cancel).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<List<RemoteMeeting>>(cancellationToken: cancel).ConfigureAwait(false)
            ?? new List<RemoteMeeting>();
    }

    public async Task<RemoteMeeting?> GetMeetingAsync(string meetingId, CancellationToken cancel = default)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/v1/meetings/{Uri.EscapeDataString(meetingId)}", null, cancel).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<RemoteMeeting>(cancellationToken: cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Uploads a pause-segmented chunk of audio (as a WAV file) and waits for
    /// AssemblyAI's async transcription of it, via the backend so the raw
    /// AssemblyAI key never reaches this process. Can take several seconds to
    /// ~45s to return, matching AssemblyAI's typical async turnaround.
    /// </summary>
    public async Task<List<BatchUtterance>> TranscribeBatchAsync(byte[] wavBytes, string speechModel, bool diarization, CancellationToken cancel = default)
    {
        var path = $"/v1/assemblyai/transcribe-batch?speechModel={Uri.EscapeDataString(speechModel)}&diarization={(diarization ? "true" : "false")}";
        var resp = await SendWithContentAsync(HttpMethod.Post, path, () =>
        {
            var content = new ByteArrayContent(wavBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            return content;
        }, cancel).ConfigureAwait(false);
        var body = await resp.Content.ReadFromJsonAsync<BatchTranscribeResponse>(cancellationToken: cancel).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Backend returned no batch transcript.");
        return body.Utterances;
    }

    public async Task<List<RemoteLine>> GetLinesAsync(string meetingId, CancellationToken cancel = default)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/v1/meetings/{Uri.EscapeDataString(meetingId)}/lines", null, cancel).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<List<RemoteLine>>(cancellationToken: cancel).ConfigureAwait(false)
            ?? new List<RemoteLine>();
    }

    public async Task<List<RemoteAttendeeEvent>> GetAttendeeEventsAsync(string meetingId, CancellationToken cancel = default)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/v1/meetings/{Uri.EscapeDataString(meetingId)}/attendees", null, cancel).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<List<RemoteAttendeeEvent>>(cancellationToken: cancel).ConfigureAwait(false)
            ?? new List<RemoteAttendeeEvent>();
    }

    public async Task<List<RemoteScreenshot>> GetScreenshotsAsync(string meetingId, CancellationToken cancel = default)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/v1/meetings/{Uri.EscapeDataString(meetingId)}/screenshots", null, cancel).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<List<RemoteScreenshot>>(cancellationToken: cancel).ConfigureAwait(false)
            ?? new List<RemoteScreenshot>();
    }

    /// <summary>Downloads bytes from a signed GCS URL - no auth header needed, the signature is the credential.</summary>
    public async Task<byte[]> DownloadAsync(string url, CancellationToken cancel = default)
    {
        using var resp = await _http.GetAsync(url, cancel).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancel) =>
        SendWithContentAsync(method, path, () => body == null ? null : JsonContent.Create(body), cancel);

    /// <summary>
    /// Takes a content factory rather than a materialised HttpContent: each
    /// retry attempt (after a 401 forces a fresh token) needs its own
    /// HttpContent instance, since the previous attempt's request/content was
    /// already disposed when its `using` block ended.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithContentAsync(HttpMethod method, string path, Func<HttpContent?> contentFactory, CancellationToken cancel)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            var token = await _auth.GetIdTokenAsync(cancel).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, $"{BaseUrl}{path}")
            {
                Content = contentFactory(),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _http.SendAsync(request, cancel).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 1)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var text = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
                    throw new InvalidOperationException($"{ExtractErrorMessage(text)} ({(int)response.StatusCode})");
                }
                return response;
            }
            // Force a fresh token and try once more before giving up.
            response.Dispose();
            _auth.InvalidateCachedToken();
        }
    }

    /// <summary>Pulls the {"error": "..."} message out of a backend error body, falling back to the raw text if it's not that shape.</summary>
    private static string ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                return error.GetString()!;
        }
        catch (JsonException)
        {
        }
        return string.IsNullOrWhiteSpace(body) ? "Backend request failed." : body;
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("token")] public string Token { get; set; } = "";
    }

    private sealed class ScreenshotUrlResponse
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("uploadUrl")] public string UploadUrl { get; set; } = "";
    }

    private sealed class AskResponse
    {
        [JsonPropertyName("answer")] public string Answer { get; set; } = "";
    }

    private sealed class BatchTranscribeResponse
    {
        [JsonPropertyName("utterances")] public List<BatchUtterance> Utterances { get; set; } = new();
    }
}

public sealed class BatchUtterance
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("speaker")] public string? Speaker { get; set; }
    [JsonPropertyName("startMs")] public int StartMs { get; set; }
    [JsonPropertyName("endMs")] public int EndMs { get; set; }
}

public sealed class RemoteMeeting
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("started")] public string? Started { get; set; }
    [JsonPropertyName("engine")] public string Engine { get; set; } = "assemblyai";
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    /// <summary>RTF notes, base64-encoded. Only populated by GetMeetingAsync, not the list.</summary>
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    /// <summary>ISO timestamp of the last server-side write to this meeting (doc, lines, or attendees).</summary>
    [JsonPropertyName("updatedAt")] public string? UpdatedAt { get; set; }
}

public sealed class RemoteLine
{
    [JsonPropertyName("speaker")] public string Speaker { get; set; } = "";
    [JsonPropertyName("speakerLabel")] public string? SpeakerLabel { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
}

public sealed class RemoteAttendeeEvent
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("joined")] public bool Joined { get; set; }
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
}

public sealed class RemoteScreenshot
{
    [JsonPropertyName("objectPath")] public string ObjectPath { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}
