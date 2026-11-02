namespace WindowsAudioVolumeManager.ViewModels {
	public sealed class DeviceChoiceViewModel : ViewModelBase {
		private readonly SettingsViewModel _settings;
		private bool _managed;
		public string Id { get; }
		public string Label { get; }
		public bool IsAvailable { get; }

		public DeviceChoiceViewModel(SettingsViewModel settings, string id, string name, bool available, bool isDefault) {
			_settings = settings;
			Id = id;
			IsAvailable = available;
			Label = name + (!available ? " (disconnected)" : isDefault && settings.FollowDefaultDevice ? " (also followed as default)" : "");
			_managed = settings.ManagedDeviceIds.Contains(id);
		}

		public bool Managed {
			get => _managed;
			set {
				if (SetField(ref _managed, value)) {
					_settings.SetDeviceManaged(Id, value);
				}
			}
		}
	}
}
