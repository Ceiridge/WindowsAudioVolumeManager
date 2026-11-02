using System.Text.Json;
using WindowsAudioVolumeManager.Interop;
using WindowsAudioVolumeManager.Persistence;

namespace WindowsAudioVolumeManager.ViewModels {
	public class SettingsViewModel : ViewModelBase {
		private const string DeviceModeKey = "separate_volumes_by_device";
		private const string ManagedDevicesKey = "managed_output_ids";
		private readonly SettingsRepository _settings;
		private bool _trayIconEnabled;
		private bool _autostart;
		private bool _startMinimized;
		private bool _followDefaultDevice;
		private bool _separateVolumesByDevice;
		private readonly HashSet<string> _managedDeviceIds = new(StringComparer.Ordinal);
		private uint _hotkeyModifiers;
		private uint _hotkeyKey;
		private string _hotkeyDisplay = "";

		public event Action<bool>? TrayIconEnabledChanged;
		public event Action<bool>? AutostartChanged;
		public event Action<bool>? FollowDefaultDeviceChanged;
		public event Action<uint, uint>? HotkeyChanged;
		public event Action? DeviceModeChanged;

		public SettingsViewModel(SettingsRepository settings) {
			_settings = settings;
			_trayIconEnabled = settings.GetBool(SettingsRepository.Keys.TrayIconEnabled, true);
			_autostart = CanAutostart && settings.GetBool(SettingsRepository.Keys.Autostart, false);
			_startMinimized = !Database.UsesIsolatedProfile && settings.GetBool(SettingsRepository.Keys.StartMinimized, true);
			_followDefaultDevice = settings.GetBool(SettingsRepository.Keys.FollowDefaultDevice, true);
			_separateVolumesByDevice = settings.GetBool(DeviceModeKey, false);
			try {
				string[] ids = JsonSerializer.Deserialize<string[]>(settings.Get(ManagedDevicesKey) ?? "[]") ?? [];
				_managedDeviceIds.UnionWith(ids.Where(id => !string.IsNullOrEmpty(id)));
			} catch (JsonException) {
				// A malformed opt-in configuration must not expand the set of managed outputs.
				_separateVolumesByDevice = false;
			}
			_hotkeyModifiers = settings.GetUInt(SettingsRepository.Keys.HotkeyModifiers, HotkeyManager.DefaultModifiers);
			_hotkeyKey = settings.GetUInt(SettingsRepository.Keys.HotkeyKey, HotkeyManager.DefaultKey);
			_hotkeyDisplay = HotkeyManager.Describe(_hotkeyModifiers, _hotkeyKey);
		}

		public bool CanAutostart => !Database.UsesIsolatedProfile;
		public string ProfileNotice => Database.UsesIsolatedProfile
			? "Isolated test profile: Start with Windows is intentionally disabled. Use Installer.cmd in the kit for permanent installation with your tested settings."
			: "Normal profile: Start with Windows registers this executable for your Windows account. Keep it in its installation folder.";
		public uint HotkeyModifiers => _hotkeyModifiers;
		public uint HotkeyKey => _hotkeyKey;
		public IReadOnlyList<string> ManagedDeviceIds => _managedDeviceIds.ToArray();

		public bool SeparateVolumesByDevice {
			get => _separateVolumesByDevice;
			set {
				if (SetField(ref _separateVolumesByDevice, value)) {
					_settings.SetBool(DeviceModeKey, value);
					DeviceModeChanged?.Invoke();
				}
			}
		}

		public void SetDeviceManaged(string id, bool enabled) {
			if (string.IsNullOrEmpty(id)) { return; }
			bool changed = enabled ? _managedDeviceIds.Add(id) : _managedDeviceIds.Remove(id);
			if (changed) {
				_settings.Set(ManagedDevicesKey, JsonSerializer.Serialize(_managedDeviceIds.Order(StringComparer.Ordinal)));
				OnPropertyChanged(nameof(ManagedDeviceIds));
				DeviceModeChanged?.Invoke();
			}
		}

		public bool TrayIconEnabled {
			get => _trayIconEnabled;
			set { if (SetField(ref _trayIconEnabled, value)) { _settings.SetBool(SettingsRepository.Keys.TrayIconEnabled, value); TrayIconEnabledChanged?.Invoke(value); } }
		}
		public bool Autostart {
			get => _autostart;
			set { if (value && !CanAutostart) { return; } if (SetField(ref _autostart, value)) { _settings.SetBool(SettingsRepository.Keys.Autostart, value); AutostartChanged?.Invoke(value); } }
		}
		public bool StartMinimized {
			get => _startMinimized;
			set { if (SetField(ref _startMinimized, value)) { _settings.SetBool(SettingsRepository.Keys.StartMinimized, value); } }
		}
		public bool FollowDefaultDevice {
			get => _followDefaultDevice;
			set { if (SetField(ref _followDefaultDevice, value)) { _settings.SetBool(SettingsRepository.Keys.FollowDefaultDevice, value); FollowDefaultDeviceChanged?.Invoke(value); } }
		}
		public string HotkeyDisplay { get => _hotkeyDisplay; private set => SetField(ref _hotkeyDisplay, value); }

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
