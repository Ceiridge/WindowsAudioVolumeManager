using System.Globalization;
using Microsoft.Data.Sqlite;
using WindowsAudioVolumeManager.Audio;

namespace WindowsAudioVolumeManager.Persistence {
	public record SavedDeviceRecord(DeviceVolumeRule Rule, DateTime? LastSeenAt);

	public sealed class DeviceVolumeRepository {
		private readonly Database _database;
		public DeviceVolumeRepository(Database database) => _database = database;

		public List<SavedDeviceRecord> GetAll() {
			using var connection = _database.Open();
			using var command = connection.CreateCommand();
			command.CommandText = "SELECT device_id, name, device_name, scalar_volume, keep_volume, last_seen_at FROM device_saved_sessions;";
			using var reader = command.ExecuteReader();
			List<SavedDeviceRecord> result = [];
			while (reader.Read()) {
				DateTime? seen = !reader.IsDBNull(5) && DateTime.TryParse(reader.GetString(5), CultureInfo.InvariantCulture,
					DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed.ToLocalTime() : null;
				result.Add(new(new(new(reader.GetString(0), reader.GetString(1)), reader.GetString(2),
					(float)reader.GetDouble(3), reader.GetInt64(4) != 0), seen));
			}
			return result;
		}

		public static void Upsert(SqliteConnection connection, DeviceVolumeRule rule, SqliteTransaction? transaction = null) {
			using var command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = """
				INSERT INTO device_saved_sessions (device_id, name, device_name, scalar_volume, keep_volume, updated_at)
				VALUES ($device, $name, $deviceName, $volume, $keep, datetime('now'))
				ON CONFLICT(device_id, name) DO UPDATE SET
					device_name = excluded.device_name, scalar_volume = excluded.scalar_volume,
					keep_volume = excluded.keep_volume, updated_at = excluded.updated_at;
				""";
			command.Parameters.AddWithValue("$device", rule.Key.DeviceId);
			command.Parameters.AddWithValue("$name", rule.Key.Name);
			command.Parameters.AddWithValue("$deviceName", rule.DeviceName);
			command.Parameters.AddWithValue("$volume", VolumePolicy.Clamp(rule.ScalarVolume));
			command.Parameters.AddWithValue("$keep", rule.MaintainVolume ? 1 : 0);
			command.ExecuteNonQuery();
		}

		public static void Delete(SqliteConnection connection, VolumeKey key, SqliteTransaction? transaction = null) =>
			Execute(connection, key, "DELETE FROM device_saved_sessions WHERE device_id = $device AND name = $name;", transaction);

		public static void TouchLastSeen(SqliteConnection connection, VolumeKey key, SqliteTransaction? transaction = null) =>
			Execute(connection, key, "UPDATE device_saved_sessions SET last_seen_at = datetime('now') WHERE device_id = $device AND name = $name;", transaction);

		private static void Execute(SqliteConnection connection, VolumeKey key, string sql, SqliteTransaction? transaction) {
			using var command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = sql;
			command.Parameters.AddWithValue("$device", key.DeviceId);
			command.Parameters.AddWithValue("$name", key.Name);
			command.ExecuteNonQuery();
		}
	}
}
