using System.Text.Json;
using Microsoft.Data.Sqlite;
using WindowsAudioVolumeManager.Audio;
using WindowsAudioVolumeManager.Persistence;

internal static class Program {
	private static readonly List<(string Name, Action Run)> Cases = [];
	private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "WavmDeviceVolumesTests", Guid.NewGuid().ToString("N"));
	private static readonly VolumeKey Vaio = new("endpoint-vaio", "Discord.exe");
	private static readonly VolumeKey Aux = new("endpoint-aux", "Discord.exe");

	private static int Main(string[] args) {
		Directory.CreateDirectory(TestRoot);
		RegisterPolicyTests();
		RegisterGateTests();
		RegisterSelectionTests();
		RegisterPersistenceTests();
		List<object> results = [];
		int failed = 0;
		foreach (var test in Cases) {
			try {
				test.Run();
				Console.WriteLine("PASS " + test.Name);
				results.Add(new { name = test.Name, passed = true, error = (string?)null });
			} catch (Exception e) {
				failed++;
				Console.WriteLine("FAIL " + test.Name + ": " + e.Message);
				results.Add(new { name = test.Name, passed = false, error = e.ToString() });
			}
		}
		SqliteConnection.ClearAllPools();
		string report = JsonSerializer.Serialize(new { tests = Cases.Count, passed = Cases.Count - failed, failed,
			audioHardwareTested = false, results }, new JsonSerializerOptions { WriteIndented = true });
		int reportIndex = Array.IndexOf(args, "--report");
		if (reportIndex >= 0 && reportIndex + 1 < args.Length) {
			string path = Path.GetFullPath(args[reportIndex + 1]);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, report);
		}
		Console.WriteLine($"{Cases.Count - failed}/{Cases.Count} passed. These tests do not exercise WASAPI, WPF or audio hardware.");
		return failed == 0 ? 0 : 1;
	}

	private static void Test(string name, Action run) => Cases.Add((name, run));
	private static void Check(bool condition, string message = "Assertion failed") { if (!condition) { throw new Exception(message); } }
	private static void Near(float actual, float expected) => Check(Math.Abs(actual - expected) < 0.0001f, $"Expected {expected}, got {actual}");
	private static void Throws<T>(Action action) where T : Exception {
		try { action(); } catch (T) { return; }
		throw new Exception("Expected " + typeof(T).Name);
	}
	private static VolumePolicy Policy() => new() { DefaultVolumeScalar = .05f };
	private static DeviceVolumeRule Rule(VolumeKey key, float volume, bool maintain = false) => new(key, "Same display name", volume, maintain);

	private static void RegisterPolicyTests() {
		Test("unknown applications use configured default", () => Near(Policy().Resolve(Vaio).ScalarVolume, .05f));
		Test("legacy global setting is inherited", () => { var p = Policy(); p.SetSaved("Discord.exe", .06f); Near(p.Resolve(Vaio).ScalarVolume, .06f); });
		Test("endpoint overrides outrank global settings", () => { var p = Policy(); p.SetSaved("Discord.exe", .06f); p.SetDevice(Rule(Aux, .6f)); Near(p.Resolve(Aux).ScalarVolume, .6f); Near(p.GetDesiredVolume("Discord.exe"), .06f); });
		Test("VAIO and AUX remain independent", () => { var p = Policy(); p.SetDevice(Rule(Vaio, .05f)); p.SetDevice(Rule(Aux, .6f)); p.SetDevice(Rule(Vaio, .02f)); Near(p.Resolve(Aux).ScalarVolume, .6f); });
		Test("same output display name does not merge identities", () => { var p = Policy(); p.SetDevice(Rule(Vaio, .05f)); p.SetDevice(Rule(Aux, .8f)); Check(p.GetAllDevices().Count == 2); });
		Test("removing one override restores its inherited setting", () => { var p = Policy(); p.SetSaved("Discord.exe", .07f); p.SetDevice(Rule(Vaio, .01f)); p.SetDevice(Rule(Aux, .8f)); p.RemoveDevice(Vaio); Near(p.Resolve(Vaio).ScalarVolume, .07f); Near(p.Resolve(Aux).ScalarVolume, .8f); });
		Test("removing global settings preserves overrides", () => { var p = Policy(); p.SetSaved("Discord.exe", .2f); p.SetDevice(Rule(Aux, .8f)); p.RemoveSaved("Discord.exe"); Near(p.Resolve(Vaio).ScalarVolume, .05f); Near(p.Resolve(Aux).ScalarVolume, .8f); });
		Test("legacy resolution ignores endpoint maintenance", () => { var p = Policy(); p.SetDevice(Rule(Vaio, .01f, true)); Check(!p.Resolve(VolumeKey.Global("Discord.exe")).MaintainVolume); });
		Test("maintain requires an explicit endpoint rule", () => { var p = Policy(); p.SetSaved("Discord.exe", .1f); Check(!p.Resolve(Vaio).MaintainVolume); p.SetDevice(Rule(Vaio, .02f, true)); Check(p.Resolve(Vaio).MaintainVolume); Check(!p.Resolve(Aux).MaintainVolume); });
		Test("enabling maintenance preserves the saved target", () => { var p = Policy(); p.SetDevice(Rule(Vaio, .02f)); var updated = p.SetMaintenance(Vaio, true); Check(updated != null && updated.MaintainVolume); Near(p.Resolve(Vaio).ScalarVolume, .02f); p.SetMaintenance(Vaio, false); Near(p.Resolve(Vaio).ScalarVolume, .02f); Check(!p.Resolve(Vaio).MaintainVolume); });
		Test("maintenance does not create an unknown override", () => { var p = Policy(); Check(p.SetMaintenance(Aux, true) == null); Check(p.GetAllDevices().Count == 0); });
		Test("volume values are clamped", () => { var p = Policy(); p.SetSaved("low", -1); p.SetSaved("high", 2); Near(p.GetDesiredVolume("low"), 0); Near(p.GetDesiredVolume("high"), 1); });
		Test("non-finite values are rejected", () => { Throws<ArgumentOutOfRangeException>(() => Policy().DefaultVolumeScalar = float.NaN); Throws<ArgumentOutOfRangeException>(() => Policy().SetDevice(Rule(Aux, float.PositiveInfinity))); });
		Test("global keys cannot enter endpoint repository policy", () => Throws<ArgumentException>(() => Policy().SetDevice(Rule(VolumeKey.Global("Discord.exe"), .1f))));
		Test("serialized write keys resist delimiter collisions", () => Check(new VolumeKey("a|b", "c").WriteKey != new VolumeKey("a", "b|c").WriteKey));
		Test("rule identities remain stable when live instances restart", () => Check(new VolumeKey("endpoint-vaio", "Discord.exe") == Vaio));
		Test("policy snapshots are safe during concurrent updates", () => { var p = Policy(); Parallel.For(0, 500, i => { p.SetDevice(Rule(i % 2 == 0 ? Vaio : Aux, (i % 100) / 100f)); _ = p.GetAllDevices(); _ = p.Resolve(Aux); }); Check(p.GetAllDevices().Count == 2); });
	}

	private static void RegisterGateTests() {
		Test("at-target events do not cause writes", () => Check(!new CorrectionGate().TryCorrect(.05f, .05f, 0)));
		Test("quantization tolerance prevents self-echo loops", () => Check(!new CorrectionGate().TryCorrect(.0501f, .05f, 0)));
		Test("a first dangerous volume increase is corrected immediately", () => { var g = new CorrectionGate(); Check(g.TryCorrect(1, .05f, 0)); Check(!g.Pending); });
		Test("a first quiet deviation is also corrected immediately", () => Check(new CorrectionGate().TryCorrect(.01f, .05f, 0)));
		Test("callbacks from our own writes do not enqueue corrections", () => { var g = new CorrectionGate(); Check(g.TryCorrect(1, .05f, 0)); Check(!g.TryCorrect(.05f, .05f, 1)); Check(!g.Pending); });
		Test("a conflict burst does not permanently suspend Maintain", () => {
			var g = new CorrectionGate(); int writes = 0;
			for (long t = 0; t < 1000; t++) { if (g.TryCorrect(1, .05f, t)) { writes++; } }
			Check(writes == 100); Check(g.Pending); Check(g.RetryAfterMilliseconds(999) == 1);
		});
		Test("deferred correction runs exactly at its deadline", () => {
			var g = new CorrectionGate(); Check(g.TryCorrect(1, .05f, 0));
			Check(!g.TryCorrect(1, .05f, 1)); Check(g.Pending);
			Check(g.RetryAfterMilliseconds(1) == 9);
			Check(!g.TryCorrect(1, .05f, 9));
			Check(g.TryCorrect(1, .05f, 10)); Check(!g.Pending);
		});
		Test("drag release needs no extra callback to restore target", () => {
			var g = new CorrectionGate(); Check(g.TryCorrect(1, .05f, 0));
			Check(!g.TryCorrect(1, .05f, 5));
			Check(g.TryCorrect(1, .05f, 10));
		});
		Test("future events cannot delay an already scheduled correction", () => {
			var g = new CorrectionGate(); Check(g.TryCorrect(1, .05f, 0));
			for (int t = 1; t < 10; t++) { Check(!g.TryCorrect(1, .05f, t)); }
			Check(g.RetryAfterMilliseconds(9) == 1);
		});
		Test("multiple sessions have independent pacing", () => {
			var vaio = new CorrectionGate(); var aux = new CorrectionGate();
			Check(vaio.TryCorrect(1, .05f, 0)); Check(!vaio.TryCorrect(1, .05f, 1));
			Check(aux.TryCorrect(.3f, .6f, 1)); Check(!aux.Pending);
		});
		Test("long slider movement keeps correcting at a bounded rate", () => {
			var g = new CorrectionGate(); int writes = 0;
			for (long t = 0; t <= 4000; t += 2) { if (g.TryCorrect(.8f, .05f, t)) { writes++; } }
			Check(writes == 401); Check(!g.Pending);
		});
		Test("the next independent change after a quiet period is immediate", () => {
			var g = new CorrectionGate(); Check(g.TryCorrect(.9f, .05f, 0)); Check(g.TryCorrect(.9f, .05f, 1000));
		});
		Test("a temporary return to target cancels pending work", () => {
			var g = new CorrectionGate(); Check(g.TryCorrect(1, .05f, 0)); Check(!g.TryCorrect(.9f, .05f, 1));
			Check(!g.TryCorrect(.05f, .05f, 2)); Check(!g.Pending);
		});
		Test("invalid floating point input produces no write", () => {
			var g = new CorrectionGate(); Check(!g.TryCorrect(float.NaN, .05f, 0));
			Check(!g.TryCorrect(1, float.PositiveInfinity, 0));
		});
		Test("a COM write failure schedules a bounded retry", () => {
			var g = new CorrectionGate(); Check(g.TryCorrect(1, .05f, 0));
			g.MarkWriteFailed(0); Check(g.Pending);
			Check(g.RetryAfterMilliseconds(10) == CorrectionGate.FailedWriteRetryMilliseconds - 10);
			Check(!g.TryCorrect(1, .05f, 149)); Check(g.TryCorrect(1, .05f, 150));
		});
		Test("explicit edits reset pacing", () => {
			var g = new CorrectionGate(); Check(g.TryCorrect(1, .05f, 0));
			Check(!g.TryCorrect(1, .05f, 1)); g.Reset(); Check(g.TryCorrect(1, .05f, 1));
		});
		Test("new session has no inherited conflict state", () => {
			var old = new CorrectionGate(); Check(old.TryCorrect(1, .05f, 0)); Check(!old.TryCorrect(1, .05f, 1));
			Check(new CorrectionGate().TryCorrect(1, .05f, 1));
		});
		Test("retries on failed COM writes are not pushed back by new callbacks", () => {
			var g = new CorrectionGate(); Check(g.TryCorrect(1, .05f, 0)); g.MarkWriteFailed(0);
			for (int t = 1; t < 150; t++) { Check(!g.TryCorrect(1, .05f, t)); }
			Check(g.RetryAfterMilliseconds(149) == 1); Check(g.TryCorrect(1, .05f, 150));
		});
		Test("an at-target echo cancels a COM failure retry", () => {
			var g = new CorrectionGate(); Check(g.TryCorrect(1, .05f, 0)); g.MarkWriteFailed(0);
			Check(!g.TryCorrect(.05f, .05f, 1)); Check(!g.Pending);
		});
		Test("reset cancels pending COM failure", () => {
			var g = new CorrectionGate(); Check(g.TryCorrect(1, .05f, 0)); g.MarkWriteFailed(0);
			g.Reset(); Check(!g.Pending); Check(g.RetryAfterMilliseconds(1) == 0);
			Check(g.TryCorrect(1, .05f, 1));
		});
		Test("rapid repeated over-target writes cannot disable maintenance", () => {
			var g = new CorrectionGate();
			for (long t = 0; t < 20000; t += 10) { Check(g.TryCorrect(1, .05f, t)); }
			Check(!g.Pending);
		});
		Test("event storm followed by silence has an outstanding bounded retry", () => {
			var g = new CorrectionGate(); Check(g.TryCorrect(1, .05f, 0));
			for (long t = 1; t < 9; t++) { Check(!g.TryCorrect(1, .05f, t)); }
			Check(g.Pending); Check(g.RetryAfterMilliseconds(8) == 2);
			Check(g.TryCorrect(1, .05f, 10));
		});
	}

	private static void RegisterSelectionTests() {
		HashSet<string> ids = ["VAIO", "AUX"];
		Test("legacy manages only its selected output", () => { Check(OutputSelectionPolicy.ShouldManage("VAIO", false, "VAIO", ids, true, "Other")); Check(!OutputSelectionPolicy.ShouldManage("AUX", false, "VAIO", ids, true, "Other")); });
		Test("device mode does not manage an output merely displayed", () => Check(!OutputSelectionPolicy.ShouldManage("Other", true, "Other", ids, false, "Other")));
		Test("explicit outputs remain managed when default changes", () => Check(OutputSelectionPolicy.ShouldManage("AUX", true, "Other", ids, true, "NewDefault")));
		Test("follow default adds exactly the current default", () => { Check(OutputSelectionPolicy.ShouldManage("Default", true, "Other", ids, true, "Default")); Check(!OutputSelectionPolicy.ShouldManage("Other", true, "Other", ids, true, "Default")); });
		Test("disabled follow keeps unselected defaults untouched", () => Check(!OutputSelectionPolicy.ShouldManage("Default", true, "Default", ids, false, "Default")));
		Test("no checked outputs and no follow means no session writes", () => Check(!OutputSelectionPolicy.ShouldManage("VAIO", true, "VAIO", new HashSet<string>(), false, "VAIO")));
	}

	private static Database NewDatabase() {
		var database = new Database(Path.Combine(TestRoot, Guid.NewGuid().ToString("N"), "test.db"));
		database.Initialize();
		return database;
	}
	private static void Execute(SqliteConnection c, string sql) { using var command = c.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
	private static long Scalar(SqliteConnection c, string sql) { using var command = c.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }

	private static void RegisterPersistenceTests() {
		Test("fresh database migrates to v2", () => { using var c = NewDatabase().Open(); Check(Scalar(c, "PRAGMA user_version") == 2); Check(Scalar(c, "SELECT count(*) FROM sqlite_master WHERE name IN ('saved_sessions','device_saved_sessions','settings')") == 3); });
		Test("same app is independently persisted for two outputs", () => { var db = NewDatabase(); using var c = db.Open(); DeviceVolumeRepository.Upsert(c, Rule(Vaio, .05f, true)); DeviceVolumeRepository.Upsert(c, Rule(Aux, .6f)); var all = new DeviceVolumeRepository(db).GetAll(); Check(all.Count == 2); Near(all.Single(r => r.Rule.Key == Aux).Rule.ScalarVolume, .6f); Check(all.Single(r => r.Rule.Key == Vaio).Rule.MaintainVolume); });
		Test("endpoint deletion does not delete another output or global", () => { var db = NewDatabase(); using var c = db.Open(); SessionVolumeRepository.Upsert(c, "Discord.exe", .1f); DeviceVolumeRepository.Upsert(c, Rule(Vaio, .05f)); DeviceVolumeRepository.Upsert(c, Rule(Aux, .6f)); DeviceVolumeRepository.Delete(c, Vaio); Check(new DeviceVolumeRepository(db).GetAll().Single().Rule.Key == Aux); Check(new SessionVolumeRepository(db).GetAllDetailed().Count == 1); });
		Test("offline updates do not fabricate last-seen timestamps", () => { var db = NewDatabase(); using var c = db.Open(); DeviceVolumeRepository.Upsert(c, Rule(Vaio, .05f)); Check(new DeviceVolumeRepository(db).GetAll().Single().LastSeenAt == null); DeviceVolumeRepository.TouchLastSeen(c, Vaio); Check(new DeviceVolumeRepository(db).GetAll().Single().LastSeenAt != null); });
		Test("upsert updates volume and maintain flag without duplicating", () => { var db = NewDatabase(); using var c = db.Open(); DeviceVolumeRepository.Upsert(c, Rule(Vaio, .05f)); DeviceVolumeRepository.Upsert(c, Rule(Vaio, .02f, true)); var r = new DeviceVolumeRepository(db).GetAll().Single().Rule; Near(r.ScalarVolume, .02f); Check(r.MaintainVolume); });
		Test("v1 migration preserves globals and produces a valid backup", () => {
			var db = new Database(Path.Combine(TestRoot, Guid.NewGuid().ToString("N"), "legacy.db"));
			using (var c = db.Open()) {
				Execute(c, "PRAGMA journal_mode=WAL; CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL); CREATE TABLE saved_sessions(name TEXT PRIMARY KEY, scalar_volume REAL NOT NULL, updated_at TEXT NOT NULL, last_seen_at TEXT); INSERT INTO saved_sessions VALUES('Discord.exe',0.06,'2026-01-01',NULL); PRAGMA user_version=1;");
			}
			db.Initialize();
			Near(new SessionVolumeRepository(db).GetAllDetailed().Single().ScalarVolume, .06f);
			string backup = Directory.GetFiles(Path.GetDirectoryName(db.DatabasePath)!, "*.before-device-volumes-*.db").Single();
			using var bc = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly }.ToString());
			bc.Open(); Check(Scalar(bc, "PRAGMA user_version") == 1); Check(Scalar(bc, "SELECT count(*) FROM saved_sessions") == 1);
		});
		Test("migration is idempotent", () => { var db = NewDatabase(); db.Initialize(); using var c = db.Open(); Check(Scalar(c, "PRAGMA user_version") == 2); Check(Directory.GetFiles(Path.GetDirectoryName(db.DatabasePath)!, "*.before-device-volumes-*.db").Length == 0); });
		Test("newer schema fails rather than silently downgrading", () => { var db = NewDatabase(); using var c = db.Open(); Execute(c, "PRAGMA user_version=99;"); Throws<InvalidOperationException>(() => SchemaMigrator.Migrate(c)); });
		Test("SQL constraints reject invalid persisted maintenance flag", () => { using var c = NewDatabase().Open(); Throws<SqliteException>(() => Execute(c, "INSERT INTO device_saved_sessions(device_id,name,device_name,scalar_volume,keep_volume) VALUES('x','app','x',0.1,2)")); });
		Test("SQL constraints reject an empty endpoint ID", () => { using var c = NewDatabase().Open(); Throws<SqliteException>(() => Execute(c, "INSERT INTO device_saved_sessions(device_id,name,device_name,scalar_volume) VALUES('','app','x',0.1)")); });
		Test("device rule transactions roll back atomically", () => { var db = NewDatabase(); using var c = db.Open(); using (var t = c.BeginTransaction()) { DeviceVolumeRepository.Upsert(c, Rule(Vaio, .01f), t); t.Rollback(); } Check(new DeviceVolumeRepository(db).GetAll().Count == 0); });
		Test("debounced writes for different outputs do not collide", () => { var db = NewDatabase(); using (var writer = new DebouncedWriter(db)) { writer.Schedule(Vaio.WriteKey, (c, t) => DeviceVolumeRepository.Upsert(c, Rule(Vaio, .01f), t)); writer.Schedule(Aux.WriteKey, (c, t) => DeviceVolumeRepository.Upsert(c, Rule(Aux, .6f), t)); } Check(new DeviceVolumeRepository(db).GetAll().Count == 2); });
		Test("debounce persists the latest value of one rule", () => { var db = NewDatabase(); using (var writer = new DebouncedWriter(db)) { writer.Schedule(Vaio.WriteKey, (c, t) => DeviceVolumeRepository.Upsert(c, Rule(Vaio, .05f), t)); writer.Schedule(Vaio.WriteKey, (c, t) => DeviceVolumeRepository.Upsert(c, Rule(Vaio, .01f), t)); } Near(new DeviceVolumeRepository(db).GetAll().Single().Rule.ScalarVolume, .01f); });
	}
}
