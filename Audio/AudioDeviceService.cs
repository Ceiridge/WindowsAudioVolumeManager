using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using WindowsAudioVolumeManager.Diagnostics;

namespace WindowsAudioVolumeManager.Audio {
	/// <summary>
	/// Owns all CoreAudio objects on a dedicated MTA worker thread and exposes an
	/// event-driven API. Replaces the legacy 1 Hz polling thread: session creation,
	/// per-session volume changes, master volume changes and device topology changes
	/// all arrive via CoreAudio callbacks. A slow reconciliation timer (30 s) only
	/// exists as a safety net for missed events.
	///
	/// All public methods may be called from any thread; all events are raised on
	/// the audio worker thread (subscribers must marshal to the UI thread themselves).
	/// </summary>
	public class AudioDeviceService : IDisposable {
		private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

		/// <summary>(devices, currentDeviceId) whenever the device list or selection changes.</summary>
		public event Action<IReadOnlyList<AudioDeviceInfo>, string?>? DevicesChanged;
		/// <summary>Master volume scalar [0..1] of the current device.</summary>
		public event Action<float>? MasterVolumeChanged;
		/// <summary>Full snapshot whenever sessions appear/disappear or the device changes.</summary>
		public event Action<IReadOnlyList<AudioSessionInfo>>? SessionsChanged;
		/// <summary>(sessionName, scalarVolume) when a session's volume changes (also echoes our own writes).</summary>
		public event Action<string, float>? SessionVolumeChanged;
		/// <summary>Human-readable error for the status line.</summary>
		public event Action<string>? ErrorOccurred;

		private readonly VolumePolicy _policy;
		private readonly BlockingCollection<Action> _queue = new();
		private readonly Thread _thread;
		private Timer? _reconcileTimer;
		private volatile bool _followDefaultDevice;

		// Everything below is only touched on the worker thread.
		private MMDeviceEnumerator? _enumerator;
		private DeviceNotificationClient? _notificationClient;
		private MMDevice? _currentDevice;
		private AudioSessionManager? _sessionManager;
		private AudioEndpointVolume? _endpointVolume;
		private string? _currentDeviceId;
		private IReadOnlyList<AudioDeviceInfo> _deviceSnapshot = [];
		private readonly Dictionary<string, TrackedSession> _sessions = new();

		private sealed record TrackedSession(AudioSessionControl Control, SessionEventsHandler Handler, string InstanceId, string Name);

		public AudioDeviceService(VolumePolicy policy) {
			_policy = policy;
			_thread = new Thread(WorkerLoop) {
				IsBackground = true,
				Name = "AudioDeviceWorker"
			};
			_thread.SetApartmentState(ApartmentState.MTA);
		}

		public void Start(string? preferredDeviceId, bool followDefaultDevice) {
			_followDefaultDevice = followDefaultDevice;
			_thread.Start();
			Post(() => InitializeInternal(preferredDeviceId));
			_reconcileTimer = new Timer(_ => Post(RefreshSessionsInternal), null, ReconcileInterval, ReconcileInterval);
		}

		public void SelectDevice(string deviceId) => Post(() => SelectDeviceInternal(deviceId));

		public void SetMasterVolume(float scalar) => Post(() => {
			if (_endpointVolume != null) {
				_endpointVolume.MasterVolumeLevelScalar = Math.Clamp(scalar, 0f, 1f);
			}
		});

		public void SetSessionVolume(string name, float scalar) => Post(() => {
			float clamped = Math.Clamp(scalar, 0f, 1f);
			foreach (TrackedSession session in _sessions.Values) {
				if (session.Name == name) {
					Try(() => session.Control.SimpleAudioVolume.Volume = clamped);
				}
			}
		});

		public void RequestRefresh() => Post(() => {
			BuildDeviceListInternal();
			RefreshSessionsInternal();
		});

		public void SetFollowDefaultDevice(bool follow) {
			_followDefaultDevice = follow;
			if (follow) {
				Post(() => {
					string? defaultId = GetDefaultDeviceId();
					if (defaultId != null && defaultId != _currentDeviceId) {
						SelectDeviceInternal(defaultId);
					}
				});
			}
		}

		// ---- Callback entry points (arrive on COM threads, re-marshalled to the worker) ----

		public void HandleSessionVolumeChanged(string instanceId, float volume) => Post(() => {
			if (_sessions.TryGetValue(instanceId, out TrackedSession? session)) {
				SessionVolumeChanged?.Invoke(session.Name, volume);
			}
		});

