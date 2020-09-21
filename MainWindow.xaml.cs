using CSCore.CoreAudioAPI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace WindowsAudioVolumeManager {
	public partial class MainWindow : Window {
		private readonly List<MMDevice> Devices = new List<MMDevice>();
		private readonly ObservableCollection<AudioSessionElement> SessionControls = new ObservableCollection<AudioSessionElement>();

		public MainWindow() {
			InitializeComponent();
			SessionListView.DataContext = SessionControls;

			Task.Run(() => { // Windows CoreAudio API has to be called in an MTA thread (typical Microsoft logic)
				using MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();
				MMDevice defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
				int count = 0;

				foreach (MMDevice device in deviceEnumerator.EnumAudioEndpoints(DataFlow.Render, DeviceState.Active)) {
					bool isDefault = defaultDevice.DeviceID.Equals(device.DeviceID);

					Invoke(() => {
						int index = AudioOutputCombo.Items.Add(device.FriendlyName + " (" + device.DeviceID + ")" + (isDefault ? " [PRIMARY]" : ""));
						Devices.Add(device);

						if (isDefault) {
							AudioOutputCombo.SelectedIndex = index;
						}
					});
					count++;
				}

				if(count > 0) {
					RefreshSessions();
				}
			});
		}

		/// Has to run in an MTA thread
		private void RefreshSessions() {
			MMDevice device = Devices[(int)Invoke(() => AudioOutputCombo.SelectedIndex)];

			using AudioSessionManager2 sessionManager = AudioSessionManager2.FromMMDevice(device);
			using AudioSessionEnumerator sessionEnumerator = sessionManager.GetSessionEnumerator();

			Invoke(SessionControls.Clear);
			foreach (AudioSessionControl session in sessionEnumerator) {
				string displayName = session.DisplayName;
				using AudioSessionControl2 session2 = session.QueryInterface<AudioSessionControl2>();
				using SimpleAudioVolume volume = session.QueryInterface<SimpleAudioVolume>();

				string name = (displayName == null || displayName.Length == 0) ? session2.Process.MainModule.ModuleName : displayName;
				int vol = (int)(volume.MasterVolume * 100d);

				Invoke(() => SessionControls.Add(new AudioSessionElement(name, vol)));
			}
		}

		private object Invoke(Action action) {
			return Invoke(() => {
				action();
				return null;
			});
		}
		private object Invoke(Func<object> action) {
			return Dispatcher.Invoke(DispatcherPriority.Normal, action);
		}


		private void RefreshAppsButton_Click(object sender, RoutedEventArgs e) {
			Task.Run(RefreshSessions);
		}
	}
}
