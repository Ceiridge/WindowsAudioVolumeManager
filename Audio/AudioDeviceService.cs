using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using WindowsAudioVolumeManager.Diagnostics;

namespace WindowsAudioVolumeManager.Audio {
	/// <summary>
	/// Owns Core Audio wrappers on one MTA thread. In device mode the selected output
	/// is a master-volume selection only; managed outputs keep their subscriptions.
	/// No session on an unmanaged output is enumerated or written.
	/// </summary>
	public class AudioDeviceService : IDisposable {
		public event Action<IReadOnlyList<AudioDeviceInfo>, string?, long>? DevicesChanged;
		public event Action<string, float, long>? MasterVolumeChanged;
		public event Action<AudioStateSnapshot>? SessionsChanged;
		public event Action<string>? ErrorOccurred;

		private readonly VolumePolicy _policy;
		private readonly BlockingCollection<Action> _queue = new();
		private readonly ConcurrentDictionary<(string Device, Guid Epoch, string Instance), byte> _pendingVolumes = new();
		private readonly ConcurrentDictionary<(string Device, Guid Epoch), byte> _pendingRefreshes = new();
		private readonly Thread _thread;
		private readonly Dictionary<string, EndpointContext> _contexts = new();
		private readonly HashSet<string> _managedIds = new(StringComparer.Ordinal);
		private MMDeviceEnumerator? _enumerator;
		private DeviceNotificationClient? _notificationClient;
		private Timer? _reconcileTimer;
		private Timer? _maintenanceTimer;
		private int _maintenanceTickQueued;
		private Timer? _uiSnapshotTimer;
		private int _uiSnapshotQueued;
		private long _lastUiSnapshotAt;
		private const int UiSnapshotIntervalMilliseconds = 50;
		private IReadOnlyList<AudioDeviceInfo> _devices = [];
		private string? _selectedId;
		private string? _defaultId;
		private bool _followDefault;
		private bool _deviceScoped;
		private long _revision;
		private long _requestedRevision;
		private int _stopping;
		private int _started;
		public long ConfigurationVersion => Interlocked.Read(ref _requestedRevision);

		private sealed class TrackedSession(AudioSessionControl control, SessionEventsHandler handler, string name) {
			public AudioSessionControl Control { get; } = control;
			public SessionEventsHandler Handler { get; } = handler;
			public string Name { get; } = name;
			public CorrectionGate Gate { get; } = new();
			public long LastFailureReportedAt;
		}

		private sealed class EndpointContext(AudioDeviceService owner, MMDevice device) {
			public MMDevice Device { get; } = device;
			public string Id { get; } = device.ID;
			public string Name { get; } = device.FriendlyName;
			public AudioEndpointVolume? EndpointVolume;
			public AudioSessionManager? Manager;
			public Guid Epoch = Guid.NewGuid();
			public bool Managed;
			public Dictionary<string, TrackedSession> Sessions { get; } = new();

			public void OnCreated(object sender, IAudioSessionControl session) => owner.QueueRefresh(this);
			public void OnMasterVolume(AudioVolumeNotificationData notification) => owner.Post(() => {
				if (owner.IsCurrent(this) && owner._selectedId == Id) {
					owner.PublishMaster(this);
				}
			});
		}

		public AudioDeviceService(VolumePolicy policy) {
			_policy = policy;
			_thread = new Thread(WorkerLoop) { IsBackground = true, Name = "AudioDeviceWorker" };
			_thread.SetApartmentState(ApartmentState.MTA);
		}