		public void HandleSessionExpired(string instanceId) => Post(() => RemoveSessionInternal(instanceId, raiseSnapshot: true));

		public void HandleDefaultDeviceChanged(string defaultDeviceId) => Post(() => {
			BuildDeviceListInternal();
			if (_followDefaultDevice && defaultDeviceId != _currentDeviceId) {
				SelectDeviceInternal(defaultDeviceId);
			}
		});

		public void HandleDeviceTopologyChanged() => Post(() => {
			BuildDeviceListInternal();

			// If the current device disappeared, fall back to the default device.
			if (_currentDeviceId != null && _deviceSnapshot.All(d => d.Id != _currentDeviceId)) {
				string? defaultId = GetDefaultDeviceId() ?? _deviceSnapshot.FirstOrDefault()?.Id;
				if (defaultId != null) {
					_currentDeviceId = null; // force re-selection
					SelectDeviceInternal(defaultId);
				}
			}
		});

		// ---- Worker-thread implementation ----

		private void InitializeInternal(string? preferredDeviceId) {
			_enumerator = new MMDeviceEnumerator();
			_notificationClient = new DeviceNotificationClient(this);
			_enumerator.RegisterEndpointNotificationCallback(_notificationClient);

			BuildDeviceListInternal();

			string? targetId = null;
			if (!_followDefaultDevice && preferredDeviceId != null && _deviceSnapshot.Any(d => d.Id == preferredDeviceId)) {
				targetId = preferredDeviceId;
			}
			targetId ??= GetDefaultDeviceId() ?? _deviceSnapshot.FirstOrDefault()?.Id;

			if (targetId != null) {
				SelectDeviceInternal(targetId);
			} else {
				ErrorOccurred?.Invoke("No active audio output device found");
			}
		}

		private void BuildDeviceListInternal() {
			if (_enumerator == null) {
				return;
			}

			string? defaultId = GetDefaultDeviceId();
			List<AudioDeviceInfo> devices = [];

			foreach (MMDevice device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) {
				using (device) {
					devices.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
				}
			}

			_deviceSnapshot = devices;
			DevicesChanged?.Invoke(devices, _currentDeviceId);
		}

		private string? GetDefaultDeviceId() {
			try {
				using MMDevice defaultDevice = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
				return defaultDevice.ID;
			} catch {
				return null; // no default device (e.g. all devices unplugged)
			}
		}

		private void SelectDeviceInternal(string deviceId) {
			if (_enumerator == null || deviceId == _currentDeviceId) {
				return;
			}

			CleanupDeviceInternal();

			_currentDevice = _enumerator.GetDevice(deviceId);
			_currentDeviceId = deviceId;

			_endpointVolume = _currentDevice.AudioEndpointVolume;
			_endpointVolume.OnVolumeNotification += OnMasterVolumeNotification;

			_sessionManager = _currentDevice.AudioSessionManager;
			_sessionManager.OnSessionCreated += OnSessionCreatedCallback;

			RefreshSessionsInternal();
			MasterVolumeChanged?.Invoke(_endpointVolume.MasterVolumeLevelScalar);
			DevicesChanged?.Invoke(_deviceSnapshot, _currentDeviceId);
		}

		private void RefreshSessionsInternal() {
			if (_sessionManager == null) {
				return;
			}

			_sessionManager.RefreshSessions();
			SessionCollection sessions = _sessionManager.Sessions;
			HashSet<string> seen = [];
			bool changed = false;

			for (int i = 0; i < sessions.Count; i++) {
				AudioSessionControl control;
				try {
					control = sessions[i];
				} catch {
					continue;
				}

				string? instanceId = Try(() => control.GetSessionInstanceIdentifier);
				if (instanceId == null || !seen.Add(instanceId) || IsExpired(control)) {
					control.Dispose();
					continue;
				}

				if (_sessions.ContainsKey(instanceId)) {
					control.Dispose(); // already tracked via an earlier wrapper
					continue;
				}

				TrackSessionInternal(control, instanceId);
				changed = true;
			}

			foreach (string staleId in _sessions.Keys.Where(id => !seen.Contains(id)).ToList()) {
				RemoveSessionInternal(staleId, raiseSnapshot: false);
				changed = true;
			}

			if (changed) {
				RaiseSessionsSnapshot();
			}
		}

