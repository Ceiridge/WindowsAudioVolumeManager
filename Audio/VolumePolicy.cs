using System.Collections.Concurrent;

namespace WindowsAudioVolumeManager.Audio {
	/// <summary>Global preferences remain intact; endpoint overrides are additive.</summary>
	public class VolumePolicy {
		private readonly ConcurrentDictionary<string, float> _savedVolumes = new();
		private readonly ConcurrentDictionary<VolumeKey, DeviceVolumeRule> _deviceVolumes = new();
		private float _defaultVolumeScalar = 1f;

		public static float Clamp(float value) {
			if (!float.IsFinite(value)) {
				throw new ArgumentOutOfRangeException(nameof(value), "Volume must be finite.");
			}
			return Math.Clamp(value, 0f, 1f);
		}

		public float DefaultVolumeScalar {
			get => Volatile.Read(ref _defaultVolumeScalar);
			set => Volatile.Write(ref _defaultVolumeScalar, Clamp(value));
		}

		public void Load(IDictionary<string, float> savedVolumes) {
			_savedVolumes.Clear();
			foreach ((string name, float volume) in savedVolumes) {
				SetSaved(name, volume);
			}
		}

		public void LoadDevices(IEnumerable<DeviceVolumeRule> rules) {
			_deviceVolumes.Clear();
			foreach (DeviceVolumeRule rule in rules) {
				SetDevice(rule);
			}
		}

		public void SetSaved(string name, float scalarVolume) => _savedVolumes[name] = Clamp(scalarVolume);
		public void RemoveSaved(string name) => _savedVolumes.TryRemove(name, out _);
		public float? GetSaved(string name) => _savedVolumes.TryGetValue(name, out float value) ? value : null;
		public IReadOnlyDictionary<string, float> GetAllSaved() => new Dictionary<string, float>(_savedVolumes);
		public float GetDesiredVolume(string name) => GetSaved(name) ?? DefaultVolumeScalar;

		public void SetDevice(DeviceVolumeRule rule) {
			if (rule.Key.IsGlobal || string.IsNullOrWhiteSpace(rule.Key.Name)) {
				throw new ArgumentException("An endpoint rule needs an endpoint ID and application name.", nameof(rule));
			}
			_deviceVolumes[rule.Key] = rule with { ScalarVolume = Clamp(rule.ScalarVolume) };
		}

		/// <summary>Toggle enforcement without adopting a possibly reset live volume.</summary>
		public DeviceVolumeRule? SetMaintenance(VolumeKey key, bool enabled) {
			while (_deviceVolumes.TryGetValue(key, out var current)) {
				DeviceVolumeRule updated = current with { MaintainVolume = enabled };
				if (_deviceVolumes.TryUpdate(key, updated, current)) { return updated; }
			}
			return null;
		}

		public void RemoveDevice(VolumeKey key) => _deviceVolumes.TryRemove(key, out _);
		public DeviceVolumeRule? GetDevice(VolumeKey key) => _deviceVolumes.TryGetValue(key, out var rule) ? rule : null;
		public IReadOnlyList<DeviceVolumeRule> GetAllDevices() => _deviceVolumes.Values.ToList();

		public VolumeDecision Resolve(VolumeKey key) {
			DeviceVolumeRule? rule = key.IsGlobal ? null : GetDevice(key);
			if (rule != null) {
				return new(rule.ScalarVolume, "Device override", rule.MaintainVolume);
			}
			float? global = GetSaved(key.Name);
			return global.HasValue
				? new(global.Value, key.IsGlobal ? "Saved app setting" : "Inherited app setting", false)
				: new(DefaultVolumeScalar, "Default", false);
		}
	}
}
