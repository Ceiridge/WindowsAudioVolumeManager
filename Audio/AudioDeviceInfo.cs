namespace WindowsAudioVolumeManager.Audio {
	/// <summary>Immutable snapshot of a render device for the UI.</summary>
	public record AudioDeviceInfo(string Id, string FriendlyName, bool IsDefault) {
		public string DisplayName => IsDefault ? FriendlyName + " [PRIMARY]" : FriendlyName;
	}
}
