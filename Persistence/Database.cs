using System.IO;
using Microsoft.Data.Sqlite;

namespace WindowsAudioVolumeManager.Persistence {
	/// <summary>Default location is unchanged. WAVM_DATA_DIR is an explicit isolated preview profile.</summary>
	public class Database {
		public static bool UsesIsolatedProfile => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAVM_DATA_DIR"));
		public static string AppDataDirectory => UsesIsolatedProfile
			? Path.GetFullPath(Environment.GetEnvironmentVariable("WAVM_DATA_DIR")!)
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsAudioVolumeManager");

		public string DatabasePath { get; }
		private readonly string _connectionString;

		public Database(string? databasePath = null) {
			DatabasePath = databasePath ?? Path.Combine(AppDataDirectory, "volumeManager.db");
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!);
			_connectionString = new SqliteConnectionStringBuilder {
				DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true
			}.ToString();
		}

		public SqliteConnection Open() {
			SqliteConnection connection = new(_connectionString);
			connection.Open();
			return connection;
		}

		public void Initialize() {
			using SqliteConnection connection = Open();
			using (SqliteCommand pragma = connection.CreateCommand()) {
				pragma.CommandText = "PRAGMA journal_mode=WAL;";
				pragma.ExecuteNonQuery();
			}
			SchemaMigrator.Migrate(connection);
		}
	}
}
