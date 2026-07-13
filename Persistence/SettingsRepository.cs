using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WindowsAudioVolumeManager.Persistence {
	/// <summary>Typed key-value settings stored in the settings table.</summary>
	public class SettingsRepository {
		public static class Keys {
			public const string DefaultVolumeScalar = "default_volume_scalar";
			public const string HotkeyModifiers = "hotkey_modifiers";
			public const string HotkeyKey = "hotkey_key";
			public const string Autostart = "autostart";
			public const string TrayIconEnabled = "tray_icon_enabled";
			public const string StartMinimized = "start_minimized";
			public const string SelectedDeviceId = "selected_device_id";
			public const string FollowDefaultDevice = "follow_default_device";
			public const string MigrationDone = "migration_done";
		}

		private readonly Database _database;

		public SettingsRepository(Database database) {
			_database = database;
		}

		public string? Get(string key) {
			using SqliteConnection connection = _database.Open();
			return Get(connection, key);
		}

		public static string? Get(SqliteConnection connection, string key) {
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT value FROM settings WHERE key = $key;";
			command.Parameters.AddWithValue("$key", key);
			return command.ExecuteScalar() as string;
		}

		public void Set(string key, string value) {
			using SqliteConnection connection = _database.Open();
			Set(connection, key, value);
		}

		public static void Set(SqliteConnection connection, string key, string value, SqliteTransaction? transaction = null) {
			using SqliteCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = """
				INSERT INTO settings (key, value) VALUES ($key, $value)
				ON CONFLICT(key) DO UPDATE SET value = excluded.value;
				""";
			command.Parameters.AddWithValue("$key", key);
			command.Parameters.AddWithValue("$value", value);
			command.ExecuteNonQuery();
		}

		public float GetFloat(string key, float fallback) =>
			float.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;

		public void SetFloat(string key, float value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

		public bool GetBool(string key, bool fallback) =>
			Get(key) switch { "1" => true, "0" => false, _ => fallback };

		public void SetBool(string key, bool value) => Set(key, value ? "1" : "0");

		public uint GetUInt(string key, uint fallback) =>
			uint.TryParse(Get(key), out uint value) ? value : fallback;

		public void SetUInt(string key, uint value) => Set(key, value.ToString(CultureInfo.InvariantCulture));
	}
}
