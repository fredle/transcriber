import pyaudio
import re


def detect_loopback_speakers():
    """
    Detect all speakers that have loopback recording capability enabled.
    Loopback devices allow you to capture audio output from speakers.
    """
    p = pyaudio.PyAudio()

    print("=" * 80)
    print("LOOPBACK AUDIO DEVICE DETECTION")
    print("=" * 80)

    device_count = p.get_device_count()
    loopback_devices = []
    output_devices = []

    # First pass: collect all devices
    for i in range(device_count):
        try:
            device_info = p.get_device_info_by_index(i)
            device_name = str(device_info['name']).lower()
            host_api = p.get_host_api_info_by_index(int(device_info['hostApi']))['name']

            # Detect loopback devices
            # These are input devices that capture speaker output
            is_loopback = any([
                'stereo mix' in device_name,
                'loopback' in device_name,
                'what u hear' in device_name,
                'wave out mix' in device_name,
                ('what you hear' in device_name),
            ])

            # Check if it's an input device with loopback capability
            if int(device_info['maxInputChannels']) > 0 and is_loopback:
                loopback_devices.append({
                    'index': i,
                    'name': device_info['name'],
                    'channels': device_info['maxInputChannels'],
                    'sample_rate': device_info['defaultSampleRate'],
                    'host_api': host_api
                })

            # Collect output devices (speakers/headphones)
            if int(device_info['maxOutputChannels']) > 0:
                output_devices.append({
                    'index': i,
                    'name': device_info['name'],
                    'channels': device_info['maxOutputChannels'],
                    'sample_rate': device_info['defaultSampleRate'],
                    'host_api': host_api
                })
        except Exception as e:
            print(f"Warning: Could not read device {i}: {e}")
            continue

    # Display results
    if loopback_devices:
        print(f"\n[OK] Found {len(loopback_devices)} loopback device(s) for capturing speaker audio:")
        print("=" * 80)

        for device in loopback_devices:
            print(f"\n[Loopback Device {device['index']}]")
            print(f"  Name: {device['name']}")
            print(f"  Channels: {device['channels']}")
            print(f"  Sample Rate: {device['sample_rate']:.0f} Hz")
            print(f"  Host API: {device['host_api']}")

            # Try to match with corresponding speaker
            matched_speakers = find_matching_speakers(device, output_devices)
            if matched_speakers:
                print(f"  Associated Speakers:")
                for speaker in matched_speakers:
                    print(f"    → Device {speaker['index']}: {speaker['name']}")
            else:
                print(f"  Associated Speakers: All system audio")

        print("\n" + "=" * 80)
        print("USAGE INSTRUCTIONS:")
        print("=" * 80)
        print("To capture audio from these loopback devices, use the device index")
        print("shown above when initializing your audio stream.")
        print("\nExample:")
        if loopback_devices:
            example_device = loopback_devices[0]
            print(f"  stream = p.open(")
            print(f"      input_device_index={example_device['index']},")
            print(f"      channels={example_device['channels']},")
            print(f"      rate={int(example_device['sample_rate'])},")
            print(f"      format=pyaudio.paInt16,")
            print(f"      input=True")
            print(f"  )")
    else:
        print("\n[NOT FOUND] No loopback devices found!")
        print("\n" + "=" * 80)
        print("TROUBLESHOOTING:")
        print("=" * 80)
        print("Loopback devices (like 'Stereo Mix') need to be enabled in Windows:")
        print("1. Right-click the speaker icon in the system tray")
        print("2. Select 'Sounds' or 'Sound settings'")
        print("3. Go to the 'Recording' tab")
        print("4. Right-click in the empty space and enable 'Show Disabled Devices'")
        print("5. Find 'Stereo Mix' or similar device")
        print("6. Right-click it and select 'Enable'")
        print("7. Set it as the default recording device (optional)")
        print("\nNote: Not all audio drivers support loopback devices.")

    # Show available speakers for reference
    if output_devices:
        print("\n" + "=" * 80)
        print(f"AVAILABLE SPEAKER/OUTPUT DEVICES ({len(output_devices)} found):")
        print("=" * 80)
        for device in output_devices:
            print(f"\nDevice {device['index']}: {device['name']}")
            print(f"  Channels: {device['channels']}")
            print(f"  Sample Rate: {device['sample_rate']:.0f} Hz")
            print(f"  Host API: {device['host_api']}")

    print("\n" + "=" * 80)
    p.terminate()

    return loopback_devices


def find_matching_speakers(loopback_device, output_devices):
    """
    Try to match a loopback device to its corresponding speaker(s).
    This is heuristic-based since there's no direct API for this.
    """
    loopback_name = loopback_device['name'].lower()
    matched = []

    # Extract brand/device identifiers from loopback name
    # e.g., "Stereo Mix (Realtek Audio)" -> "realtek"
    match = re.search(r'\(([^)]+)\)', loopback_name)
    if match:
        identifier = match.group(1).lower()

        # Find speakers with matching identifier
        for speaker in output_devices:
            speaker_name = speaker['name'].lower()
            if identifier in speaker_name:
                matched.append(speaker)

    # If no specific match, check if it's a generic stereo mix
    if not matched and ('stereo mix' in loopback_name or 'what u hear' in loopback_name):
        # Generic loopback captures all system audio
        return []

    return matched


if __name__ == "__main__":
    loopback_devices = detect_loopback_speakers()

    # Return exit code based on whether loopback devices were found
    import sys
    sys.exit(0 if loopback_devices else 1)
