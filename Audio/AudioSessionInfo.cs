namespace WindowsAudioVolumeManager.Audio {
	/// <summary>InstanceId is transient; only DeviceId + Name is persisted.</summary>
	public record AudioSessionInfo(string DeviceId, string DeviceName, string InstanceId,
		string Name, float ScalarVolume, bool IsSystemSounds);

	public record AudioStateSnapshot(long Revision, bool DeviceScoped, string? SelectedDeviceId,
		IReadOnlyList<AudioSessionInfo> Sessions);
}
