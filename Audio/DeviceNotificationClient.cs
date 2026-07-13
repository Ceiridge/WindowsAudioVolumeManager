using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace WindowsAudioVolumeManager.Audio {
	/// <summary>
	/// Receives device topology notifications (device added/removed, default device
	/// changed) and forwards them to the <see cref="AudioDeviceService"/>.
	/// </summary>
	public class DeviceNotificationClient : IMMNotificationClient {
		private readonly AudioDeviceService _service;

		public DeviceNotificationClient(AudioDeviceService service) {
			_service = service;
		}

		public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) {
			if (flow == DataFlow.Render && role == Role.Multimedia) {
				_service.HandleDefaultDeviceChanged(defaultDeviceId);
			}
		}

		public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _service.HandleDeviceTopologyChanged();
		public void OnDeviceAdded(string pwstrDeviceId) => _service.HandleDeviceTopologyChanged();
		public void OnDeviceRemoved(string deviceId) => _service.HandleDeviceTopologyChanged();
		public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
	}
}
