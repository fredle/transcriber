using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MeetingTranscriber.Services;

/// <summary>
/// One realtime AssemblyAI connection for one audio channel (mic or speaker).
///
/// Talks to the v3 streaming endpoint directly over a WebSocket rather than
/// through the SDK, because the parameters we need (universal-3-5-pro with
/// diarization) map cleanly onto documented query-string options and this
/// keeps the session lifecycle — in particular the mandatory Terminate — in
/// plain sight.
/// </summary>
public sealed class AssemblyAiStream : IAsyncDisposable
{
    // Realtime requires 50-1000 ms of audio per frame, sent no faster than
    // real time. Capture buffers are far smaller, so they are batched up.
    private const int SendChunkMs = 200;

    private readonly string _apiKey;
    private readonly int _sampleRate;
    private readonly string _label;
    private readonly int _bytesPerSend;

    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly List<byte> _pending = new();

    private Task? _receiveLoop;

    /// <summary>Fires once per finalised turn: (text, speakerLabel, startMs, endMs).</summary>
    public event Action<string, string?, int, int>? FinalTurn;

    /// <summary>Fires on transport or API errors, for surfacing in the log.</summary>
    public event Action<string>? Error;

    public AssemblyAiStream(string apiKey, int sampleRate, string label)
    {
        _apiKey = apiKey;
        _sampleRate = sampleRate;
        _label = label;
        // 16-bit mono
        _bytesPerSend = (int)(sampleRate * (SendChunkMs / 1000.0)) * 2;
    }

    public async Task StartAsync(CancellationToken cancel = default)
    {
        var uri = new Uri(
            "wss://streaming.assemblyai.com/v3/ws" +
            $"?sample_rate={_sampleRate}" +
            "&encoding=pcm_s16le" +
            "&speech_model=universal-3-5-pro" +
            "&mode=balanced" +
            "&speaker_labels=true");

        // The Authorization header is the raw key - no "Bearer" prefix.
        _socket.Options.SetRequestHeader("Authorization", _apiKey);
        await _socket.ConnectAsync(uri, cancel).ConfigureAwait(false);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    /// <summary>Queue raw 16-bit mono PCM captured from the device.</summary>
    public void Feed(byte[] pcm, int count)
    {
        if (_socket.State != WebSocketState.Open) return;

        List<byte[]>? ready = null;
        lock (_pending)
        {
            _pending.AddRange(pcm.AsSpan(0, count).ToArray());
            while (_pending.Count >= _bytesPerSend)
            {
                ready ??= new List<byte[]>();
                ready.Add(_pending.GetRange(0, _bytesPerSend).ToArray());
                _pending.RemoveRange(0, _bytesPerSend);
            }
        }
        if (ready == null) return;

        foreach (var frame in ready)
            _ = SendFrameAsync(frame);
    }

    private async Task SendFrameAsync(byte[] frame)
    {
        try
        {
            await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.SendAsync(frame, WebSocketMessageType.Binary, true, _cts.Token)
                                 .ConfigureAwait(false);
                }
            }
            finally { _sendLock.Release(); }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { Error?.Invoke($"[{_label}] send failed: {ex.Message}"); }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancel)
    {
        var buffer = new byte[64 * 1024];
        var message = new StringBuilder();
        try
        {
            while (!cancel.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(buffer, cancel).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;

                message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                var payload = message.ToString();
                message.Clear();
                HandleMessage(payload);
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch (WebSocketException ex) { Error?.Invoke($"[{_label}] socket closed: {ex.Message}"); }
        catch (Exception ex) { Error?.Invoke($"[{_label}] receive failed: {ex.Message}"); }
    }

    private void HandleMessage(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;

            switch (typeProp.GetString())
            {
                case "Turn":
                    // Partial turns stream continuously; only finalised ones are kept.
                    if (!root.TryGetProperty("end_of_turn", out var eot) || !eot.GetBoolean())
                        return;

                    var text = root.TryGetProperty("transcript", out var t)
                        ? (t.GetString() ?? "").Trim() : "";
                    if (text.Length == 0) return;

                    string? speaker = root.TryGetProperty("speaker_label", out var sp)
                        ? sp.GetString() : null;

                    int startMs = 0, endMs = 0;
                    if (root.TryGetProperty("words", out var words) &&
                        words.ValueKind == JsonValueKind.Array && words.GetArrayLength() > 0)
                    {
                        var first = words[0];
                        var last = words[words.GetArrayLength() - 1];
                        if (first.TryGetProperty("start", out var s)) startMs = s.GetInt32();
                        if (last.TryGetProperty("end", out var e)) endMs = e.GetInt32();
                    }

                    FinalTurn?.Invoke(text, speaker, startMs, endMs);
                    break;

                case "Error":
                    var msg = root.TryGetProperty("error", out var err) ? err.GetString() : payload;
                    Error?.Invoke($"[{_label}] {msg}");
                    break;
            }
        }
        catch (JsonException)
        {
            // A malformed frame is not worth tearing the session down for.
        }
    }

    /// <summary>
    /// Close the session cleanly. Sending Terminate matters: an abandoned
    /// session keeps accruing charges until the server-side cap.
    /// </summary>
    public async Task StopAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                var terminate = Encoding.UTF8.GetBytes("{\"type\":\"Terminate\"}");
                await _sendLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    await _socket.SendAsync(terminate, WebSocketMessageType.Text, true, CancellationToken.None)
                                 .ConfigureAwait(false);
                }
                finally { _sendLock.Release(); }

                // Give the server a moment to flush its final turns and any
                // late speaker revision before the socket goes away.
                if (_receiveLoop != null)
                    await Task.WhenAny(_receiveLoop, Task.Delay(3000)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { Error?.Invoke($"[{_label}] terminate failed: {ex.Message}"); }

        _cts.Cancel();
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                             .ConfigureAwait(false);
            }
        }
        catch { /* already gone */ }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
        _socket.Dispose();
        _sendLock.Dispose();
    }
}
