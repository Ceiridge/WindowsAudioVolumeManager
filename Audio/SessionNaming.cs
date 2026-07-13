using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace WindowsAudioVolumeManager.Audio {
	/// <summary>
	/// Resolves a stable display name for an audio session.
	/// The naming scheme intentionally matches the legacy app (DisplayName if set,
	/// otherwise "&lt;process&gt;.exe", system sounds as "_System Sounds") so that
	/// saved sessions migrated from volumeConfig.json still match.
	/// Unlike the legacy Process.MainModule approach, this never throws for
	/// elevated/protected processes.
	/// </summary>
	public static class SessionNaming {
		public const string SystemSoundsName = "_System Sounds";
		private const string SystemSoundsResource = @"@%SystemRoot%\System32\AudioSrv.Dll,-202";

		public static string Resolve(AudioSessionControl session) {
			try {
				if (session.IsSystemSoundsSession) {
					return SystemSoundsName;
				}
			} catch {
				// fall through to the other strategies
			}

			string? displayName = TryGet(() => session.DisplayName);
			if (!string.IsNullOrWhiteSpace(displayName)) {
				return displayName == SystemSoundsResource ? SystemSoundsName : displayName;
			}

			uint processId = TryGet(() => session.GetProcessID);
			if (processId != 0) {
				try {
					using Process process = Process.GetProcessById((int)processId);
					return process.ProcessName + ".exe";
				} catch {
					// process already exited or otherwise inaccessible
				}
			}

			// Last resort: extract the executable name from the session identifier,
			// e.g. "...\device\...%b{...}|\Device\...\chrome.exe%b{...}"
			string? identifier = TryGet(() => session.GetSessionIdentifier);
			if (!string.IsNullOrEmpty(identifier)) {
				int exeEnd = identifier.LastIndexOf(".exe", StringComparison.OrdinalIgnoreCase);
				if (exeEnd > 0) {
					int start = identifier.LastIndexOfAny(['\\', '/', '|'], exeEnd) + 1;
					return identifier[start..(exeEnd + 4)];
				}
			}

			return "Unknown Session";
		}

		private static T? TryGet<T>(Func<T> getter) {
			try {
				return getter();
			} catch {
				return default;
			}
		}
	}
}
