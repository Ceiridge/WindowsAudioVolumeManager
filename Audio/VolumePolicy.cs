using System.Collections.Concurrent;

namespace WindowsAudioVolumeManager.Audio {
	/// <summary>
	/// Thread-safe view of the desired volumes: saved per-app volumes plus the
	/// default volume for unknown apps. Read by the audio worker thread when a
	/// new session appears; kept up to date by the UI layer.
	/// </summary>
	public class VolumePolicy {
		private readonly ConcurrentDictionary<string, float> _savedVolumes = new();
		private float _defaultVolumeScalar = 1f;

		public float DefaultVolumeScalar {
			get => Volatile.Read(ref _defaultVolumeScalar);
			set => Volatile.Write(ref _defaultVolumeScalar, Math.Clamp(value, 0f, 1f));
		}

		public void Load(IDictionary<string, float> savedVolumes) {
			_savedVolumes.Clear();
			foreach ((string name, float volume) in savedVolumes) {
				_savedVolumes[name] = volume;
			}
		}

		public void SetSaved(string name, float scalarVolume) => _savedVolumes[name] = Math.Clamp(scalarVolume, 0f, 1f);

		public void RemoveSaved(string name) => _savedVolumes.TryRemove(name, out _);

		public float? GetSaved(string name) => _savedVolumes.TryGetValue(name, out float volume) ? volume : null;

		public IReadOnlyDictionary<string, float> GetAllSaved() => new Dictionary<string, float>(_savedVolumes);

		/// <summary>The volume a newly appearing session should get.</summary>
		public float GetDesiredVolume(string name) => GetSaved(name) ?? DefaultVolumeScalar;
	}
}