		public void Start(string? preferredDeviceId, bool followDefaultDevice) {
			if (Interlocked.Exchange(ref _started, 1) != 0) {
				throw new InvalidOperationException("The audio worker is already started.");
			}
			// One-shot timer: dormant until a correction is actually deferred.
			_maintenanceTimer = new Timer(_ => QueueMaintenanceTick(), null, Timeout.Infinite, Timeout.Infinite);
			// UI snapshots are paced separately: audio corrections are never delayed
			// merely to save WPF work when an external app produces an event storm.
			_uiSnapshotTimer = new Timer(_ => Post(PublishSessions), null, Timeout.Infinite, Timeout.Infinite);
			_thread.Start();
			Post(() => {
				_followDefault = followDefaultDevice;
				_enumerator = new MMDeviceEnumerator();
				_notificationClient = new DeviceNotificationClient(this);
				_enumerator.RegisterEndpointNotificationCallback(_notificationClient);
				ReadDevices();
				_selectedId = !_followDefault && ContainsDevice(preferredDeviceId) ? preferredDeviceId : _defaultId;
				EnsureSelection();
				SyncContexts();
				PublishAll();
			});
			_reconcileTimer = new Timer(_ => Post(() => {
				ReadDevices();
				EnsureSelection();
				SyncContexts();
				foreach (EndpointContext context in _contexts.Values.Where(c => c.Managed).ToList()) {
					Try(() => RefreshSessions(context));
				}
				PublishAll();
			}), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
		}

		public void ConfigureDeviceMode(bool enabled, IEnumerable<string> managedIds) {
			string[] ids = managedIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToArray();
			long revision = Interlocked.Increment(ref _requestedRevision);
			Post(() => {
				bool modeChanged = _deviceScoped != enabled;
				_deviceScoped = enabled;
				_managedIds.Clear();
				_managedIds.UnionWith(ids);
				_revision = revision;
				if (_enumerator == null) {
					return;
				}
				// Switching rule namespaces is explicit. A mere display selection does not do this.
				if (modeChanged) {
					foreach (EndpointContext context in _contexts.Values) {
						StopSessions(context);
					}
				}
				SyncContexts();
				PublishAll();
			});
		}

		public void SelectDevice(string deviceId) {
			long revision = Interlocked.Increment(ref _requestedRevision);
			Post(() => {
				_revision = revision;
				if (ContainsDevice(deviceId)) {
					_selectedId = deviceId;
					SyncContexts();
				}
				PublishAll();
			});
		}

		public void SetFollowDefaultDevice(bool follow) {
			long revision = Interlocked.Increment(ref _requestedRevision);
			Post(() => {
				_revision = revision;
				_followDefault = follow;
				if (_enumerator == null) {
					return;
				}
				ReadDevices();
				if (follow && _defaultId != null) {
					_selectedId = _defaultId;
				}
				EnsureSelection();
				SyncContexts();
				PublishAll();
			});
		}

		public void SetMasterVolume(string deviceId, float scalar) {
			float target = VolumePolicy.Clamp(scalar);
			Post(() => {
				if (_contexts.TryGetValue(deviceId, out var context) && context.EndpointVolume != null) {
					context.EndpointVolume.MasterVolumeLevelScalar = target;
					PublishMaster(context);
				}
			});
		}

		public void SetSessionVolume(string deviceId, string name, float scalar, bool deviceScoped) {
			float target = VolumePolicy.Clamp(scalar);
			Post(() => {
				if (_deviceScoped != deviceScoped || !_contexts.TryGetValue(deviceId, out var context) || !context.Managed) {
					return;
				}
				foreach (TrackedSession session in context.Sessions.Values.Where(s => s.Name == name)) {
					session.Gate.Reset();
					Try(() => session.Control.SimpleAudioVolume.Volume = target);
				}
				PublishSessions();
			});
		}

		/// <summary>Explicit UI edits reset the circuit breaker and apply the latest rule.</summary>
		public void ReapplyRule(VolumeKey key) => Post(() => {
			if (_deviceScoped == key.IsGlobal) {
				return; // queued command from a different mode
			}
			foreach (EndpointContext context in _contexts.Values.Where(c => c.Managed && (key.IsGlobal || c.Id == key.DeviceId))) {
				foreach (TrackedSession session in context.Sessions.Values.Where(s => s.Name == key.Name)) {
					session.Gate.Reset();
					float target = _policy.Resolve(key).ScalarVolume;
					Try(() => session.Control.SimpleAudioVolume.Volume = target);
				}
			}
			SchedulePendingCorrections();
			PublishSessions();
		});

		public void RequestRefresh() => Post(() => {
			ReadDevices();
			EnsureSelection();
			SyncContexts();
			foreach (EndpointContext context in _contexts.Values.Where(c => c.Managed).ToList()) {
				Try(() => RefreshSessions(context));
			}
			PublishAll();
		});

		public void HandleDefaultDeviceChanged(string defaultDeviceId) => Post(() => {
			ReadDevices();
			if (_followDefault && ContainsDevice(defaultDeviceId)) {
				_selectedId = defaultDeviceId;
			}
			EnsureSelection();
			SyncContexts();
			PublishAll();
		});

		public void HandleDeviceTopologyChanged() => RequestRefresh();

		public void HandleSessionVolumeChanged(string deviceId, Guid epoch, string instanceId) {
			var pending = (deviceId, epoch, instanceId);
			if (!_pendingVolumes.TryAdd(pending, 0)) {
				return;
			}
			Post(() => {
				_pendingVolumes.TryRemove(pending, out _);
				if (!TryGetSession(deviceId, epoch, instanceId, out var context, out var session)) {
					return;
				}
				// Ignore callback payloads. They may precede a newer UI write; read live state instead.
				CorrectIfMaintained(context!, session!);
				SchedulePendingCorrections();
				RequestVolumeSnapshot();
			});
		}

		public void HandleSessionExpired(string deviceId, Guid epoch, string instanceId) => Post(() => {
			if (TryGetSession(deviceId, epoch, instanceId, out var context, out _)) {
				RemoveSession(context!, instanceId);
				PublishSessions();
			}
		});

		private bool TryGetSession(string deviceId, Guid epoch, string instanceId,
			out EndpointContext? context, out TrackedSession? session) {
			session = null;
			return _contexts.TryGetValue(deviceId, out context) && context.Managed && context.Epoch == epoch
				&& context.Sessions.TryGetValue(instanceId, out session);
		}

		private bool IsCurrent(EndpointContext context) =>
			_contexts.TryGetValue(context.Id, out var current) && ReferenceEquals(current, context);

		private void QueueRefresh(EndpointContext context) {
			Guid epoch = context.Epoch;
			var key = (context.Id, epoch);
			if (!_pendingRefreshes.TryAdd(key, 0)) {
				return;
			}
			Post(() => {
				_pendingRefreshes.TryRemove(key, out _);
				if (IsCurrent(context) && context.Managed && context.Epoch == epoch) {
					RefreshSessions(context);
					PublishSessions();
				}
			});
		}

		private void ReadDevices() {
			if (_enumerator == null) {
				return;
			}
			try {
				using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
				_defaultId = device.ID;
			} catch {
				_defaultId = null;
			}
			List<AudioDeviceInfo> devices = [];
			foreach (MMDevice device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) {
				using (device) {
					devices.Add(new(device.ID, device.FriendlyName, device.ID == _defaultId));
				}
			}
			_devices = devices;
		}

		private bool ContainsDevice(string? id) => id != null && _devices.Any(d => d.Id == id);
		private void EnsureSelection() {
			if (!ContainsDevice(_selectedId)) {
				_selectedId = ContainsDevice(_defaultId) ? _defaultId : _devices.FirstOrDefault()?.Id;
			}
		}

		private bool ShouldManage(string id) => OutputSelectionPolicy.ShouldManage(id, _deviceScoped,
			_selectedId, _managedIds, _followDefault, _defaultId);

		private void SyncContexts() {
			if (_enumerator == null) {
				return;
			}
			HashSet<string> wanted = _devices.Where(d => ShouldManage(d.Id) || d.Id == _selectedId).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
			foreach (string id in _contexts.Keys.Where(id => !wanted.Contains(id)).ToList()) {
				DropContext(id);
			}
			foreach (string id in wanted) {
				try {
					if (!_contexts.TryGetValue(id, out var context)) {
						context = new(this, _enumerator.GetDevice(id));
						_contexts.Add(id, context);
						context.EndpointVolume = context.Device.AudioEndpointVolume;
						context.EndpointVolume.OnVolumeNotification += context.OnMasterVolume;
					}
					bool managed = ShouldManage(id);
					if (managed && !context.Managed) {
						context.Epoch = Guid.NewGuid();
						context.Managed = true;
						context.Manager = context.Device.AudioSessionManager;
						context.Manager.OnSessionCreated += context.OnCreated;
						RefreshSessions(context);
					} else if (!managed && context.Managed) {
						StopSessions(context);
					}
				} catch (Exception e) {
					DropContext(id);
					Logger.Error("Could not manage an audio output", e);
					ReportError("An audio output is unavailable; use Refresh to retry.");
				}
			}
		}

		private VolumeKey RuleKey(EndpointContext context, string name) =>
			_deviceScoped ? new(context.Id, name) : VolumeKey.Global(name);

		private void RefreshSessions(EndpointContext context) {
			if (!context.Managed || context.Manager == null) {
				return;
			}
			context.Manager.RefreshSessions();
			SessionCollection collection = context.Manager.Sessions;
			HashSet<string> seen = [];
			for (int i = 0; i < collection.Count; i++) {
				AudioSessionControl control;
				try { control = collection[i]; } catch { continue; }
				string? instanceId = null;
				try {
					if (control.State != AudioSessionState.AudioSessionStateExpired) {
						instanceId = control.GetSessionInstanceIdentifier;
					}
				} catch { }
				if (instanceId == null || !seen.Add(instanceId) || context.Sessions.ContainsKey(instanceId)) {
					Try(control.Dispose);
					continue;
				}
				string name = SessionNaming.Resolve(control);
				SessionEventsHandler handler = new(this, context.Id, context.Epoch, instanceId);
				// Initial policy is applied before registering callbacks. This does not mute/unmute.
				Try(() => control.SimpleAudioVolume.Volume = _policy.Resolve(RuleKey(context, name)).ScalarVolume);
				context.Sessions.Add(instanceId, new(control, handler, name));
				Try(() => control.RegisterEventClient(handler));
			}
			foreach (string stale in context.Sessions.Keys.Where(id => !seen.Contains(id)).ToList()) {
				RemoveSession(context, stale);
			}
			// Reconciliation also recovers a missed volume callback without reapplying
			// unlocked rules. Deferred corrections run on the one-shot timer.
			foreach (TrackedSession session in context.Sessions.Values) {
				CorrectIfMaintained(context, session);
			}
			SchedulePendingCorrections();
		}

		/// <summary>
		/// Retry only sessions with an outstanding short correction deferral. A
		/// one-shot timer is scheduled for the earliest deadline; it sleeps
		/// indefinitely when no correction is outstanding.
		/// </summary>
		private void SchedulePendingCorrections() {
			long now = Environment.TickCount64;
			long earliest = long.MaxValue;
			foreach (EndpointContext context in _contexts.Values.Where(c => c.Managed)) {
				foreach (TrackedSession session in context.Sessions.Values) {
					if (!session.Gate.Pending || !_policy.Resolve(RuleKey(context, session.Name)).MaintainVolume) { continue; }
					earliest = Math.Min(earliest, session.Gate.RetryAfterMilliseconds(now));
				}
			}
			try {
				// One-shot, not a background 100Hz scan. A zero deadline means ASAP.
				_maintenanceTimer?.Change(earliest == long.MaxValue ? Timeout.Infinite :
					(int)Math.Clamp(earliest, 1L, int.MaxValue), Timeout.Infinite);
			} catch (ObjectDisposedException) { /* Shutdown raced this worker action. */ }
		}

		private void QueueMaintenanceTick() {
			if (Volatile.Read(ref _stopping) != 0 || Interlocked.Exchange(ref _maintenanceTickQueued, 1) != 0) { return; }
			Post(() => {
				try {
					foreach (EndpointContext context in _contexts.Values.Where(c => c.Managed)) {
						foreach (TrackedSession session in context.Sessions.Values) {
							if (session.Gate.Pending) { CorrectIfMaintained(context, session); }
						}
					}
					RequestVolumeSnapshot();
				} finally {
					Interlocked.Exchange(ref _maintenanceTickQueued, 0);
					SchedulePendingCorrections();
				}
			});
		}

		private void CorrectIfMaintained(EndpointContext context, TrackedSession session) {
			VolumeDecision decision = _policy.Resolve(RuleKey(context, session.Name));
			if (!decision.MaintainVolume) { session.Gate.Reset(); return; }
			long now = Environment.TickCount64;
			float actual;
			try { actual = session.Control.SimpleAudioVolume.Volume; }
			catch (Exception e) {
				session.Gate.MarkWriteFailed(now);
				ReportMaintenanceFailure(context, session, now, e);
				return;
			}
			if (!session.Gate.TryCorrect(actual, decision.ScalarVolume, now)) { return; }
			try {
				session.Control.SimpleAudioVolume.Volume = decision.ScalarVolume;
			} catch (Exception e) {
				session.Gate.MarkWriteFailed(now);
				ReportMaintenanceFailure(context, session, now, e);
			}
		}

		private void ReportMaintenanceFailure(EndpointContext context, TrackedSession session, long now, Exception error) {
			if (session.LastFailureReportedAt == 0 || now - session.LastFailureReportedAt >= 5000) {
				Logger.Warn($"Maintain cannot adjust {session.Name} on {context.Name}: {error.Message}");
				session.LastFailureReportedAt = now;
			}
		}

		private void RemoveSession(EndpointContext context, string instanceId) {
			if (!context.Sessions.Remove(instanceId, out var session)) {
				return;
			}
			Try(() => session.Control.UnRegisterEventClient(session.Handler));
			Try(session.Control.Dispose);
		}

		private void StopSessions(EndpointContext context) {
			context.Managed = false;
			context.Epoch = Guid.NewGuid();
			if (context.Manager != null) {
				context.Manager.OnSessionCreated -= context.OnCreated;
			}
			foreach (string id in context.Sessions.Keys.ToList()) {
				RemoveSession(context, id);
			}
			// AudioSessionManager is cached by MMDevice. Keep it alive until that context is disposed.
		}

		private void DropContext(string id) {
			if (!_contexts.Remove(id, out var context)) {
				return;
			}
			StopSessions(context);
			if (context.EndpointVolume != null) {
				context.EndpointVolume.OnVolumeNotification -= context.OnMasterVolume;
				Try(context.EndpointVolume.Dispose);
			}
			if (context.Manager != null) {
				Try(context.Manager.Dispose);
			}
			Try(context.Device.Dispose);
		}

		private void PublishMaster(EndpointContext context) {
			if (context.EndpointVolume != null && context.Id == _selectedId) {
				Try(() => MasterVolumeChanged?.Invoke(context.Id, context.EndpointVolume.MasterVolumeLevelScalar, _revision));
			}
		}

		private void PublishAll() {
			DevicesChanged?.Invoke(_devices, _selectedId, _revision);
			if (_selectedId != null && _contexts.TryGetValue(_selectedId, out var context)) {
				PublishMaster(context);
			}
			PublishSessions();
		}

		/// <summary>
		/// Bound WPF updates under adversarial writers while preserving immediate
		/// audio correction. Coalescing is for DISPLAY only, never the Core Audio
		/// change handler or the saved volume target.
		/// </summary>
		private void RequestVolumeSnapshot() {
			long elapsed = Environment.TickCount64 - _lastUiSnapshotAt;
			if (_lastUiSnapshotAt == 0 || elapsed >= UiSnapshotIntervalMilliseconds) {
				PublishSessions();
				return;
			}
			if (Interlocked.Exchange(ref _uiSnapshotQueued, 1) != 0) { return; }
			try {
				_uiSnapshotTimer?.Change((int)Math.Clamp(UiSnapshotIntervalMilliseconds - elapsed, 1L,
					UiSnapshotIntervalMilliseconds), Timeout.Infinite);
			} catch (ObjectDisposedException) { /* Shutting down. */ }
		}

		private void PublishSessions() {
			Interlocked.Exchange(ref _uiSnapshotQueued, 0);
			_lastUiSnapshotAt = Environment.TickCount64;
			List<AudioSessionInfo> sessions = [];
			foreach (EndpointContext context in _contexts.Values.Where(c => c.Managed)) {
				foreach ((string instanceId, TrackedSession session) in context.Sessions) {
					Try(() => sessions.Add(new(context.Id, context.Name, instanceId, session.Name,
						session.Control.SimpleAudioVolume.Volume, session.Name == SessionNaming.SystemSoundsName)));
				}
			}
			SessionsChanged?.Invoke(new(_revision, _deviceScoped, _selectedId, sessions));
		}

		private void Post(Action action) {
			if (Volatile.Read(ref _stopping) != 0) {
				return;
			}
			try { _queue.Add(action); } catch (InvalidOperationException) { }
		}

		private void WorkerLoop() {
			try {
				foreach (Action action in _queue.GetConsumingEnumerable()) {
					if (Volatile.Read(ref _stopping) != 0) {
						break;
					}
					try { action(); } catch (Exception e) {
						Logger.Error("Audio worker action failed", e);
						ReportError("Audio error: " + e.Message);
					}
				}
			} finally {
				foreach (string id in _contexts.Keys.ToList()) { DropContext(id); }
				if (_enumerator != null) {
					if (_notificationClient != null) {
						Try(() => _enumerator.UnregisterEndpointNotificationCallback(_notificationClient));
					}
					Try(_enumerator.Dispose);
				}
				_queue.Dispose();
			}
		}

		private void ReportError(string message) {
			Logger.Warn(message);
			// A dispatcher can already be shutting down when a COM operation fails.
			Try(() => ErrorOccurred?.Invoke(message));
		}

		private static void Try(Action action) {
			try { action(); } catch { /* Processes and endpoints can disappear during a COM operation. */ }
		}

		public void Dispose() {
			if (Interlocked.Exchange(ref _stopping, 1) != 0) { return; }
			_maintenanceTimer?.Dispose();
			_uiSnapshotTimer?.Dispose();
			_reconcileTimer?.Dispose();
			try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { }
			if (Volatile.Read(ref _started) != 0) {
				_thread.Join(TimeSpan.FromSeconds(5));
			} else {
				_queue.Dispose();
			}
			GC.SuppressFinalize(this);
		}
	}
}
