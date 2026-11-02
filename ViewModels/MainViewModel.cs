using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
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
		private readonly Dictionary<VolumeKey, DateTime?> _lastSeen = new();
		private readonly Dictionary<string, string> _deviceNames = new(StringComparer.Ordinal);
		private IReadOnlyList<AudioSessionInfo> _live = [];
		private AudioDeviceInfo? _selectedDevice;
		private bool _suppressDeviceApply;
		private int _masterVolume;
		private bool _suppressMasterApply;
		private int _defaultVolume;
		private string _searchText = "";
		private string _statusText = "Ready";
		private string _hotkeyStatusText = "";

		public ObservableCollection<AudioDeviceInfo> Devices { get; } = [];
		public ObservableCollection<DeviceChoiceViewModel> DeviceChoices { get; } = [];
		public ObservableCollection<SessionViewModel> Sessions { get; } = [];
		public ICollectionView SessionsView { get; }
		public ICommand RefreshCommand { get; }
		public SettingsViewModel Settings { get; }
		public bool HasSelectedDevice => SelectedDevice != null;

		public MainViewModel(AudioDeviceService audioService, VolumePolicy policy, DebouncedWriter writer,
			SettingsViewModel settings, IEnumerable<SavedSessionRecord> savedRecords,
			IEnumerable<SavedDeviceRecord> deviceRecords, Dispatcher dispatcher) {
			_audioService = audioService;
			_policy = policy;
			_writer = writer;
			Settings = settings;
			_dispatcher = dispatcher;
			foreach (SavedSessionRecord record in savedRecords) { _lastSeen[VolumeKey.Global(record.Name)] = record.LastSeenAt; }
			foreach (SavedDeviceRecord record in deviceRecords) {
				_lastSeen[record.Rule.Key] = record.LastSeenAt;
				_deviceNames[record.Rule.Key.DeviceId] = record.Rule.DeviceName;
			}
			_defaultVolume = (int)Math.Round(_policy.DefaultVolumeScalar * 100f);
			RefreshCommand = new RelayCommand(_audioService.RequestRefresh);
			SessionsView = CollectionViewSource.GetDefaultView(Sessions);
			SessionsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SessionViewModel.GroupLabel)));
			SessionsView.Filter = item => string.IsNullOrWhiteSpace(_searchText)
				|| ((SessionViewModel)item).DisplayName.Contains(_searchText.Trim(), StringComparison.OrdinalIgnoreCase)
				|| ((SessionViewModel)item).DeviceName.Contains(_searchText.Trim(), StringComparison.OrdinalIgnoreCase);

			_audioService.DevicesChanged += (devices, id, revision) => _dispatcher.BeginInvoke(() => {
				if (revision == _audioService.ConfigurationVersion) { UpdateDevices(devices, id); }
			});
			_audioService.MasterVolumeChanged += (id, volume, revision) => _dispatcher.BeginInvoke(() => {
				if (revision == _audioService.ConfigurationVersion && id == SelectedDevice?.Id) {
					_suppressMasterApply = true;
					try { MasterVolume = (int)Math.Round(volume * 100f); } finally { _suppressMasterApply = false; }
				}
			});
			_audioService.SessionsChanged += snapshot => _dispatcher.BeginInvoke(() => {
				if (snapshot.Revision != _audioService.ConfigurationVersion || snapshot.DeviceScoped != Settings.SeparateVolumesByDevice) { return; }
				_live = snapshot.Sessions;
				RebuildRows();
			});
			_audioService.ErrorOccurred += text => _dispatcher.BeginInvoke(() => StatusText = text);
			Settings.DeviceModeChanged += ApplyDeviceMode;
			Settings.PropertyChanged += (_, args) => {
				if (args.PropertyName == nameof(Settings.FollowDefaultDevice)) { UpdateChoices(); }
			};
			ApplyDeviceMode();
		}

		public AudioDeviceInfo? SelectedDevice {
			get => _selectedDevice;
			set {
				if (!SetField(ref _selectedDevice, value) || value == null || _suppressDeviceApply) { return; }
				OnPropertyChanged(nameof(HasSelectedDevice));
				_audioService.SelectDevice(value.Id);
				string id = value.Id;
				_writer.Schedule("setting:" + SettingsRepository.Keys.SelectedDeviceId,
					(connection, transaction) => SettingsRepository.Set(connection, SettingsRepository.Keys.SelectedDeviceId, id, transaction));
			}
		}

		public int MasterVolume {
			get => _masterVolume;
			set {
				if (SetField(ref _masterVolume, Math.Clamp(value, 0, 100))) {
					OnPropertyChanged(nameof(MasterVolumeText));
					if (!_suppressMasterApply && SelectedDevice != null) {
						_audioService.SetMasterVolume(SelectedDevice.Id, _masterVolume / 100f);
					}
				}
			}
		}
		public string MasterVolumeText => MasterVolume + " %";
		public int DefaultVolume {
			get => _defaultVolume;
			set {
				if (SetField(ref _defaultVolume, Math.Clamp(value, 0, 100))) {
					OnPropertyChanged(nameof(DefaultVolumeText));
					float scalar = _defaultVolume / 100f;
					_policy.DefaultVolumeScalar = scalar;
					_writer.Schedule("setting:" + SettingsRepository.Keys.DefaultVolumeScalar,
						(connection, transaction) => SettingsRepository.Set(connection, SettingsRepository.Keys.DefaultVolumeScalar,
							scalar.ToString(CultureInfo.InvariantCulture), transaction));
					RebuildRows();
				}
			}
		}
		public string DefaultVolumeText => DefaultVolume + " %";
		public string SearchText { get => _searchText; set { if (SetField(ref _searchText, value)) { SessionsView.Refresh(); } } }
		public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }
		public string HotkeyStatusText { get => _hotkeyStatusText; set => SetField(ref _hotkeyStatusText, value); }

		private void ApplyDeviceMode() {
			_audioService.ConfigureDeviceMode(Settings.SeparateVolumesByDevice, Settings.ManagedDeviceIds);
			_live = [];
			UpdateChoices();
			RebuildRows();
			StatusText = Settings.SeparateVolumesByDevice
				? "Device mode: only checked outputs and the followed Windows default are managed."
				: "Legacy mode: app preferences are global; only the selected output is managed.";
		}

		private void UpdateDevices(IReadOnlyList<AudioDeviceInfo> devices, string? id) {
			_suppressDeviceApply = true;
			try {
				Devices.Clear();
				foreach (var device in devices) {
					Devices.Add(device);
					_deviceNames[device.Id] = device.FriendlyName;
				}
				_selectedDevice = devices.FirstOrDefault(d => d.Id == id);
				OnPropertyChanged(nameof(SelectedDevice));
				OnPropertyChanged(nameof(HasSelectedDevice));
			} finally { _suppressDeviceApply = false; }
			UpdateChoices();
		}

		private void UpdateChoices() {
			DeviceChoices.Clear();
			HashSet<string> ids = Devices.Select(d => d.Id).Concat(Settings.ManagedDeviceIds)
				.Concat(_policy.GetAllDevices().Select(r => r.Key.DeviceId)).ToHashSet(StringComparer.Ordinal);
			foreach (string id in ids.OrderBy(DeviceName, StringComparer.OrdinalIgnoreCase)) {
				AudioDeviceInfo? available = Devices.FirstOrDefault(d => d.Id == id);
				DeviceChoices.Add(new(Settings, id, DeviceName(id), available != null, available?.IsDefault == true));
			}
		}

		private string DeviceName(string id) => _deviceNames.TryGetValue(id, out string? name) ? name : "Unavailable output";
		private bool IsCurrentMode(SessionViewModel row) => Settings.SeparateVolumesByDevice != row.Key.IsGlobal;

		public void ApplySessionVolume(SessionViewModel row) {
			if (!IsCurrentMode(row)) { return; }
			// Publish the new rule before queuing its live write; callbacks see the new target.
			if (row.Saved) { SaveRule(row); }
			if (row.IsActive) { _audioService.SetSessionVolume(row.DeviceId, row.Name, row.Volume / 100f, !row.Key.IsGlobal); }
			else { RebuildRows(); }
		}

		public void ApplySavedChanged(SessionViewModel row) {
			if (!IsCurrentMode(row)) { return; }
			VolumeKey key = row.Key;
			if (row.Saved) {
				SaveRule(row);
				_audioService.ReapplyRule(key);
			} else if (key.IsGlobal) {
				_policy.RemoveSaved(key.Name);
				_writer.Schedule(key.WriteKey, (c, t) => SessionVolumeRepository.Delete(c, key.Name, t));
				// Preserve legacy behavior: unsaving alone does not change a live volume.
			} else {
				_policy.RemoveDevice(key);
				_writer.Schedule(key.WriteKey, (c, t) => DeviceVolumeRepository.Delete(c, key, t));
				_audioService.ReapplyRule(key); // returns only this output to global/default inheritance
			}
			RebuildRows();
		}

		public void ApplyMaintainChanged(SessionViewModel row) {
			if (!IsCurrentMode(row) || !row.Saved || row.Key.IsGlobal) { return; }
			// A live Discord reset may be displayed on an unlocked row. Maintaining
			// a saved rule must keep its saved target, not save that reset volume.
			DeviceVolumeRule? updated = _policy.SetMaintenance(row.Key, row.MaintainVolume);
			if (updated == null) { return; }
			_writer.Schedule(row.Key.WriteKey, (c, t) => DeviceVolumeRepository.Upsert(c, updated, t));
			_audioService.ReapplyRule(row.Key);
			RebuildRows();
		}

		public void ResumeMaintenance(SessionViewModel row) {
			if (!IsCurrentMode(row) || !row.Saved || !row.MaintainVolume || row.Key.IsGlobal) { return; }
			// Retry the persisted target, not a transient Windows mixer value.
			_audioService.ReapplyRule(row.Key);
		}

		private void SaveRule(SessionViewModel row) {
			VolumeKey key = row.Key;
			float scalar = row.Volume / 100f;
			bool active = row.IsActive;
			if (key.IsGlobal) {
				_policy.SetSaved(key.Name, scalar);
				_writer.Schedule(key.WriteKey, (c, t) => SessionVolumeRepository.Upsert(c, key.Name, scalar, t));
			} else {
				DeviceVolumeRule rule = new(key, row.DeviceName, scalar, row.MaintainVolume);
				_policy.SetDevice(rule);
				_writer.Schedule(key.WriteKey, (c, t) => {
					DeviceVolumeRepository.Upsert(c, rule, t);
					if (active) { DeviceVolumeRepository.TouchLastSeen(c, key, t); }
				});
			}
			if (active) { _lastSeen[key] = DateTime.Now; }
		}

		private void MarkSeen(VolumeKey key) {
			_lastSeen[key] = DateTime.Now;
			_writer.Schedule("seen:" + key.WriteKey, (c, t) => {
				if (key.IsGlobal) { SessionVolumeRepository.TouchLastSeen(c, key.Name, t); }
				else { DeviceVolumeRepository.TouchLastSeen(c, key, t); }
			});
		}

		private void RebuildRows() {
			bool scoped = Settings.SeparateVolumesByDevice;
			Dictionary<VolumeKey, List<AudioSessionInfo>> active = _live.GroupBy(info => scoped
				? new VolumeKey(info.DeviceId, info.Name) : VolumeKey.Global(info.Name)).ToDictionary(group => group.Key, group => group.ToList());
			HashSet<VolumeKey> keys = active.Keys.ToHashSet();
			if (scoped) { keys.UnionWith(_policy.GetAllDevices().Select(r => r.Key)); }
			else { keys.UnionWith(_policy.GetAllSaved().Keys.Select(VolumeKey.Global)); }
			Dictionary<VolumeKey, SessionViewModel> existing = Sessions.ToDictionary(s => s.Key);
			List<SessionViewModel> result = [];
			foreach (VolumeKey key in keys) {
				if (!existing.TryGetValue(key, out var row)) { row = new(this, key); }
				IReadOnlyList<AudioSessionInfo> live = active.TryGetValue(key, out var instances) ? instances : Array.Empty<AudioSessionInfo>();
				bool saved = key.IsGlobal ? _policy.GetSaved(key.Name) != null : _policy.GetDevice(key) != null;
				if (saved && !row.IsActive && live.Count > 0) { MarkSeen(key); }
				string id = key.IsGlobal ? live.FirstOrDefault()?.DeviceId ?? SelectedDevice?.Id ?? "" : key.DeviceId;
				string name = live.FirstOrDefault()?.DeviceName ?? _policy.GetDevice(key)?.DeviceName ?? DeviceName(id);
				row.Update(id, name, live, _policy.Resolve(key), saved, _lastSeen.GetValueOrDefault(key));
				result.Add(row);
			}
			result.Sort((a, b) => {
				int device = string.Compare(a.DeviceName, b.DeviceName, StringComparison.OrdinalIgnoreCase);
				if (device != 0) { return device; }
				if (a.IsSystemSounds != b.IsSystemSounds) { return a.IsSystemSounds ? -1 : 1; }
				if (a.IsActive != b.IsActive) { return a.IsActive ? -1 : 1; }
				return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
			});
			if (!result.SequenceEqual(Sessions)) {
				Sessions.Clear();
				foreach (var row in result) { Sessions.Add(row); }
			}
			SessionsView.Refresh();
		}
	}
}
