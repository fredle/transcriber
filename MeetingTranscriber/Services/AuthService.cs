using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MeetingTranscriber.Services;

/// <summary>
/// Signs the user in with their Google account and hands out Firebase ID
/// tokens for calling the backend. Uses the same "installed app" pattern as
/// `gcloud auth login`: open the system browser to Google's consent screen,
/// catch the redirect on a local loopback listener, then exchange the
/// resulting Google credential for a Firebase session via the Identity
/// Toolkit REST API. Only a refresh token is persisted (DPAPI-protected,
/// alongside the rest of Settings) - ID tokens are minted from it on demand
/// and never written to disk.
/// </summary>
public sealed class AuthService
{
    private const string GoogleAuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string GoogleTokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string IdentityToolkitSignInWithIdp = "https://identitytoolkit.googleapis.com/v1/accounts:signInWithIdp";
    private const string SecureTokenEndpoint = "https://securetoken.googleapis.com/v1/token";

    private readonly Settings _settings;
    private readonly HttpClient _http = new();

    private string? _idToken;
    private DateTimeOffset _idTokenExpiry = DateTimeOffset.MinValue;

    public string? Email { get; private set; }
    public bool IsSignedIn => !string.IsNullOrEmpty(_settings.RefreshToken);

    public AuthService(Settings settings)
    {
        _settings = settings;
        Email = string.IsNullOrEmpty(settings.AccountEmail) ? null : settings.AccountEmail;
    }

    /// <summary>
    /// Opens the system browser for the user to sign in with Google, then
    /// exchanges the result for a Firebase session. Throws if configuration
    /// is missing or the user closes the browser without completing sign-in.
    /// </summary>
    public async Task SignInInteractiveAsync(CancellationToken cancel = default)
    {
        if (!CloudConfig.IsConfigured)
            throw new InvalidOperationException(
                "This build has no cloud configuration baked in - it wasn't produced by the release pipeline.");

        using var listener = new HttpListener();
        var port = GetFreeLoopbackPort();
        var redirectUri = $"http://127.0.0.1:{port}/callback/";
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        var authUrl = $"{GoogleAuthEndpoint}?client_id={Uri.EscapeDataString(CloudConfig.GoogleClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&response_type=code&scope=openid%20email%20profile&access_type=offline&prompt=consent";

        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        string code;
        using (cancel.Register(() => { try { listener.Stop(); } catch (Exception) { } }))
        {
            var context = await listener.GetContextAsync().ConfigureAwait(false);
            var query = context.Request.QueryString;
            var error = query["error"];
            code = query["code"] ?? "";

            // window.open('','_self').close() closes a tab the browser opened
            // for a plain navigation (not via script), which window.close()
            // alone can't do in most browsers - the standard workaround. The
            // visible text is only a fallback for the rare browser that blocks
            // even that.
            const string autoClose = "<script>window.open('','_self').close();</script>";
            var html = error == null
                ? $"<html><body>{autoClose}Signed in - this window should close automatically.</body></html>"
                : $"<html><body>{autoClose}Sign-in failed: {WebUtility.HtmlEncode(error)}</body></html>";
            var buffer = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, cancel).ConfigureAwait(false);
            context.Response.OutputStream.Close();

            if (error != null || code.Length == 0)
                throw new InvalidOperationException($"Google sign-in was not completed ({error ?? "no code returned"}).");
        }
        listener.Stop();