		private void TrackSessionInternal(AudioSessionControl control, string instanceId) {
			string name = SessionNaming.Resolve(control);

			// New sessions get their saved volume, unknown ones the default volume.
			Try(() => control.SimpleAudioVolume.Volume = _policy.GetDesiredVolume(name));

			SessionEventsHandler handler = new(this, instanceId);
			Try(() => control.RegisterEventClient(handler));

			_sessions[instanceId] = new TrackedSession(control, handler, instanceId, name);
		}

		private void RemoveSessionInternal(string instanceId, bool raiseSnapshot) {
			if (!_sessions.Remove(instanceId, out TrackedSession? session)) {
				return;
			}

			Try(() => session.Control.UnRegisterEventClient(session.Handler));
			Try(session.Control.Dispose);

			if (raiseSnapshot) {
				RaiseSessionsSnapshot();
			}
		}

		private void RaiseSessionsSnapshot() {
			List<AudioSessionInfo> snapshot = [];

			foreach (TrackedSession session in _sessions.Values) {
				float volume = Try(() => session.Control.SimpleAudioVolume.Volume, 0f);
				snapshot.Add(new AudioSessionInfo(session.InstanceId, session.Name, volume,
					session.Name == SessionNaming.SystemSoundsName));
			}

			SessionsChanged?.Invoke(snapshot);
		}

		private void CleanupDeviceInternal() {
			foreach (string instanceId in _sessions.Keys.ToList()) {
				RemoveSessionInternal(instanceId, raiseSnapshot: false);
			}

			if (_sessionManager != null) {
				_sessionManager.OnSessionCreated -= OnSessionCreatedCallback;
				Try(_sessionManager.Dispose);
				_sessionManager = null;
			}

			if (_endpointVolume != null) {
				_endpointVolume.OnVolumeNotification -= OnMasterVolumeNotification;
				Try(_endpointVolume.Dispose);
				_endpointVolume = null;
			}

			if (_currentDevice != null) {
				Try(_currentDevice.Dispose);
				_currentDevice = null;
			}

			_currentDeviceId = null;
		}

		private void OnSessionCreatedCallback(object sender, IAudioSessionControl newSession) =>
			Post(RefreshSessionsInternal); // re-enumerate instead of wrapping the raw COM pointer

		private void OnMasterVolumeNotification(AudioVolumeNotificationData data) =>
			MasterVolumeChanged?.Invoke(data.MasterVolume);

		// ---- Worker plumbing ----

		private void WorkerLoop() {
			foreach (Action action in _queue.GetConsumingEnumerable()) {
				try {
					action();
				} catch (Exception e) {
					Logger.Error("Audio worker action failed", e);
					ErrorOccurred?.Invoke("Audio error: " + e.Message);
				}
			}

			// Drained after CompleteAdding: release COM objects on the owning thread.
			try {
				CleanupDeviceInternal();

				if (_enumerator != null) {
					if (_notificationClient != null) {
						Try(() => _enumerator.UnregisterEndpointNotificationCallback(_notificationClient));
					}
					Try(_enumerator.Dispose);
					_enumerator = null;
				}
			} catch (Exception e) {
				Logger.Error("Audio worker cleanup failed", e);
			}
		}

		private void Post(Action action) {
			try {
				if (!_queue.IsAddingCompleted) {
					_queue.Add(action);
				}
			} catch (InvalidOperationException) {
				// racing CompleteAdding during shutdown; safe to drop
			}
		}

		private static void Try(Action action) {
			try {
				action();
			} catch {
				// COM objects can die at any time (process exit, device unplug); callers treat this as best-effort
			}
		}

		private static T? Try<T>(Func<T> func) {
			try {
				return func();
			} catch {
				return default;
			}
		}

		private static T Try<T>(Func<T> func, T fallback) {
			try {
				return func();
			} catch {
				return fallback;
			}
		}

		private static bool IsExpired(AudioSessionControl control) {
			try {
				return control.State == AudioSessionState.AudioSessionStateExpired;
			} catch {
				return true;
			}
		}

		public void Dispose() {
			_reconcileTimer?.Dispose();
			_queue.CompleteAdding();

			if (_thread.IsAlive) {
				_thread.Join(TimeSpan.FromSeconds(3));
			}

			_queue.Dispose();
			GC.SuppressFinalize(this);
		}
	}
}
