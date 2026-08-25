using System;
using System.Collections.Generic;
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
}
