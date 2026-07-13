using WindowsAudioVolumeManager.Interop;
using WindowsAudioVolumeManager.Persistence;

namespace WindowsAudioVolumeManager.ViewModels {
	/// <summary>
	/// Settings shown in the expander of the main window. Values are persisted
	/// immediately; side effects (tray icon, autostart, hotkey re-registration,
	/// device following) are wired up by the App via the *Changed events.
	/// </summary>
	public class SettingsViewModel : ViewModelBase {
		private readonly SettingsRepository _settings;

		private bool _trayIconEnabled;
		private bool _autostart;
		private bool _startMinimized;
		private bool _followDefaultDevice;
		private uint _hotkeyModifiers;
		private uint _hotkeyKey;
		private string _hotkeyDisplay = "";

		public event Action<bool>? TrayIconEnabledChanged;
		public event Action<bool>? AutostartChanged;
		public event Action<bool>? FollowDefaultDeviceChanged;
		public event Action<uint, uint>? HotkeyChanged;

		public SettingsViewModel(SettingsRepository settings) {
			_settings = settings;

			_trayIconEnabled = settings.GetBool(SettingsRepository.Keys.TrayIconEnabled, true);
			_autostart = settings.GetBool(SettingsRepository.Keys.Autostart, false);
			_startMinimized = settings.GetBool(SettingsRepository.Keys.StartMinimized, true);
			_followDefaultDevice = settings.GetBool(SettingsRepository.Keys.FollowDefaultDevice, true);
			_hotkeyModifiers = settings.GetUInt(SettingsRepository.Keys.HotkeyModifiers, HotkeyManager.DefaultModifiers);
			_hotkeyKey = settings.GetUInt(SettingsRepository.Keys.HotkeyKey, HotkeyManager.DefaultKey);
			_hotkeyDisplay = HotkeyManager.Describe(_hotkeyModifiers, _hotkeyKey);
		}

		public uint HotkeyModifiers => _hotkeyModifiers;
		public uint HotkeyKey => _hotkeyKey;

		public bool TrayIconEnabled {
			get => _trayIconEnabled;
			set {
				if (SetField(ref _trayIconEnabled, value)) {
					_settings.SetBool(SettingsRepository.Keys.TrayIconEnabled, value);
					TrayIconEnabledChanged?.Invoke(value);
				}
			}
		}

		public bool Autostart {
			get => _autostart;
			set {
				if (SetField(ref _autostart, value)) {
					_settings.SetBool(SettingsRepository.Keys.Autostart, value);
					AutostartChanged?.Invoke(value);
				}
			}
		}

		public bool StartMinimized {
			get => _startMinimized;
			set {
				if (SetField(ref _startMinimized, value)) {
					_settings.SetBool(SettingsRepository.Keys.StartMinimized, value);
				}
			}
		}

		public bool FollowDefaultDevice {
			get => _followDefaultDevice;
			set {
				if (SetField(ref _followDefaultDevice, value)) {
					_settings.SetBool(SettingsRepository.Keys.FollowDefaultDevice, value);
					FollowDefaultDeviceChanged?.Invoke(value);
				}
			}
		}

		public string HotkeyDisplay {
			get => _hotkeyDisplay;
			private set => SetField(ref _hotkeyDisplay, value);
		}

		/// <summary>Called by the hotkey capture box in the view.</summary>
		public void SetHotkey(uint modifiers, uint virtualKey) {
			_hotkeyModifiers = modifiers;
			_hotkeyKey = virtualKey;
			_settings.SetUInt(SettingsRepository.Keys.HotkeyModifiers, modifiers);
			_settings.SetUInt(SettingsRepository.Keys.HotkeyKey, virtualKey);
			HotkeyDisplay = HotkeyManager.Describe(modifiers, virtualKey);
			HotkeyChanged?.Invoke(modifiers, virtualKey);
		}
	}
}
