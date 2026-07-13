using System.Windows.Input;
using WindowsAudioVolumeManager.Audio;

namespace WindowsAudioVolumeManager.ViewModels {
	/// <summary>
	/// One row in the session list. Represents all audio session instances sharing
	/// the same application name; can also represent a saved application that is
	/// currently not playing audio (IsActive = false).
	/// Volumes are absolute (0-100 % of the device), unlike the legacy
	/// master-relative math that could divide by zero.
	/// </summary>
	public class SessionViewModel : ViewModelBase {
		private readonly MainViewModel _owner;
		private int _volume;
		private bool _saved;
		private bool _isActive;
		private bool _suppressApply;
		private DateTime? _lastSeenAt;

		/// <summary>Internal name, matches the persisted key (e.g. "_System Sounds", "chrome.exe").</summary>
		public string Name { get; }

		public bool IsSystemSounds { get; }

		public ICommand RemoveSavedCommand { get; }

		public SessionViewModel(MainViewModel owner, string name, int volume, bool saved, bool isActive, DateTime? lastSeenAt) {
			_owner = owner;
			Name = name;
			IsSystemSounds = name == SessionNaming.SystemSoundsName;
			_volume = Math.Clamp(volume, 0, 100);
			_saved = saved;
			_isActive = isActive;
			_lastSeenAt = lastSeenAt;
			RemoveSavedCommand = new RelayCommand(() => Saved = false, () => Saved);
		}

		/// <summary>Pretty name for the UI; persistence keeps using <see cref="Name"/>.</summary>
		public string DisplayName => IsSystemSounds ? "System Sounds" : Name;

		/// <summary>Group header the list is grouped by; order is enforced by the owner's sort.</summary>
		public string GroupLabel => IsSystemSounds ? "System" : IsActive ? "Running Apps" : "Saved · not running";

		public int Volume {
			get => _volume;
			set {
				int clamped = Math.Clamp(value, 0, 100);
				if (_volume == clamped) {
					return;
				}

				_volume = clamped;
				OnPropertyChanged();
				OnPropertyChanged(nameof(VolumeText));

				if (!_suppressApply) {
					_owner.ApplySessionVolume(this);
				}
			}
		}

		public string VolumeText => _volume + " %";

		public bool Saved {
			get => _saved;
			set {
				if (SetField(ref _saved, value)) {
					(RemoveSavedCommand as RelayCommand)?.RaiseCanExecuteChanged();
					_owner.ApplySavedChanged(this);
				}
			}
		}

		public bool IsActive {
			get => _isActive;
			set {
				if (SetField(ref _isActive, value)) {
					OnPropertyChanged(nameof(GroupLabel));
					OnPropertyChanged(nameof(LastSeenText));
				}
			}
		}

		public DateTime? LastSeenAt {
			get => _lastSeenAt;
			set {
				if (SetField(ref _lastSeenAt, value)) {
					OnPropertyChanged(nameof(LastSeenText));
				}
			}
		}

		public string LastSeenText {
			get {
				if (IsActive) {
					return "Running now";
				}
				if (_lastSeenAt == null) {
					return "Last active: unknown";
				}

				int days = (int)(DateTime.Now.Date - _lastSeenAt.Value.Date).TotalDays;
				return "Last active: " + days switch {
					<= 0 => "today",
					1 => "yesterday",
					< 30 => $"{days} days ago",
					< 365 => $"{days / 30} months ago",
					_ => $"{days / 365} years ago"
				};
			}
		}

		/// <summary>Update from a CoreAudio notification without echoing back to the device.</summary>
		public void UpdateVolumeFromDevice(float scalarVolume) {
			_suppressApply = true;
			Volume = (int)Math.Round(scalarVolume * 100f);
			_suppressApply = false;
		}
	}
}
