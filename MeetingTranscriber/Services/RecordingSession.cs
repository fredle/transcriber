using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingTranscriber.Services;

/// <summary>
/// One recording run: captures the microphone and the speaker loopback,
/// streams both to AssemblyAI, and writes the transcript to disk.
///
/// The mic is written as [ME] and the speaker loopback as [OTHER]; diarised
/// speaker labels are only meaningful on the speaker channel, which mixes the
/// remote participants.
/// </summary>
public sealed class RecordingSession : IAsyncDisposable
{
    // How often to check whether the Teams meeting changed, and how many
    // consecutive checks a new title must survive before it counts. Teams
    // reports transitional titles when moving between calls, so a single
    // sighting is not enough to split a recording.
    private static readonly TimeSpan TitlePollInterval = TimeSpan.FromSeconds(4);
    private const int TitleStablePolls = 2;

    // How often to check who's on the call, and how many consecutive polls a
    // sighting must survive before it counts, in either direction. Teams'
    // accessibility tree can return a short/garbled read transiently (e.g.
    // right as it's still spinning up) - occasionally even a bogus phantom
    // entry rather than just an incomplete one - so a single sighting is not
    // enough to record a join, and a single miss is not enough to record a
    // departure.
    private static readonly TimeSpan AttendeePollInterval = TimeSpan.FromSeconds(5);
    private const int AttendeeStablePolls = 2;

    private readonly string _apiKey;
    private AudioDevice _micDevice;
    private AudioDevice _speakerDevice;

    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _loopbackCapture;
    private AssemblyAiStream? _micStream;
    private AssemblyAiStream? _speakerStream;

    private readonly object _writerLock = new();
    private StreamWriter? _writer;
    private string _folder = "";
    private string? _currentTitle;

    // Guards _presentAttendees/_absentStreaks, which WatchAttendeesAsync
    // mutates on its own poll loop while StartNewSessionFile/StopAsync (on
    // the title watcher's or the caller's thread) can close them out at the
    // same time.
    private readonly object _attendeeLock = new();
    private readonly Dictionary<string, DateTime> _presentAttendees = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _candidateStreaks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _absentStreaks = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _watcherCts;

    // Level metering for the UI, sampled by the chart rather than pushed.
    private float _micLevel;
    private float _speakerLevel;

    public event Action<string, string?, string>? TranscriptLine;  // speaker, label, text
    public event Action<string, string>? NewSession;               // folder, title
    public event Action<string>? Log;

    public string Folder => _folder;
    public float MicLevel => _micLevel;
    public float SpeakerLevel => _speakerLevel;
    public AudioDevice MicDevice => _micDevice;
    public AudioDevice SpeakerDevice => _speakerDevice;

