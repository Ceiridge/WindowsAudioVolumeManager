using Microsoft.Data.Sqlite;
using WindowsAudioVolumeManager.Diagnostics;

namespace WindowsAudioVolumeManager.Persistence {
	/// <summary>
	/// Coalesces frequent writes (e.g. slider drags) and commits them in a single
	/// transaction 500 ms after the last change. Writes scheduled under the same key
	/// replace each other, so only the latest value is persisted.
	/// Replaces the old unsynchronized Dirty-flag + 1 s polling thread.
	/// </summary>
	public class DebouncedWriter : IDisposable {
		private const int DebounceMilliseconds = 500;

		private readonly Database _database;
		private readonly object _lock = new();
		private readonly Dictionary<string, Action<SqliteConnection, SqliteTransaction>> _pending = new();
		private readonly Timer _timer;
		private bool _disposed;

		public DebouncedWriter(Database database) {
			_database = database;
			_timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
		}

		public void Schedule(string key, Action<SqliteConnection, SqliteTransaction> write) {
			lock (_lock) {
				if (_disposed) {
					return;
				}

				_pending[key] = write;
				_timer.Change(DebounceMilliseconds, Timeout.Infinite);
			}
		}

		public void Flush() {
			Action<SqliteConnection, SqliteTransaction>[] writes;

			lock (_lock) {
				if (_pending.Count == 0) {
					return;
				}

				writes = [.. _pending.Values];
				_pending.Clear();
			}

			try {
				using SqliteConnection connection = _database.Open();
				using SqliteTransaction transaction = connection.BeginTransaction();

				foreach (Action<SqliteConnection, SqliteTransaction> write in writes) {
					write(connection, transaction);
				}

				transaction.Commit();
			} catch (Exception e) {
				Logger.Error("Failed to persist pending changes", e);
			}
		}

		public void Dispose() {
			lock (_lock) {
				if (_disposed) {
					return;
				}
				_disposed = true;
			}

			_timer.Dispose();
			Flush();
			GC.SuppressFinalize(this);
		}
	}
}
