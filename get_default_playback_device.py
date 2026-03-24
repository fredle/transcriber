import sounddevice as sd

LOOPBACK_PATTERNS = ['stereo mix', 'loopback', 'what u hear', 'wave out mix', 'what you hear']


def get_default_playback_device():
    """
    Get the default audio playback device and the loopback input device
    associated with it (e.g. Stereo Mix).
    """
    default_idx = sd.default.device[1]
    default_info = sd.query_devices(default_idx)
    hostapis = sd.query_hostapis()
    default_api_name = hostapis[default_info['hostapi']]['name']

    # Find the loopback input device (Stereo Mix, etc.)
    loopback_device_id = None
    loopback_info = None
    devices = sd.query_devices()
    for i, dev in enumerate(devices):
        name_lower = dev['name'].lower()
        if dev['max_input_channels'] > 0 and any(p in name_lower for p in LOOPBACK_PATTERNS):
            loopback_device_id = i
            loopback_info = dev
            break

    return {
        'index': default_idx,
        'name': default_info['name'],
        'channels': int(default_info['max_output_channels']),
        'sample_rate': default_info['default_samplerate'],
        'host_api': default_api_name,
        'loopback_device_id': loopback_device_id,
        'loopback_name': loopback_info['name'] if loopback_info else None,
    }


if __name__ == "__main__":
    device = get_default_playback_device()

    if device:
        print(f"Default Playback Device: {device['index']}")
        print(f"Name: {device['name']}")
        print(f"Channels: {device['channels']}")
        print(f"Sample Rate: {device['sample_rate']:.0f} Hz")
        print(f"Host API: {device['host_api']}")
        print()
        if device['loopback_device_id'] is not None:
            print(f"Loopback Device ID: {device['loopback_device_id']}")
            print(f"Loopback Device Name: {device['loopback_name']}")
        else:
            print("No loopback device found")
    else:
        print("No default playback device found")
