"""
Teams Call Detection Script for Windows
Detects if a Microsoft Teams call is currently active using multiple methods.
"""

import psutil
import win32gui
import win32process
import win32api
import win32con
import time
from typing import Optional, Dict, List
import re


class TeamsCallDetector:
    """Detects if Microsoft Teams is in an active call."""
    
    # Window title patterns that indicate an active call
    CALL_PATTERNS = [
        r"Meeting in progress",
        r"\| Microsoft Teams",
        r"Call in progress",
        r"Teams Meeting",
        r"Microsoft Teams Call"
    ]
    
    # Process names for Teams
    TEAMS_PROCESS_NAMES = [
        "ms-teams.exe",      # New Teams
        "Teams.exe",         # Classic Teams
        "msteams.exe"        # Alternative name
    ]
    
    def __init__(self):
        self.teams_windows = []
        
    def get_teams_processes(self) -> List[psutil.Process]:
        """Get all running Teams processes."""
        teams_processes = []
        for proc in psutil.process_iter(['name', 'pid', 'cpu_percent']):
            try:
                if proc.info['name'] in self.TEAMS_PROCESS_NAMES:
                    teams_processes.append(proc)
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                continue
        return teams_processes
    
    def _enum_windows_callback(self, hwnd, results):
        """Callback for enumerating windows."""
        if win32gui.IsWindowVisible(hwnd):
            window_title = win32gui.GetWindowText(hwnd)
            if window_title:
                _, pid = win32process.GetWindowThreadProcessId(hwnd)
                results.append({
                    'hwnd': hwnd,
                    'title': window_title,
                    'pid': pid
                })
    
    def get_all_windows(self) -> List[Dict]:
        """Get all visible windows with their titles and PIDs."""
        windows = []
        win32gui.EnumWindows(self._enum_windows_callback, windows)
        return windows
    
    def find_teams_windows(self) -> List[Dict]:
        """Find all Teams windows."""
        all_windows = self.get_all_windows()
        teams_pids = [proc.pid for proc in self.get_teams_processes()]
        
        teams_windows = []
        for window in all_windows:
            # Check if window belongs to Teams process
            if window['pid'] in teams_pids:
                teams_windows.append(window)
            # Also check if title contains "Teams"
            elif 'teams' in window['title'].lower():
                teams_windows.append(window)
        
        return teams_windows
    
    def check_window_titles_for_call(self) -> Optional[Dict]:
        """Check if any Teams window title indicates an active call."""
        teams_windows = self.find_teams_windows()
        
        for window in teams_windows:
            title = window['title']
            # Check against call patterns
            for pattern in self.CALL_PATTERNS:
                if re.search(pattern, title, re.IGNORECASE):
                    return {
                        'method': 'window_title',
                        'detected': True,
                        'window_title': title,
                        'pattern_matched': pattern
                    }
        
        return None
    
    def check_teams_audio_activity(self) -> bool:
        """
        Check if Teams processes are consuming significant CPU,
        which might indicate an active call.
        """
        teams_processes = self.get_teams_processes()
        
        if not teams_processes:
            return False
        
        # Sample CPU usage
        for proc in teams_processes:
            try:
                # Get CPU percent over a short interval
                cpu_percent = proc.cpu_percent(interval=0.1)
                # If Teams is using more than 5% CPU, might be in a call
                if cpu_percent > 5:
                    return True
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                continue
        
        return False
    
    def is_teams_running(self) -> bool:
        """Check if Teams is running."""
        return len(self.get_teams_processes()) > 0
    
    def detect_call(self) -> Dict:
        """
        Main method to detect if a Teams call is active.
        Returns a dictionary with detection results.
        """
        result = {
            'call_active': False,
            'teams_running': False,
            'detection_methods': [],
            'details': {}
        }
        
        # Check if Teams is running
        teams_running = self.is_teams_running()
        result['teams_running'] = teams_running
        
        if not teams_running:
            result['details']['message'] = "Microsoft Teams is not running"
            return result
        
        # Method 1: Check window titles
        window_detection = self.check_window_titles_for_call()
        if window_detection:
            result['call_active'] = True
            result['detection_methods'].append('window_title')
            result['details']['window_detection'] = window_detection
        
        # Method 2: Check CPU activity (supplementary)
        audio_active = self.check_teams_audio_activity()
        if audio_active:
            result['detection_methods'].append('cpu_activity')
            result['details']['high_cpu_detected'] = True
            # Only set call_active if not already detected by window title
            if not result['call_active']:
                result['call_active'] = True
        
        return result


def main():
    """Main function to demonstrate Teams call detection."""
    print("=" * 60)
    print("Microsoft Teams Call Detection")
    print("=" * 60)
    print()
    
    detector = TeamsCallDetector()
    
    print("Checking for active Teams calls...")
    print("Press Ctrl+C to stop monitoring\n")
    
    try:
        while True:
            result = detector.detect_call()
            
            timestamp = time.strftime("%Y-%m-%d %H:%M:%S")
            print(f"[{timestamp}]", end=" ")
            
            if not result['teams_running']:
                print("❌ Teams is not running")
            elif result['call_active']:
                print("📞 CALL ACTIVE!")
                if 'window_detection' in result['details']:
                    window_info = result['details']['window_detection']
                    print(f"   Window: {window_info['window_title']}")
                    print(f"   Pattern: {window_info['pattern_matched']}")
                if result['details'].get('high_cpu_detected'):
                    print(f"   High CPU activity detected")
            else:
                print("✓ Teams running, no call detected")
            
            print(f"   Detection methods used: {', '.join(result['detection_methods']) if result['detection_methods'] else 'none'}")
            print()
            
            # Check every 5 seconds
            time.sleep(5)
            
    except KeyboardInterrupt:
        print("\n\nMonitoring stopped.")


if __name__ == "__main__":
    main()
