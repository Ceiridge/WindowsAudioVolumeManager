using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace WindowsAudioVolumeManager.Audio {
	/// <summary>
	/// Receives CoreAudio callbacks for a single session and forwards the relevant
	/// ones to the <see cref="AudioDeviceService"/>. Callbacks arrive on COM worker
	/// threads; the service re-marshals them onto its own audio thread.
	/// </summary>
	public class SessionEventsHandler : IAudioSessionEventsHandler {
		private readonly AudioDeviceService _service;
		private readonly string _instanceId;

		public SessionEventsHandler(AudioDeviceService service, string instanceId) {
			_service = service;
			_instanceId = instanceId;
		}

		public void OnVolumeChanged(float volume, bool isMuted) =>
			_service.HandleSessionVolumeChanged(_instanceId, volume);

		public void OnStateChanged(AudioSessionState state) {
			if (state == AudioSessionState.AudioSessionStateExpired) {
				_service.HandleSessionExpired(_instanceId);
			}
		}

		public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) =>
			_service.HandleSessionExpired(_instanceId);

		public void OnDisplayNameChanged(string displayName) { }
		public void OnIconPathChanged(string iconPath) { }
		public void OnChannelVolumeChanged(uint channelCount, nint newVolumes, uint channelIndex) { }
		public void OnGroupingParamChanged(ref Guid groupingId) { }
	}
}
