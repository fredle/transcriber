namespace MeetingTranscriber.Services;

/// <summary>
/// Combines the mic and speaker capture feeds into the single 16-bit mono PCM
/// stream a merged <see cref="IAudioStream"/> expects, instead of one stream
/// per device. Used when Settings.MergeStreams is on: half the transcription
/// connections/segments, at the cost of losing "ME" vs "OTHER" attribution -
/// downstream, only AssemblyAI's own diarized speaker label can tell speakers
/// apart.
///
/// Mic and speaker loopback are independent WASAPI captures that are not
/// clock-synced and often run at different native sample rates, so each
/// side's audio is resampled to a common target rate as it arrives, queued,
/// and drained on a fixed timer tick that sums the two queues sample-for-
/// sample (treating a starved queue as silence rather than blocking on it,
/// so one side going quiet never stalls the other).
/// </summary>
public sealed class AudioMixer : IDisposable
{
    private const int DrainIntervalMs = 20;

    private readonly int _targetRate;
    private readonly Action<byte[], int> _onMixed;
    private readonly object _lock = new();
    private readonly List<short> _micQueue = new();
    private readonly List<short> _speakerQueue = new();
    private readonly Timer _timer;

    public int TargetRate => _targetRate;

    /// <param name="targetRate">Common sample rate to mix at - pass the mic's native rate.</param>
    /// <param name="onMixed">Called on the timer thread with the mixed 16-bit mono PCM for each tick.</param>
    public AudioMixer(int targetRate, Action<byte[], int> onMixed)
    {
        _targetRate = targetRate;
        _onMixed = onMixed;
        _timer = new Timer(_ => Drain(), null, DrainIntervalMs, DrainIntervalMs);
    }

    public void FeedMic(byte[] pcm, int count, int sourceRate) => Feed(_micQueue, pcm, count, sourceRate);
    public void FeedSpeaker(byte[] pcm, int count, int sourceRate) => Feed(_speakerQueue, pcm, count, sourceRate);

    private void Feed(List<short> queue, byte[] pcm, int count, int sourceRate)
    {
        var samples = BytesToSamples(pcm, count);
        var resampled = sourceRate == _targetRate ? samples : Resample(samples, sourceRate, _targetRate);
        lock (_lock) queue.AddRange(resampled);
    }

    private void Drain()
    {
        var chunkSamples = _targetRate * DrainIntervalMs / 1000;
        short[] mic, speaker;
        lock (_lock)
        {
            mic = TakeUpTo(_micQueue, chunkSamples);
            speaker = TakeUpTo(_speakerQueue, chunkSamples);
        }
        if (mic.Length == 0 && speaker.Length == 0) return;

        var count = Math.Max(mic.Length, speaker.Length);
        var mixed = new byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            var m = i < mic.Length ? mic[i] : (short)0;
            var s = i < speaker.Length ? speaker[i] : (short)0;
            var sum = Math.Clamp(m + s, short.MinValue, short.MaxValue);
            mixed[i * 2] = (byte)(sum & 0xFF);
            mixed[i * 2 + 1] = (byte)((sum >> 8) & 0xFF);
        }
        _onMixed(mixed, mixed.Length);
    }

    private static short[] TakeUpTo(List<short> queue, int max)
    {
        var take = Math.Min(max, queue.Count);
        if (take == 0) return Array.Empty<short>();
        var result = queue.GetRange(0, take).ToArray();
        queue.RemoveRange(0, take);
        return result;
    }

    private static short[] BytesToSamples(byte[] pcm, int count)
    {
        var samples = new short[count / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
        return samples;
    }

    /// <summary>Linear-interpolation resample - good enough for speech intelligibility, not audiophile quality.</summary>
    private static short[] Resample(short[] input, int fromRate, int toRate)
    {
        if (input.Length == 0) return input;
        var outLength = (int)((long)input.Length * toRate / fromRate);
        var output = new short[outLength];
        var ratio = (double)fromRate / toRate;
        for (var i = 0; i < outLength; i++)
        {
            var srcPos = i * ratio;
            var srcIndex = (int)srcPos;
            var frac = srcPos - srcIndex;
            var a = input[Math.Min(srcIndex, input.Length - 1)];
            var b = input[Math.Min(srcIndex + 1, input.Length - 1)];
            output[i] = (short)(a + (b - a) * frac);
        }
        return output;
    }

    public void Dispose() => _timer.Dispose();
}
