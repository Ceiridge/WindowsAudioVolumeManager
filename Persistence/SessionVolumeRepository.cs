using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WindowsAudioVolumeManager.Persistence {
	public record SavedSessionRecord(string Name, float ScalarVolume, DateTime? LastSeenAt);

	/// <summary>Saved per-application volumes (saved_sessions table).</summary>
	public class SessionVolumeRepository {
		private readonly Database _database;

		public SessionVolumeRepository(Database database) {
			_database = database;
		}

		public List<SavedSessionRecord> GetAllDetailed() {
			List<SavedSessionRecord> result = [];

			using SqliteConnection connection = _database.Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT name, scalar_volume, last_seen_at FROM saved_sessions;";

			using SqliteDataReader reader = command.ExecuteReader();
			while (reader.Read()) {
				result.Add(new SavedSessionRecord(reader.GetString(0), (float)reader.GetDouble(1),
					ParseUtcTimestamp(reader.IsDBNull(2) ? null : reader.GetString(2))));
			}

			return result;
		}

		/// <summary>SQLite's datetime('now') stores UTC; convert to local time for display.</summary>
		private static DateTime? ParseUtcTimestamp(string? value) =>
			DateTime.TryParse(value, CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed)
				? parsed.ToLocalTime()
				: null;

		public static void Upsert(SqliteConnection connection, string name, float scalarVolume, SqliteTransaction? transaction = null) {
			using SqliteCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = """
				INSERT INTO saved_sessions (name, scalar_volume, updated_at, last_seen_at)
				VALUES ($name, $volume, datetime('now'), datetime('now'))
				ON CONFLICT(name) DO UPDATE SET
					scalar_volume = excluded.scalar_volume,
					updated_at = excluded.updated_at,
					last_seen_at = excluded.last_seen_at;
				""";
			command.Parameters.AddWithValue("$name", name);
			command.Parameters.AddWithValue("$volume", Math.Clamp(scalarVolume, 0f, 1f));
			command.ExecuteNonQuery();
		}

		public static void Delete(SqliteConnection connection, string name, SqliteTransaction? transaction = null) {
			using SqliteCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = "DELETE FROM saved_sessions WHERE name = $name;";
			command.Parameters.AddWithValue("$name", name);
			command.ExecuteNonQuery();
		}

		public static void TouchLastSeen(SqliteConnection connection, string name, SqliteTransaction? transaction = null) {
			using SqliteCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = "UPDATE saved_sessions SET last_seen_at = datetime('now') WHERE name = $name;";
			command.Parameters.AddWithValue("$name", name);
			command.ExecuteNonQuery();
		}
	}
}
