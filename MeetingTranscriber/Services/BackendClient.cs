using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object body, CancellationToken cancel)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            var token = await _auth.GetIdTokenAsync(cancel).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, $"{BaseUrl}{path}")
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _http.SendAsync(request, cancel).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 1)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var text = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
                    throw new InvalidOperationException($"Backend request failed ({(int)response.StatusCode}): {text}");
                }
                return response;
            }
            // Force a fresh token and try once more before giving up.
            response.Dispose();
            _auth.InvalidateCachedToken();
        }
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
}
