using Microsoft.Data.Sqlite;
using WindowsAudioVolumeManager.Diagnostics;

namespace WindowsAudioVolumeManager.Persistence {
	/// <summary>
	/// Versioned schema migrations tracked via PRAGMA user_version.
	/// Add new entries to <see cref="Migrations"/> to evolve the schema; never edit old ones.
	/// </summary>
	public static class SchemaMigrator {
		private static readonly string[] Migrations = [
			// v1: initial schema
			"""
			CREATE TABLE settings (
				key   TEXT PRIMARY KEY,
				value TEXT NOT NULL
			);

			CREATE TABLE saved_sessions (
				name          TEXT PRIMARY KEY,
				scalar_volume REAL NOT NULL CHECK (scalar_volume >= 0 AND scalar_volume <= 1),
				updated_at    TEXT NOT NULL DEFAULT (datetime('now')),
				last_seen_at  TEXT
			);
			"""
		];

		public static void Migrate(SqliteConnection connection) {
			long currentVersion = GetUserVersion(connection);

			for (long version = currentVersion; version < Migrations.Length; version++) {
				using SqliteTransaction transaction = connection.BeginTransaction();

				using (SqliteCommand command = connection.CreateCommand()) {
					command.Transaction = transaction;
					command.CommandText = Migrations[version];
					command.ExecuteNonQuery();
				}

				using (SqliteCommand setVersion = connection.CreateCommand()) {
					setVersion.Transaction = transaction;
					setVersion.CommandText = $"PRAGMA user_version = {version + 1};";
					setVersion.ExecuteNonQuery();
				}

				transaction.Commit();
				Logger.Info($"Database migrated to schema version {version + 1}");
			}
		}

		private static long GetUserVersion(SqliteConnection connection) {
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "PRAGMA user_version;";
			return (long)command.ExecuteScalar()!;
		}
	}
}
