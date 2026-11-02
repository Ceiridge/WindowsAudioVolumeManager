using System.Text.Json;

namespace WindowsAudioVolumeManager.Audio {
	/// <summary>Persistent rule identity. An empty DeviceId denotes a legacy global rule.</summary>
	public readonly record struct VolumeKey(string DeviceId, string Name) {
		public bool IsGlobal => DeviceId.Length == 0;
		public string WriteKey => "volume:" + JsonSerializer.Serialize(new[] { DeviceId, Name });
		public static VolumeKey Global(string name) => new("", name);
	}

	public sealed record DeviceVolumeRule(VolumeKey Key, string DeviceName, float ScalarVolume, bool MaintainVolume);
	public readonly record struct VolumeDecision(float ScalarVolume, string Source, bool MaintainVolume);
}
