using Microsoft.Win32;
using WindowsAudioVolumeManager.Diagnostics;

namespace WindowsAudioVolumeManager.Interop {
	/// <summary>Manages the HKCU Run registry entry for starting with Windows.</summary>
	public static class AutostartManager {
		private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
		private const string ValueName = "WindowsAudioVolumeManager";

		public static bool IsEnabled() {
			try {
				using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
				return key?.GetValue(ValueName) != null;
			} catch (Exception e) {
				Logger.Error("Could not read autostart registry entry", e);
				return false;
			}
		}

		public static bool SetEnabled(bool enabled) {
			try {
				using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

				if (enabled) {
					string? exePath = Environment.ProcessPath;
					if (exePath == null) {
						Logger.Error("Could not determine executable path for autostart");
						return false;
					}
					key.SetValue(ValueName, $"\"{exePath}\"");
				} else {
					key.DeleteValue(ValueName, throwOnMissingValue: false);
				}

				return true;
			} catch (Exception e) {
				Logger.Error("Could not update autostart registry entry", e);
				return false;
			}
		}
	}
}
