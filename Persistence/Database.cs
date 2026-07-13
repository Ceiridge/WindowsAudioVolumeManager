using System.IO;
using Microsoft.Data.Sqlite;

namespace WindowsAudioVolumeManager.Persistence {
	/// <summary>
	/// Owns the SQLite database file location and connection creation.
	/// The database lives in %APPDATA%\WindowsAudioVolumeManager\volumeManager.db
	/// so it is independent of the working directory (the old JSON config was not).
	/// </summary>
	public class Database {
		public static string AppDataDirectory =>
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsAudioVolumeManager");

		public string DatabasePath { get; }
		private readonly string _connectionString;

		public Database(string? databasePath = null) {
			DatabasePath = databasePath ?? Path.Combine(AppDataDirectory, "volumeManager.db");
			Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

			_connectionString = new SqliteConnectionStringBuilder {
				DataSource = DatabasePath,
				Mode = SqliteOpenMode.ReadWriteCreate,
				Pooling = true
			}.ToString();
		}

		public SqliteConnection Open() {
			SqliteConnection connection = new(_connectionString);
			connection.Open();
			return connection;
		}

		/// <summary>Creates/updates the schema. Must be called once at startup.</summary>
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
