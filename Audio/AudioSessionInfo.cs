namespace WindowsAudioVolumeManager.Audio {
	/// <summary>Immutable snapshot of an audio session for the UI.</summary>
	public record AudioSessionInfo(string InstanceId, string Name, float ScalarVolume, bool IsSystemSounds);
}
