using System.IO;

namespace MeetingTranscriber.Services;

/// <summary>Wraps raw 16-bit mono PCM in a canonical WAV header so it can be uploaded as a self-describing file.</summary>
public static class WavEncoder
{
    public static byte[] Encode(byte[] pcm, int sampleRate)
    {
        const int bitsPerSample = 16;
        const int channels = 1;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);
        var dataSize = pcm.Length;

        using var stream = new MemoryStream(44 + dataSize);
        using var w = new BinaryWriter(stream);

        w.Write("RIFF"u8);
        w.Write(36 + dataSize);
        w.Write("WAVE"u8);

        w.Write("fmt "u8);
        w.Write(16);                    // PCM fmt chunk size
        w.Write((short)1);              // PCM format tag
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write((short)bitsPerSample);

        w.Write("data"u8);
        w.Write(dataSize);
        w.Write(pcm);

        return stream.ToArray();
    }
}