        var googleIdToken = await ExchangeCodeForGoogleIdTokenAsync(code, redirectUri, cancel).ConfigureAwait(false);
        await SignInToFirebaseAsync(googleIdToken, cancel).ConfigureAwait(false);
    }

    /// <summary>Forces the next GetIdTokenAsync call to mint a fresh token instead of reusing the cached one.</summary>
    public void InvalidateCachedToken() => _idTokenExpiry = DateTimeOffset.MinValue;

    public void SignOut()
    {
        _settings.RefreshToken = "";
        _settings.AccountEmail = "";
        _settings.Save();
        _idToken = null;
        _idTokenExpiry = DateTimeOffset.MinValue;
        Email = null;
    }

    /// <summary>Returns a currently-valid Firebase ID token, refreshing silently if needed.</summary>
    public async Task<string> GetIdTokenAsync(CancellationToken cancel = default)
    {
        if (_idToken != null && DateTimeOffset.UtcNow < _idTokenExpiry - TimeSpan.FromSeconds(60))
            return _idToken;

        if (string.IsNullOrEmpty(_settings.RefreshToken))
            throw new InvalidOperationException("Not signed in.");

        var resp = await _http.PostAsync(
            $"{SecureTokenEndpoint}?key={Uri.EscapeDataString(CloudConfig.FirebaseApiKey)}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _settings.RefreshToken,
            }),
            cancel).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            // A rejected refresh token means the session is gone - clear it
            // rather than retrying the same failing token forever.
            SignOut();
            throw new InvalidOperationException("Your sign-in has expired. Please sign in again.");
        }

        var body = await resp.Content.ReadFromJsonAsync<RefreshResponse>(cancellationToken: cancel).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Unexpected response refreshing the sign-in session.");

        _idToken = body.IdToken;
        _idTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(double.Parse(body.ExpiresIn));
        if (!string.IsNullOrEmpty(body.RefreshToken) && body.RefreshToken != _settings.RefreshToken)
        {
            _settings.RefreshToken = body.RefreshToken;
            _settings.Save();
        }
        return _idToken;
    }

    private async Task<string> ExchangeCodeForGoogleIdTokenAsync(string code, string redirectUri, CancellationToken cancel)
    {
        var resp = await _http.PostAsync(GoogleTokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = CloudConfig.GoogleClientId,
                ["client_secret"] = CloudConfig.GoogleClientSecret,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
            }),
            cancel).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            throw new InvalidOperationException($"Google sign-in failed: {text}");
        }

        var body = await resp.Content.ReadFromJsonAsync<GoogleTokenResponse>(cancellationToken: cancel).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Unexpected response from Google during sign-in.");
        return body.IdToken;
    }

    private async Task SignInToFirebaseAsync(string googleIdToken, CancellationToken cancel)
    {
        var payload = new
        {
            postBody = $"id_token={Uri.EscapeDataString(googleIdToken)}&providerId=google.com",
            requestUri = "http://localhost",
            returnIdpCredential = true,
            returnSecureToken = true,
        };

        var resp = await _http.PostAsJsonAsync(
            $"{IdentityToolkitSignInWithIdp}?key={Uri.EscapeDataString(CloudConfig.FirebaseApiKey)}",
            payload, cancel).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            throw new InvalidOperationException($"Firebase sign-in failed: {text}");
        }

        var body = await resp.Content.ReadFromJsonAsync<FirebaseSignInResponse>(cancellationToken: cancel).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Unexpected response from Firebase during sign-in.");

        _idToken = body.IdToken;
        _idTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(double.Parse(body.ExpiresIn));
        Email = body.Email;
        _settings.RefreshToken = body.RefreshToken;
        _settings.AccountEmail = body.Email ?? "";
        _settings.Save();
    }

    private static int GetFreeLoopbackPort()
    {
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private sealed class GoogleTokenResponse
    {
        [JsonPropertyName("id_token")] public string IdToken { get; set; } = "";
    }

    private sealed class FirebaseSignInResponse
    {
        [JsonPropertyName("idToken")] public string IdToken { get; set; } = "";
        [JsonPropertyName("refreshToken")] public string RefreshToken { get; set; } = "";
        [JsonPropertyName("expiresIn")] public string ExpiresIn { get; set; } = "3600";
        [JsonPropertyName("email")] public string? Email { get; set; }
    }

    private sealed class RefreshResponse
    {
        [JsonPropertyName("id_token")] public string IdToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
        [JsonPropertyName("expires_in")] public string ExpiresIn { get; set; } = "3600";
    }
}