    /// <summary>Snapshot of remote participants currently tracked as present, sorted for stable display.</summary>
    public List<string> CurrentAttendees()
    {
        lock (_attendeeLock)
            return _presentAttendees.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public RecordingSession(string apiKey, AudioDevice mic, AudioDevice speaker)
    {
        _apiKey = apiKey;
        _micDevice = mic;
        _speakerDevice = speaker;
    }

    public async Task StartAsync()
    {
        var micDevice = AudioDevices.GetById(_micDevice.Id)
            ?? throw new InvalidOperationException($"Microphone '{_micDevice.Name}' is unavailable.");
        var speakerDevice = AudioDevices.GetById(_speakerDevice.Id)
            ?? throw new InvalidOperationException($"Speaker '{_speakerDevice.Name}' is unavailable.");

        _micCapture = new WasapiCapture(micDevice);
        _loopbackCapture = new WasapiLoopbackCapture(speakerDevice);

        // AssemblyAI accepts 8k-96k, so stream at each device's native rate
        // rather than resampling and losing quality on the way.
        var micRate = _micCapture.WaveFormat.SampleRate;
        var speakerRate = _loopbackCapture.WaveFormat.SampleRate;

        _currentTitle = TeamsMonitor.GetMeetingTitle();
        StartNewSessionFile(_currentTitle, announce: false);

        _micStream = new AssemblyAiStream(_apiKey, micRate, "mic");
        _speakerStream = new AssemblyAiStream(_apiKey, speakerRate, "speaker");
        _micStream.FinalTurn += (text, label, s, e) => WriteLine("ME", label, text, s, e);
        _speakerStream.FinalTurn += (text, label, s, e) => WriteLine("OTHER", label, text, s, e);
        _micStream.Error += m => Log?.Invoke(m);
        _speakerStream.Error += m => Log?.Invoke(m);

        await _micStream.StartAsync().ConfigureAwait(false);
        await _speakerStream.StartAsync().ConfigureAwait(false);

        // Bound to the local capture/stream rather than the field: once a
        // live device swap can repoint the fields mid-flight (see
        // ChangeMicDeviceAsync/ChangeSpeakerDeviceAsync below), a callback
        // still in flight from the outgoing capture must keep feeding the
        // stream it was actually opened against, not whatever is current.
        var micCapture = _micCapture;
        var micStream = _micStream;
        micCapture.DataAvailable += (_, e) =>
            Forward(e, micCapture.WaveFormat, micStream, ref _micLevel);
        var loopbackCapture = _loopbackCapture;
        var speakerStream = _speakerStream;
        loopbackCapture.DataAvailable += (_, e) =>
            Forward(e, loopbackCapture.WaveFormat, speakerStream, ref _speakerLevel);

        _micCapture.StartRecording();
        _loopbackCapture.StartRecording();

        _watcherCts = new CancellationTokenSource();
        _ = Task.Run(() => WatchMeetingTitleAsync(_watcherCts.Token));
        _ = Task.Run(() => WatchAttendeesAsync(_watcherCts.Token));

        Log?.Invoke($"Transcribing to {_folder}");
        Log?.Invoke($"Mic: {_micDevice.Name} @ {micRate} Hz");
        Log?.Invoke($"Speaker: {_speakerDevice.Name} @ {speakerRate} Hz (loopback)");
    }

    /// <summary>
    /// Swap the live microphone device without interrupting the recording.
    /// Opens the new capture and a fresh AssemblyAI connection first, and
    /// only tears the old ones down once that succeeds - so a device that
    /// fails to open (removed, in exclusive use, etc.) leaves the previous
    /// one running rather than losing audio. Diarisation state for this
    /// channel restarts on the new connection.
    /// </summary>
    public async Task ChangeMicDeviceAsync(AudioDevice device)
    {
        var mmDevice = AudioDevices.GetById(device.Id)
            ?? throw new InvalidOperationException($"Microphone '{device.Name}' is unavailable.");

        var capture = new WasapiCapture(mmDevice);
        var rate = capture.WaveFormat.SampleRate;
        var stream = new AssemblyAiStream(_apiKey, rate, "mic");
        stream.FinalTurn += (text, label, s, e) => WriteLine("ME", label, text, s, e);
        stream.Error += m => Log?.Invoke(m);
        await stream.StartAsync().ConfigureAwait(false);
        capture.DataAvailable += (_, e) => Forward(e, capture.WaveFormat, stream, ref _micLevel);

        var oldCapture = _micCapture;
        var oldStream = _micStream;
        try { oldCapture?.StopRecording(); } catch (Exception) { }

        _micCapture = capture;
        _micStream = stream;
        _micDevice = device;
        _micLevel = 0f;
        capture.StartRecording();

        oldCapture?.Dispose();
        if (oldStream != null) await oldStream.DisposeAsync().ConfigureAwait(false);

        Log?.Invoke($"Microphone switched to {device.Name} @ {rate} Hz.");
    }

    /// <summary>Speaker-side counterpart of <see cref="ChangeMicDeviceAsync"/>.</summary>
    public async Task ChangeSpeakerDeviceAsync(AudioDevice device)
    {
        var mmDevice = AudioDevices.GetById(device.Id)
            ?? throw new InvalidOperationException($"Speaker '{device.Name}' is unavailable.");

        var capture = new WasapiLoopbackCapture(mmDevice);
        var rate = capture.WaveFormat.SampleRate;
        var stream = new AssemblyAiStream(_apiKey, rate, "speaker");
        stream.FinalTurn += (text, label, s, e) => WriteLine("OTHER", label, text, s, e);
        stream.Error += m => Log?.Invoke(m);
        await stream.StartAsync().ConfigureAwait(false);
        capture.DataAvailable += (_, e) => Forward(e, capture.WaveFormat, stream, ref _speakerLevel);

        var oldCapture = _loopbackCapture;
        var oldStream = _speakerStream;
        try { oldCapture?.StopRecording(); } catch (Exception) { }

        _loopbackCapture = capture;
        _speakerStream = stream;
        _speakerDevice = device;
        _speakerLevel = 0f;
        capture.StartRecording();

        oldCapture?.Dispose();
        if (oldStream != null) await oldStream.DisposeAsync().ConfigureAwait(false);

        Log?.Invoke($"Speaker switched to {device.Name} @ {rate} Hz.");
    }

    /// <summary>
    /// Convert a capture buffer to 16-bit mono PCM and hand it to the stream.
    /// WASAPI hands us 32-bit float, usually stereo; AssemblyAI wants mono
    /// PCM16, so take the first channel and scale.
    /// </summary>
    private void Forward(WaveInEventArgs e, WaveFormat format, AssemblyAiStream? stream, ref float level)
    {
        if (stream == null || e.BytesRecorded == 0) return;

        var channels = Math.Max(1, format.Channels);
        byte[] mono;
        int monoBytes;
        double sumSquares = 0;
        int sampleCount = 0;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var frames = e.BytesRecorded / (4 * channels);
            mono = new byte[frames * 2];
            monoBytes = mono.Length;
            for (var i = 0; i < frames; i++)
            {
                var sample = BitConverter.ToSingle(e.Buffer, (i * channels) * 4);
                sample = Math.Clamp(sample, -1f, 1f);
                var pcm = (short)(sample * short.MaxValue);
                mono[i * 2] = (byte)(pcm & 0xFF);
                mono[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
                sumSquares += sample * sample;
                sampleCount++;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            var frames = e.BytesRecorded / (2 * channels);
            mono = new byte[frames * 2];
            monoBytes = mono.Length;
            for (var i = 0; i < frames; i++)
            {
                var pcm = BitConverter.ToInt16(e.Buffer, (i * channels) * 2);
                mono[i * 2] = (byte)(pcm & 0xFF);
                mono[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
                var f = pcm / (float)short.MaxValue;
                sumSquares += f * f;
                sampleCount++;
            }
        }
        else
        {
            return;   // unexpected format; nothing sensible to send
        }

        level = sampleCount > 0 ? (float)Math.Sqrt(sumSquares / sampleCount) : 0f;
        stream.Feed(mono, monoBytes);
    }

    private void WriteLine(string speaker, string? label, string text, int startMs, int endMs)
    {
        var display = speaker == "OTHER" && !string.IsNullOrEmpty(label) && label != "PENDING"
            ? $"[{label}] {text}"
            : text;
        TranscriptLine?.Invoke(speaker, label, display);

        var entry = new
        {
            timestamp = DateTime.Now.ToString("o"),
            speaker = $"[{speaker}]",
            speaker_label = label,
            start_time = Math.Round(startMs / 1000.0, 2),
            end_time = Math.Round(endMs / 1000.0, 2),
            text,
        };
        var json = JsonSerializer.Serialize(entry);

        // Turns arrive on network threads, so hold the lock across the write:
        // a rollover may be swapping the writer underneath us.
        lock (_writerLock)
        {
            _writer?.WriteLine(json);
            _writer?.Flush();
        }
    }

    private void StartNewSessionFile(string? title, bool announce)
    {
        var root = MeetingStore.EnsureRoot();
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var folder = Path.Combine(root, $"recording_{stamp}");
        // Two rollovers inside one second would otherwise collide.
        var suffix = 1;
        while (Directory.Exists(folder))
        {
            suffix++;
            folder = Path.Combine(root, $"recording_{stamp}_{suffix}");
        }
        Directory.CreateDirectory(folder);

        var writer = new StreamWriter(Path.Combine(folder, MeetingStore.TranscriptFileName), append: false);
        var metadata = new
        {
            session_start = DateTime.Now.ToString("o"),
            session_id = Path.GetFileName(folder)["recording_".Length..],
            type = "session_metadata",
            meeting_title = title,
            engine = "assemblyai",
        };
        writer.WriteLine(JsonSerializer.Serialize(metadata));
        writer.Flush();

        StreamWriter? previous;
        string previousFolder;
        lock (_writerLock)
        {
            previous = _writer;
            previousFolder = _folder;
            _writer = writer;
            _folder = folder;
        }
        previous?.Dispose();

        // A rollover doesn't mean anyone left - they're still on the same
        // call - so close each present attendee's segment in the old folder
        // and immediately reopen it in the new one, rather than waiting for
        // the next poll to notice they're "still" there.
        if (announce && previousFolder.Length > 0)
        {
            var now = DateTime.Now;
            lock (_attendeeLock)
            {
                foreach (var name in _presentAttendees.Keys.ToList())
                {
                    TryAppendAttendeeEvent(previousFolder, name, joined: false, now);
                    _presentAttendees[name] = now;
                    TryAppendAttendeeEvent(folder, name, joined: true, now);
                }
                _candidateStreaks.Clear();
                _absentStreaks.Clear();
            }
        }

        if (announce)
        {
            Log?.Invoke($"Meeting changed -> {title}; now recording into {Path.GetFileName(folder)}");
            NewSession?.Invoke(folder, title ?? "Untitled meeting");
        }
    }

    /// <summary>
    /// Polls Teams' participant roster and appends join/leave sightings to
    /// the current meeting folder's attendee log. A name must survive
    /// AttendeeStablePolls consecutive polls before being recorded as
    /// joined, and an already-present attendee must be missing for that many
    /// consecutive polls before being declared gone - symmetric debouncing,
    /// since a single transient UI Automation read can be not just
    /// incomplete but occasionally a bogus phantom entry.
    /// </summary>
    private async Task WatchAttendeesAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try { await Task.Delay(AttendeePollInterval, cancel).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            List<string> current;
            try { current = TeamsMonitor.GetParticipants(); }
            catch (Exception) { continue; }

            string folder;
            lock (_writerLock) { folder = _folder; }
            if (folder.Length == 0) continue;

            var currentSet = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
            var now = DateTime.Now;

            lock (_attendeeLock)
            {
                foreach (var name in current)
                {
                    _absentStreaks.Remove(name);
                    if (_presentAttendees.ContainsKey(name)) continue;

                    var streak = _candidateStreaks.GetValueOrDefault(name) + 1;
                    if (streak < AttendeeStablePolls)
                    {
                        _candidateStreaks[name] = streak;
                        continue;
                    }
                    _candidateStreaks.Remove(name);
                    _presentAttendees[name] = now;
                    TryAppendAttendeeEvent(folder, name, joined: true, now);
                }

                // Drop candidate streaks for anyone not seen this poll, so a
                // one-off sighting doesn't sit around half-credited toward a
                // much later, unrelated sighting of the same name.
                foreach (var name in _candidateStreaks.Keys.ToList())
                    if (!currentSet.Contains(name)) _candidateStreaks.Remove(name);

                foreach (var name in _presentAttendees.Keys.ToList())
                {
                    if (currentSet.Contains(name)) continue;

                    var streak = _absentStreaks.GetValueOrDefault(name) + 1;
                    if (streak < AttendeeStablePolls)
                    {
                        _absentStreaks[name] = streak;
                        continue;
                    }
                    _absentStreaks.Remove(name);
                    _presentAttendees.Remove(name);
                    TryAppendAttendeeEvent(folder, name, joined: false, now);
                }
            }
        }
    }

    private static void TryAppendAttendeeEvent(string folder, string name, bool joined, DateTime at)
    {
        try { MeetingStore.AppendAttendeeEvent(folder, name, joined, at); }
        catch (Exception) { /* best-effort; a missed event isn't worth disrupting the recording */ }
    }

    /// <summary>
    /// Roll into a fresh recording when the Teams meeting changes, so two
    /// back-to-back meetings do not share one transcript. Empty titles are
    /// ignored: Teams reports nothing mid-transition and when closed.
    /// </summary>
    private async Task WatchMeetingTitleAsync(CancellationToken cancel)
    {
        string? pending = null;
        var pendingCount = 0;

        while (!cancel.IsCancellationRequested)
        {
            try { await Task.Delay(TitlePollInterval, cancel).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            string? title;
            try { title = TeamsMonitor.GetMeetingTitle(); }
            catch (Exception) { continue; }

            if (string.IsNullOrWhiteSpace(title) || title == _currentTitle)
            {
                pending = null;
                pendingCount = 0;
                continue;
            }

            if (title == pending) pendingCount++;
            else { pending = title; pendingCount = 1; }

            if (pendingCount < TitleStablePolls) continue;

            pending = null;
            pendingCount = 0;
            _currentTitle = title;
            try { StartNewSessionFile(title, announce: true); }
            catch (Exception ex) { Log?.Invoke($"Meeting rollover failed: {ex.Message}"); }
        }
    }

    public async Task StopAsync()
    {
        _watcherCts?.Cancel();

        string finalFolder;
        lock (_writerLock) { finalFolder = _folder; }
        lock (_attendeeLock)
        {
            if (finalFolder.Length > 0)
            {
                var now = DateTime.Now;
                foreach (var name in _presentAttendees.Keys)
                    TryAppendAttendeeEvent(finalFolder, name, joined: false, now);
            }
            _presentAttendees.Clear();
            _candidateStreaks.Clear();
            _absentStreaks.Clear();
        }

        try { _micCapture?.StopRecording(); } catch (Exception) { }
        try { _loopbackCapture?.StopRecording(); } catch (Exception) { }

        // Terminate the streams before closing the file: AssemblyAI flushes
        // its final turns (and a late speaker revision) on the way out.
        if (_micStream != null) await _micStream.StopAsync().ConfigureAwait(false);
        if (_speakerStream != null) await _speakerStream.StopAsync().ConfigureAwait(false);

        lock (_writerLock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
        _micLevel = _speakerLevel = 0f;
        Log?.Invoke("Transcribing stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _micCapture?.Dispose();
        _loopbackCapture?.Dispose();
        if (_micStream != null) await _micStream.DisposeAsync().ConfigureAwait(false);
        if (_speakerStream != null) await _speakerStream.DisposeAsync().ConfigureAwait(false);
        _watcherCts?.Dispose();
    }
}
