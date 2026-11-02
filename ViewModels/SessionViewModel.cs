using System.Windows.Input;
using WindowsAudioVolumeManager.Audio;

namespace WindowsAudioVolumeManager.ViewModels {
	/// <summary>One application group on one output (or a legacy global application row).</summary>
	public class SessionViewModel : ViewModelBase {
		private readonly MainViewModel _owner;
		private int _volume;
		private bool _saved;
		private bool _maintain;
		private bool _suppress;
		private bool _isActive;
		private string _deviceName = "";
		private string _ruleSummary = "";
		private string _liveText = "";
		private DateTime? _lastSeenAt;

		public VolumeKey Key { get; }
		public string Name => Key.Name;
		public string DeviceId { get; private set; } = "";
		public string DeviceName { get => _deviceName; private set => SetField(ref _deviceName, value); }
		public string DisplayName => IsSystemSounds ? "System Sounds" : Name;
		public bool IsSystemSounds => Name == SessionNaming.SystemSoundsName;
		public bool CanSave => Name != "Unknown Session";
		public bool CanMaintain => !Key.IsGlobal && Saved && CanSave;
		public string SavedLabel => Key.IsGlobal ? "Saved" : "Override";
		public string GroupLabel => Key.IsGlobal ? (IsSystemSounds ? "System" : IsActive ? "Running Apps" : "Saved · not running") : DeviceName;
		public ICommand RemoveSavedCommand { get; }
		public ICommand ResumeMaintenanceCommand { get; }

		public SessionViewModel(MainViewModel owner, VolumeKey key) {
			_owner = owner;
			Key = key;
			RemoveSavedCommand = new RelayCommand(() => Saved = false, () => Saved);
			ResumeMaintenanceCommand = new RelayCommand(() => _owner.ResumeMaintenance(this), () => CanMaintain && MaintainVolume);
		}

		public int Volume {
			get => _volume;
			set {
				int clamped = Math.Clamp(value, 0, 100);
				if (SetField(ref _volume, clamped)) {
					OnPropertyChanged(nameof(VolumeText));
					if (!_suppress) { _owner.ApplySessionVolume(this); }
				}
			}
		}
		public string VolumeText => Volume + " %";
		public string RuleSummary { get => _ruleSummary; private set => SetField(ref _ruleSummary, value); }
		public string LiveText { get => _liveText; private set => SetField(ref _liveText, value); }
		public bool IsActive { get => _isActive; private set => SetField(ref _isActive, value); }
		public DateTime? LastSeenAt { get => _lastSeenAt; set { if (SetField(ref _lastSeenAt, value)) { OnPropertyChanged(nameof(LastSeenText)); } } }
		public string LastSeenText => IsActive ? "Observed on this output" : LastSeenAt.HasValue ? "Last observed: " + LastSeenAt.Value.ToString("g") : "Not currently observed (inactive, disconnected or unmanaged)";

		public bool Saved {
			get => _saved;
			set {
				if (value && !CanSave && !_suppress) { return; }
				if (SetField(ref _saved, value)) {
					if (!value) { _maintain = false; OnPropertyChanged(nameof(MaintainVolume)); }
					OnPropertyChanged(nameof(CanMaintain));
					(RemoveSavedCommand as RelayCommand)?.RaiseCanExecuteChanged();
					if (!_suppress) { _owner.ApplySavedChanged(this); }
				}
			}
		}

		public bool MaintainVolume {
			get => _maintain;
			set {
				if (value && !CanMaintain) { return; }
				if (SetField(ref _maintain, value)) {
					(ResumeMaintenanceCommand as RelayCommand)?.RaiseCanExecuteChanged();
					if (!_suppress) { _owner.ApplyMaintainChanged(this); }
				}
			}
		}

		public void Update(string deviceId, string deviceName, IReadOnlyList<AudioSessionInfo> live,
			VolumeDecision decision, bool saved, DateTime? seen) {
			_suppress = true;
			try {
				DeviceId = deviceId;
				DeviceName = Key.IsGlobal ? "Global app setting" : deviceName;
				Saved = saved;
				MaintainVolume = decision.MaintainVolume;
				IsActive = live.Count > 0;
				LastSeenAt = seen;
				float minimum = IsActive ? live.Min(s => s.ScalarVolume) : decision.ScalarVolume;
				float maximum = IsActive ? live.Max(s => s.ScalarVolume) : decision.ScalarVolume;
				bool mixed = Math.Abs(maximum - minimum) > CorrectionGate.Tolerance;
				Volume = (int)Math.Round((MaintainVolume ? decision.ScalarVolume : minimum) * 100f);
				RuleSummary = decision.Source + $" · target {(int)Math.Round(decision.ScalarVolume * 100f)} %";
				LiveText = mixed ? $"Mixed live volumes: {minimum * 100f:0.#}–{maximum * 100f:0.#} % (slider edits the group)"
					: IsActive ? $"Live: {minimum * 100f:0.#} % · {live.Count} session(s)" : LastSeenText;
				OnPropertyChanged(nameof(GroupLabel));
				OnPropertyChanged(nameof(LastSeenText));
			} finally { _suppress = false; }
		}
	}
}
