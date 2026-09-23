namespace MeetingTranscriber.Services;

/// <summary>
/// Pause-segmented batch transcription: buffers incoming PCM until a
/// sustained silence marks the end of a turn, then uploads that segment to
/// AssemblyAI's async API (via the backend) and raises FinalTurn once the
/// result comes back - typically a few seconds, up to ~45s.
///
/// Roughly a third to two-thirds cheaper than the realtime socket
/// (AssemblyAI bills async transcription per audio-hour submitted, not per
/// second of connection time held open), at the cost of that per-turn delay
/// instead of live captions.
/// </summary>
public sealed class AssemblyAiBatchStream : IAudioStream
{
    // A pause this long is assumed to be a genuine turn boundary rather than
    // someone just taking a breath mid-sentence.
    private const int SilenceEndsSegmentMs = 900;
    // Below this many finished ms of audio, a "segment" is almost certainly
    // a stray noise/click rather than speech worth paying to transcribe.
    private const int MinSegmentMs = 400;
    // A very quiet sample still counts as "silence" up to this amplitude
    // (16-bit PCM peak is 32767) - well below normal speech level.
    private const short SilenceAmplitudeThreshold = 400;

    private readonly BackendClient _backend;
    private readonly int _sampleRate;
    private readonly string _label;
    private readonly string _speechModel;
    private readonly bool _diarization;

    private readonly object _lock = new();
    private readonly List<byte> _segment = new();
    private int _silenceMs;
    private int _segmentMs;

    private readonly List<Task> _inFlight = new();
    private readonly object _inFlightLock = new();

    public event Action<string, string?, int, int>? FinalTurn;
    public event Action<string>? Error;

    public AssemblyAiBatchStream(
        BackendClient backend,
        int sampleRate,
        string label,
        string speechModel = "universal-streaming-english",
        bool diarization = true)
    {
        _backend = backend;
        _sampleRate = sampleRate;
        _label = label;
        _speechModel = speechModel;
        _diarization = diarization;
    }

    public Task StartAsync(CancellationToken cancel = default) => Task.CompletedTask;

    public void Feed(byte[] pcm, int count)
    {
        var durationMs = (int)(count / 2.0 / _sampleRate * 1000);
        var silent = IsSilent(pcm, count);

        byte[]? toFlush = null;
        lock (_lock)
        {
            _segment.AddRange(pcm.AsSpan(0, count).ToArray());
            _segmentMs += durationMs;

            if (silent)
            {
                _silenceMs += durationMs;
                if (_silenceMs >= SilenceEndsSegmentMs && _segmentMs - _silenceMs >= MinSegmentMs)
                {
                    toFlush = _segment.ToArray();
                    _segment.Clear();
                    _silenceMs = 0;
                    _segmentMs = 0;
                }
            }
            else
            {
                _silenceMs = 0;
            }
        }

        if (toFlush != null) FlushSegment(toFlush);
    }

    private static bool IsSilent(byte[] pcm, int count)
    {
        var peak = 0;
        for (var i = 0; i + 1 < count; i += 2)
        {
            var sample = Math.Abs((short)(pcm[i] | (pcm[i + 1] << 8)));
            if (sample > peak) peak = sample;
        }
        return peak < SilenceAmplitudeThreshold;
    }

    private void FlushSegment(byte[] pcm)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                var wav = WavEncoder.Encode(pcm, _sampleRate);
                var utterances = await _backend.TranscribeBatchAsync(wav, _speechModel, _diarization, CancellationToken.None)
                    .ConfigureAwait(false);
                foreach (var u in utterances)
                {
                    if (string.IsNullOrWhiteSpace(u.Text)) continue;
                    FinalTurn?.Invoke(u.Text, u.Speaker, u.StartMs, u.EndMs);
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke($"[{_label}] batch transcription failed: {ex.Message}");
            }
        });

        lock (_inFlightLock)
        {
            _inFlight.RemoveAll(t => t.IsCompleted);
            _inFlight.Add(task);
        }
    }

    /// <summary>Flushes any buffered audio and waits for every outstanding batch request to finish, so nothing said right before stopping is lost.</summary>
    public async Task StopAsync()
    {
        byte[]? toFlush = null;
        lock (_lock)
        {
            if (_segmentMs - _silenceMs >= MinSegmentMs)
                toFlush = _segment.ToArray();
            _segment.Clear();
            _silenceMs = 0;
            _segmentMs = 0;
        }
        if (toFlush != null) FlushSegment(toFlush);

        Task[] pending;
        lock (_inFlightLock) pending = _inFlight.ToArray();
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (Exception) { /* individual failures already surfaced via Error */ }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
