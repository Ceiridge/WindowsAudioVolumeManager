using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WindowsAudioVolumeManager.Diagnostics;

namespace WindowsAudioVolumeManager.Persistence {
	/// <summary>
	/// One-time, idempotent migration of the legacy volumeConfig.json into SQLite.
	/// The legacy file was written relative to the working directory, so both the
	/// executable directory and the current working directory are searched
	/// (the executable directory wins if both exist).
	/// </summary>
	public static class JsonConfigMigrator {
		private const string LegacyFileName = "volumeConfig.json";

		public static void MigrateIfNeeded(Database database, SettingsRepository settings) {
			if (settings.GetBool(SettingsRepository.Keys.MigrationDone, false)) {
				return;
			}

			string? legacyPath = FindLegacyConfig();
			if (legacyPath == null) {
				settings.SetBool(SettingsRepository.Keys.MigrationDone, true);
				return;
			}

			LegacyConfig? config;
			try {
				config = JsonSerializer.Deserialize<LegacyConfig>(File.ReadAllText(legacyPath));
			} catch (Exception e) {
				// Corrupt file: leave it untouched for manual inspection and retry next start.
				Logger.Error($"Could not parse legacy config at {legacyPath}; starting with defaults", e);
				return;
			}

			if (config == null) {
				settings.SetBool(SettingsRepository.Keys.MigrationDone, true);
				return;
			}

			using (SqliteConnection connection = database.Open())
			using (SqliteTransaction transaction = connection.BeginTransaction()) {
				if (SettingsRepository.Get(connection, SettingsRepository.Keys.DefaultVolumeScalar) == null) {
					SettingsRepository.Set(connection, SettingsRepository.Keys.DefaultVolumeScalar,
						Math.Clamp(config.DefaultVolumeScalar, 0f, 1f).ToString(System.Globalization.CultureInfo.InvariantCulture),
						transaction);
				}

				int migrated = 0;
				foreach (LegacySession session in config.SavedSessions ?? []) {
					if (string.IsNullOrWhiteSpace(session.Name)) {
						continue;
					}

					// Duplicates: the last entry wins, matching the old first-match-loaded behavior closely enough.
					SessionVolumeRepository.Upsert(connection, session.Name, Math.Clamp(session.ScalarVolume, 0f, 1f), transaction);
					migrated++;
				}

				SettingsRepository.Set(connection, SettingsRepository.Keys.MigrationDone, "1", transaction);
				transaction.Commit();
				Logger.Info($"Migrated {migrated} saved sessions from {legacyPath}");
			}

			TryRename(legacyPath);
		}

		private static string? FindLegacyConfig() {
			string exeDirPath = Path.Combine(AppContext.BaseDirectory, LegacyFileName);
			if (File.Exists(exeDirPath)) {
				return exeDirPath;
			}

			string cwdPath = Path.Combine(Environment.CurrentDirectory, LegacyFileName);
			return File.Exists(cwdPath) ? cwdPath : null;
		}

		private static void TryRename(string path) {
			try {
				string backupPath = path + ".migrated.bak";
				File.Delete(backupPath);
				File.Move(path, backupPath);
			} catch (Exception e) {
				// Not fatal: migration_done prevents a re-import either way.
				Logger.Warn($"Could not rename migrated config {path}: {e.Message}");
			}
		}

		private class LegacyConfig {
			public float DefaultVolumeScalar { get; set; }
			public List<LegacySession>? SavedSessions { get; set; }
		}

		private class LegacySession {
			public string? Name { get; set; }
			public float ScalarVolume { get; set; }
		}
	}
}
