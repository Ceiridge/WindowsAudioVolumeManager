using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using WindowsAudioVolumeManager.Audio;
using WindowsAudioVolumeManager.Persistence;

namespace WindowsAudioVolumeManager.ViewModels {
	public class MainViewModel : ViewModelBase {
		private readonly AudioDeviceService _audioService;
		private readonly VolumePolicy _policy;
		private readonly DebouncedWriter _writer;
		private readonly Dispatcher _dispatcher;
		private readonly Dictionary<string, DateTime?> _lastSeen;

		private AudioDeviceInfo? _selectedDevice;
		private bool _suppressDeviceApply;
		private int _masterVolume;
		private bool _suppressMasterApply;
		private int _defaultVolume;
		private string _searchText = "";
		private string _statusText = "Ready";
		private string _hotkeyStatusText = "";

		public ObservableCollection<AudioDeviceInfo> Devices { get; } = [];
		public ObservableCollection<SessionViewModel> Sessions { get; } = [];
		/// <summary>Grouped (System / Running Apps / Saved) and search-filtered view over <see cref="Sessions"/>.</summary>
		public ICollectionView SessionsView { get; }
		public ICommand RefreshCommand { get; }
		public SettingsViewModel Settings { get; }

		public MainViewModel(AudioDeviceService audioService, VolumePolicy policy, DebouncedWriter writer,
			SettingsViewModel settings, IEnumerable<SavedSessionRecord> savedRecords, Dispatcher dispatcher) {
			_audioService = audioService;
			_policy = policy;
			_writer = writer;
			Settings = settings;
			_dispatcher = dispatcher;
			_lastSeen = savedRecords.ToDictionary(r => r.Name, r => r.LastSeenAt);

			_defaultVolume = (int)Math.Round(_policy.DefaultVolumeScalar * 100f);
			RefreshCommand = new RelayCommand(_audioService.RequestRefresh);

			SessionsView = CollectionViewSource.GetDefaultView(Sessions);
			SessionsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SessionViewModel.GroupLabel)));
			SessionsView.Filter = item => string.IsNullOrWhiteSpace(_searchText)
				|| ((SessionViewModel)item).DisplayName.Contains(_searchText.Trim(), StringComparison.OrdinalIgnoreCase);

			_audioService.DevicesChanged += (devices, currentId) =>
				_dispatcher.BeginInvoke(() => UpdateDevices(devices, currentId));
			_audioService.MasterVolumeChanged += scalar =>
				_dispatcher.BeginInvoke(() => UpdateMasterVolumeFromDevice(scalar));
			_audioService.SessionsChanged += snapshot =>
				_dispatcher.BeginInvoke(() => MergeSessions(snapshot));
			_audioService.SessionVolumeChanged += (name, scalar) =>
				_dispatcher.BeginInvoke(() => UpdateSessionVolumeFromDevice(name, scalar));
			_audioService.ErrorOccurred += message =>
				_dispatcher.BeginInvoke(() => StatusText = message);

			// Show saved-but-not-running apps right away so they can be edited/unsaved.
			MergeSessions([]);
		}

		public AudioDeviceInfo? SelectedDevice {
			get => _selectedDevice;
			set {
				if (!SetField(ref _selectedDevice, value) || value == null || _suppressDeviceApply) {
					return;
				}

				_audioService.SelectDevice(value.Id);
				_writer.Schedule("setting:" + SettingsRepository.Keys.SelectedDeviceId,
					(connection, transaction) => SettingsRepository.Set(connection, SettingsRepository.Keys.SelectedDeviceId, value.Id, transaction));
			}
		}

		public int MasterVolume {
			get => _masterVolume;
			set {
				int clamped = Math.Clamp(value, 0, 100);
				if (_masterVolume == clamped) {
					return;
				}

				_masterVolume = clamped;
				OnPropertyChanged();
				OnPropertyChanged(nameof(MasterVolumeText));

				if (!_suppressMasterApply) {
					_audioService.SetMasterVolume(clamped / 100f);
				}
			}
		}

		public string MasterVolumeText => _masterVolume + " %";

		/// <summary>Volume that apps without a saved entry get when they first appear.</summary>
		public int DefaultVolume {
			get => _defaultVolume;
			set {
				int clamped = Math.Clamp(value, 0, 100);
				if (_defaultVolume == clamped) {
					return;
				}

				_defaultVolume = clamped;
				OnPropertyChanged();
				OnPropertyChanged(nameof(DefaultVolumeText));

				_policy.DefaultVolumeScalar = clamped / 100f;
				_writer.Schedule("setting:" + SettingsRepository.Keys.DefaultVolumeScalar,
					(connection, transaction) => SettingsRepository.Set(connection, SettingsRepository.Keys.DefaultVolumeScalar,
						(clamped / 100f).ToString(System.Globalization.CultureInfo.InvariantCulture), transaction));
			}
		}

		public string DefaultVolumeText => _defaultVolume + " %";

		public string SearchText {
			get => _searchText;
			set {
				if (SetField(ref _searchText, value)) {
					SessionsView.Refresh();
				}
			}
		}

		public string StatusText {
			get => _statusText;
			set => SetField(ref _statusText, value);
		}

		public string HotkeyStatusText {
			get => _hotkeyStatusText;
			set => SetField(ref _hotkeyStatusText, value);
		}

		// ---- Called by SessionViewModel ----

		public void ApplySessionVolume(SessionViewModel session) {
			float scalar = session.Volume / 100f;

			if (session.IsActive) {
				_audioService.SetSessionVolume(session.Name, scalar);
			}

			if (session.Saved) {
				_policy.SetSaved(session.Name, scalar);
				ScheduleUpsert(session.Name, scalar);
			}
		}

		public void ApplySavedChanged(SessionViewModel session) {
			if (session.Saved) {
				float scalar = session.Volume / 100f;
				_policy.SetSaved(session.Name, scalar);
				ScheduleUpsert(session.Name, scalar);
			} else {
				_policy.RemoveSaved(session.Name);
				_writer.Schedule("session:" + session.Name,
					(connection, transaction) => SessionVolumeRepository.Delete(connection, session.Name, transaction));

				if (!session.IsActive) {
					Sessions.Remove(session);
				}
			}
		}

		private void ScheduleUpsert(string name, float scalar) {
			_lastSeen[name] = DateTime.Now;
			_writer.Schedule("session:" + name,
				(connection, transaction) => SessionVolumeRepository.Upsert(connection, name, scalar, transaction));
		}

		// ---- Updates coming from the audio service (already on the UI thread) ----

		private void UpdateDevices(IReadOnlyList<AudioDeviceInfo> devices, string? currentDeviceId) {
			_suppressDeviceApply = true;
			try {
				Devices.Clear();
				foreach (AudioDeviceInfo device in devices) {
					Devices.Add(device);
				}

				_selectedDevice = devices.FirstOrDefault(d => d.Id == currentDeviceId);
				OnPropertyChanged(nameof(SelectedDevice));
			} finally {
				_suppressDeviceApply = false;
			}
		}

		private void UpdateMasterVolumeFromDevice(float scalar) {
			_suppressMasterApply = true;
			MasterVolume = (int)Math.Round(scalar * 100f);
			_suppressMasterApply = false;
		}

		private void UpdateSessionVolumeFromDevice(string name, float scalar) {
			Sessions.FirstOrDefault(s => s.Name == name)?.UpdateVolumeFromDevice(scalar);
		}

		private void MergeSessions(IReadOnlyList<AudioSessionInfo> snapshot) {
			// Group active session instances by display name (first volume wins).
			Dictionary<string, float> activeByName = [];
			foreach (AudioSessionInfo info in snapshot) {
				activeByName.TryAdd(info.Name, info.ScalarVolume);
			}

			Dictionary<string, SessionViewModel> existing = Sessions.ToDictionary(s => s.Name);
			List<SessionViewModel> result = [];
			bool groupChanged = false;

			foreach ((string name, float scalar) in activeByName) {
				if (existing.TryGetValue(name, out SessionViewModel? session)) {
					if (!session.IsActive) {
						session.IsActive = true;
						groupChanged = true;
						MarkSeen(session);
					}
					session.UpdateVolumeFromDevice(scalar);
					result.Add(session);
				} else {
					SessionViewModel created = new(this, name, (int)Math.Round(scalar * 100f),
						saved: _policy.GetSaved(name) != null, isActive: true, GetLastSeen(name));
					MarkSeen(created);
					result.Add(created);
				}
			}

			// Saved apps that are not currently playing stay visible (grayed out).
			foreach ((string name, float scalar) in _policy.GetAllSaved()) {
				if (activeByName.ContainsKey(name)) {
					continue;
				}

				if (existing.TryGetValue(name, out SessionViewModel? session)) {
					if (session.IsActive) {
						session.IsActive = false;
						groupChanged = true;
					}
					result.Add(session);
				} else {
					result.Add(new SessionViewModel(this, name, (int)Math.Round(scalar * 100f),
						saved: true, isActive: false, GetLastSeen(name)));
				}
			}

			// System Sounds first, then running apps, then saved ones; alphabetical within each group.
			result.Sort((a, b) => {
				if (a.IsSystemSounds != b.IsSystemSounds) {
					return a.IsSystemSounds ? -1 : 1;
				}
				if (a.IsActive != b.IsActive) {
					return a.IsActive ? -1 : 1;
				}
				return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
			});

			if (!result.SequenceEqual(Sessions)) {
				Sessions.Clear();
				foreach (SessionViewModel session in result) {
					Sessions.Add(session);
				}
			} else if (groupChanged) {
				// Same order, but a row switched groups in place: re-evaluate the grouping.
				SessionsView.Refresh();
			}
		}

		private DateTime? GetLastSeen(string name) => _lastSeen.TryGetValue(name, out DateTime? seen) ? seen : null;

		/// <summary>A saved app just became active: remember when it was last seen.</summary>
		private void MarkSeen(SessionViewModel session) {
			if (!session.Saved) {
				return;
			}

			session.LastSeenAt = DateTime.Now;
			_lastSeen[session.Name] = DateTime.Now;
			_writer.Schedule("seen:" + session.Name,
				(connection, transaction) => SessionVolumeRepository.TouchLastSeen(connection, session.Name, transaction));
		}
	}
}
