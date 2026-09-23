using System;
using System.Threading;
using System.Threading.Tasks;

namespace MeetingTranscriber.Services;

/// <summary>
/// Common surface for a transcription channel, realtime (<see cref="AssemblyAiStream"/>)
/// or pause-segmented batch (<see cref="AssemblyAiBatchStream"/>), so
/// <see cref="RecordingSession"/> can wire either up identically.
/// </summary>
public interface IAudioStream : IAsyncDisposable
{
    /// <summary>Fires once per finalised turn: (text, speakerLabel, startMs, endMs).</summary>
    event Action<string, string?, int, int>? FinalTurn;

    /// <summary>Fires on transport or API errors, for surfacing in the log.</summary>
    event Action<string>? Error;

    Task StartAsync(CancellationToken cancel = default);

    /// <summary>Queue raw 16-bit mono PCM captured from the device.</summary>
    void Feed(byte[] pcm, int count);

    Task StopAsync();
}
