using Microsoft.Data.Sqlite;
using WindowsAudioVolumeManager.Diagnostics;

namespace WindowsAudioVolumeManager.Persistence {
	/// <summary>Append-only schema migrations. The legacy table remains untouched.</summary>
	public static class SchemaMigrator {
		private static readonly string[] Migrations = [
			// v1: initial schema (unchanged)
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
			""",
			// v2: per-output overrides; no change to saved_sessions or its keys
			"""
			CREATE TABLE device_saved_sessions (
				device_id     TEXT NOT NULL CHECK (length(device_id) > 0),
				name          TEXT NOT NULL CHECK (length(name) > 0),
				device_name   TEXT NOT NULL,
				scalar_volume REAL NOT NULL CHECK (scalar_volume >= 0 AND scalar_volume <= 1),
				keep_volume   INTEGER NOT NULL DEFAULT 0 CHECK (keep_volume IN (0, 1)),
				updated_at    TEXT NOT NULL DEFAULT (datetime('now')),
				last_seen_at  TEXT,
				PRIMARY KEY (device_id, name)
			);
			"""
		];

		public static void Migrate(SqliteConnection connection) {
			long currentVersion = GetUserVersion(connection);
			if (currentVersion > Migrations.Length) {
				throw new InvalidOperationException("This database belongs to a newer application version. Restore a backup or use that version.");
			}
			if (currentVersion > 0 && currentVersion < Migrations.Length) {
				BackupBeforeUpgrade(connection);
			}
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

		private static void BackupBeforeUpgrade(SqliteConnection connection) {
			SqliteConnectionStringBuilder source = new(connection.ConnectionString);
			if (source.DataSource == ":memory:" || source.Mode == SqliteOpenMode.Memory) { return; }
			// SQLite's backup API also includes committed WAL contents; copying just the .db would not.
			string backupPath = source.DataSource + $".before-device-volumes-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.db";
			using SqliteConnection backup = new(new SqliteConnectionStringBuilder {
				DataSource = backupPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
			}.ToString());
			backup.Open();
			connection.BackupDatabase(backup);
			Logger.Info("Created a consistent pre-migration database backup alongside the database.");
		}

		private static long GetUserVersion(SqliteConnection connection) {
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "PRAGMA user_version;";
			return (long)command.ExecuteScalar()!;
		}
	}
}
