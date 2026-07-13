using System.IO;

namespace WindowsAudioVolumeManager.Diagnostics {
	/// <summary>
	/// Minimal thread-safe file logger with size-based rotation.
	/// Logs to %APPDATA%\WindowsAudioVolumeManager\log.txt.
	/// </summary>
	public static class Logger {
		private const long MaxLogSizeBytes = 1024 * 1024; // 1 MiB, then rotated to log.old.txt

		private static readonly object Lock = new();
		private static string? _logFilePath;

		public static void Initialize(string directory) {
			lock (Lock) {
				Directory.CreateDirectory(directory);
				_logFilePath = Path.Combine(directory, "log.txt");
			}
		}

		public static void Info(string message) => Write("INFO", message);
		public static void Warn(string message) => Write("WARN", message);

		public static void Error(string message, Exception? exception = null) =>
			Write("ERROR", exception == null ? message : $"{message}{Environment.NewLine}{exception}");

		private static void Write(string level, string message) {
			string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";

			lock (Lock) {
				if (_logFilePath == null) {
					Console.WriteLine(line);
					return;
				}

				try {
					RotateIfNeeded();
					File.AppendAllText(_logFilePath, line + Environment.NewLine);
				} catch {
					// Logging must never take the app down.
				}
			}
		}

		private static void RotateIfNeeded() {
			if (_logFilePath == null) {
				return;
			}

			FileInfo info = new(_logFilePath);
			if (info.Exists && info.Length > MaxLogSizeBytes) {
				string oldPath = Path.ChangeExtension(_logFilePath, ".old.txt");
				File.Delete(oldPath);
				File.Move(_logFilePath, oldPath);
			}
		}
	}
}
