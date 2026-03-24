import pyaudio

def list_audio_devices():
    """List all available audio input and output devices."""
    p = pyaudio.PyAudio()
    
    print("=" * 80)
    print("AVAILABLE AUDIO DEVICES")
    print("=" * 80)
    
    # Get device count
    device_count = p.get_device_count()
    
    # Lists to separate input and output devices
    input_devices = []
    output_devices = []
    loopback_devices = []
    
    # Iterate through all devices
    for i in range(device_count):
        device_info = p.get_device_info_by_index(i)
        device_name = device_info['name'].lower()
        host_api = p.get_host_api_info_by_index(device_info['hostApi'])['name']
        
        # Check if it's a loopback device (devices that capture speaker output)
        is_loopback = 'stereo mix' in device_name or 'loopback' in device_name or (
            'pc speaker' in device_name and device_info['maxInputChannels'] > 0
        )
        
        # Categorize devices
        # Input devices: only show Windows WASAPI microphones (not loopback)
        if device_info['maxInputChannels'] > 0:
            if is_loopback:
                loopback_devices.append((i, device_info))
            elif host_api == 'Windows WASAPI':
                input_devices.append((i, device_info, is_loopback))
        
        # Output devices: only show Windows WASAPI to match Windows settings
        if device_info['maxOutputChannels'] > 0 and host_api == 'Windows WASAPI':
            output_devices.append((i, device_info))
    
    # Print input devices (microphones)
    print("\n" + "=" * 80)
    print("INPUT DEVICES (MICROPHONES)")
    print("=" * 80)
    for idx, device_info, is_loopback in input_devices:
        loopback_label = " [LOOPBACK]" if is_loopback else ""
        print(f"\nDevice {idx}: {device_info['name']}{loopback_label}")
        print(f"  Max Input Channels: {device_info['maxInputChannels']}")
        print(f"  Max Output Channels: {device_info['maxOutputChannels']}")
        print(f"  Default Sample Rate: {device_info['defaultSampleRate']:.0f} Hz")
        print(f"  Host API: {p.get_host_api_info_by_index(device_info['hostApi'])['name']}")
    
    # Print output devices (speakers)
    print("\n" + "=" * 80)
    print("OUTPUT DEVICES (SPEAKERS)")
    print("=" * 80)
    for idx, device_info in output_devices:
        print(f"\nDevice {idx}: {device_info['name']}")
        print(f"  Max Output Channels: {device_info['maxOutputChannels']}")
        print(f"  Default Sample Rate: {device_info['defaultSampleRate']:.0f} Hz")
        print(f"  Host API: {p.get_host_api_info_by_index(device_info['hostApi'])['name']}")
    
    # Print loopback devices (for capturing speaker output)
    if loopback_devices:
        print("\n" + "=" * 80)
        print("LOOPBACK DEVICES (Capture Speaker Output)")
        print("=" * 80)
        for idx, device_info in loopback_devices:
            print(f"\nDevice {idx}: {device_info['name']} [LOOPBACK]")
            print(f"  Max Input Channels: {device_info['maxInputChannels']}")
            print(f"  Default Sample Rate: {device_info['defaultSampleRate']:.0f} Hz")
            print(f"  Host API: {p.get_host_api_info_by_index(device_info['hostApi'])['name']}")
    
    # Print default devices
    print("\n" + "=" * 80)
    print("DEFAULT DEVICES")
    print("=" * 80)
    try:
        default_input = p.get_default_input_device_info()
        print(f"\nDefault Input Device: {default_input['index']} - {default_input['name']}")
    except OSError:
        print("\nNo default input device found")
    
    try:
        default_output = p.get_default_output_device_info()
        print(f"Default Output Device: {default_output['index']} - {default_output['name']}")
    except OSError:
        print("No default output device found")
    
    print("\n" + "=" * 80)
    
    p.terminate()

if __name__ == "__main__":
    list_audio_devices()
