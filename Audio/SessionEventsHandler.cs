using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace WindowsAudioVolumeManager.Audio {
	/// <summary>Endpoint generation prevents late COM callbacks targeting a replacement context.</summary>
	public class SessionEventsHandler : IAudioSessionEventsHandler {
		private readonly AudioDeviceService _service;
		private readonly string _deviceId;
		private readonly Guid _generation;
		private readonly string _instanceId;

		public SessionEventsHandler(AudioDeviceService service, string deviceId, Guid generation, string instanceId) {
			_service = service;
			_deviceId = deviceId;
			_generation = generation;
			_instanceId = instanceId;
		}

		public void OnVolumeChanged(float volume, bool isMuted) =>
			_service.HandleSessionVolumeChanged(_deviceId, _generation, _instanceId);

		public void OnStateChanged(AudioSessionState state) {
			if (state == AudioSessionState.AudioSessionStateExpired) {
				_service.HandleSessionExpired(_deviceId, _generation, _instanceId);
			}
		}

		public void OnSessionDisconnected(AudioSessionDisconnectReason reason) =>
			_service.HandleSessionExpired(_deviceId, _generation, _instanceId);

		public void OnDisplayNameChanged(string displayName) { }
		public void OnIconPathChanged(string iconPath) { }
		public void OnChannelVolumeChanged(uint count, nint volumes, uint index) { }
		public void OnGroupingParamChanged(ref Guid groupingId) { }
	}
}
