using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace MeetingTranscriber.Services;

public sealed record AudioDevice(string Id, string Name, bool IsDefaultComms)
{
    public override string ToString() => Name;
}

/// <summary>
/// Enumerates the capture and render endpoints Windows exposes, which is the
/// same list Teams shows. Render devices are captured via WASAPI loopback.
/// </summary>
public static class AudioDevices
{
    public static List<AudioDevice> GetMicrophones() => Enumerate(DataFlow.Capture);

    public static List<AudioDevice> GetSpeakers() => Enumerate(DataFlow.Render);

    private static List<AudioDevice> Enumerate(DataFlow flow)
    {
        var result = new List<AudioDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            string? commsId = null;
            try
            {
                using var comms = enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
                commsId = comms.ID;
            }
            catch (Exception)
            {
                // No communications default set - not fatal, just no highlight.
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                result.Add(new AudioDevice(device.ID, device.FriendlyName, device.ID == commsId));
                device.Dispose();
            }
        }
        catch (Exception)
        {
            // Leave the list empty; the UI reports "no devices found".
        }
        return result;
    }

    public static MMDevice? GetById(string id)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDevice(id);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns a WASAPI failure into something a user can act on. NAudio
    /// surfaces most capture problems as a bare COMException whose Message
    /// is just an HRESULT (e.g. "Exception from HRESULT: 0x8889000A"), which
    /// is meaningless to see in a dialog - translate the common AUDCLNT_*
    /// codes we're actually likely to hit, and fall back to the raw message
    /// (with the code still visible) for anything else.
    /// </summary>
    public static string DescribeCaptureFailure(Exception ex, string role, string deviceName)
    {
        var reason = (ex as COMException)?.HResult switch
        {
            unchecked((int)0x88890004) => "it was disconnected, or its settings changed",
            unchecked((int)0x8889000A) => "another app currently has exclusive control of it",
            unchecked((int)0x88890008) => "it doesn't support the audio format that was requested",
            unchecked((int)0x88890017) => "the Windows Audio service isn't running",
            _ => null,
        };
        return reason != null
            ? $"Couldn't open the {role} '{deviceName}' - {reason}."
            : $"Couldn't open the {role} '{deviceName}': {ex.Message}";
    }
}
