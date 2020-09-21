using CSCore.CoreAudioAPI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace WindowsAudioVolumeManager {
	public partial class MainWindow : Window {
		public const string DEFAULT_APP_NAME = "_Default_App";

		private readonly List<MMDevice> Devices = new List<MMDevice>();
		private readonly ObservableCollection<AudioSessionElement> SessionControls = new ObservableCollection<AudioSessionElement>();

		public readonly ConfigParser Parser;
		public List<SavedAudioSession> SavedSessions = new List<SavedAudioSession>();
		public readonly AudioSessionElement DefaultSessionElement;
		public bool HasModifiedDefaultSession = false;

		public MainWindow() {
			InitializeComponent();
			SessionListView.DataContext = SessionControls;
			DefaultSessionElement = new AudioSessionElement(DEFAULT_APP_NAME, 1f, this);
			DefaultSessionSlider.DataContext = DefaultSessionElement;

			try {
				Parser = new ConfigParser(this);
				Task.Run(Parser.DirtySaverThread);
				Parser.LoadConfig();
			} catch (Exception e) {
				Console.WriteLine(e);
			}

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

				if (count > 0) {
					RefreshUIData();
				}

				Task.Run(VolumeManagerThread);
			});
		}

		/// Has to run in an MTA thread
		private void RefreshUIData() {
			MMDevice device = GetSelectedDevice();
			using AudioEndpointVolume masterVolume = AudioEndpointVolume.FromDevice(device);

			using AudioSessionManager2 sessionManager = AudioSessionManager2.FromMMDevice(device);
			using AudioSessionEnumerator sessionEnumerator = sessionManager.GetSessionEnumerator();

			Invoke(SessionControls.Clear);
			foreach (AudioSessionControl session in sessionEnumerator) {
				using SimpleAudioVolume volume = session.QueryInterface<SimpleAudioVolume>();
				string name = GetSessionName(session);
				SavedAudioSession savedSession = Invoke(() => SavedSessions.FirstOrDefault((ss) => ss.Name.Equals(name)));

				if(savedSession == null) {
					volume.MasterVolume = DefaultSessionElement.ScalarVolume;
				}

				Invoke(() => SessionControls.Add(new AudioSessionElement(name, volume.MasterVolume, this, session, savedSession)));
			}

			Invoke(() => {
				int masterVol = (int)(masterVolume.MasterVolumeLevelScalar * 100f);

				MasterSlider.Value = masterVol;
				if (!HasModifiedDefaultSession) {
					DefaultSessionElement.Volume = masterVol;
					HasModifiedDefaultSession = true;
				}
				DefaultSessionElement.NotifyVolumeUpdate();
			});
		}

		private void VolumeManagerThread() {
			while (true) {
				Thread.Sleep(1000);

				try {
					MMDevice device = GetSelectedDevice();
					using AudioSessionManager2 sessionManager = AudioSessionManager2.FromMMDevice(device);
					using AudioSessionEnumerator sessionEnumerator = sessionManager.GetSessionEnumerator();

					foreach (AudioSessionControl session in sessionEnumerator) {
						using SimpleAudioVolume volume = session.QueryInterface<SimpleAudioVolume>();
						string name = GetSessionName(session);
						AudioSessionElement element = Invoke(() => SessionControls.FirstOrDefault((element) => element.Name.Equals(name)));

						if (element == null) {
							SavedAudioSession savedSession = Invoke(() => SavedSessions.FirstOrDefault((ss) => ss.Name.Equals(name)));
							volume.MasterVolume = savedSession != null ? savedSession.ScalarVolume : DefaultSessionElement.ScalarVolume;

							Invoke(() => SessionControls.Add(new AudioSessionElement(name, volume.MasterVolume, this, session, savedSession)));
						} else {
							float masterVol = volume.MasterVolume;

							if (element.ScalarVolume != masterVol) {
								element.ScalarVolume = masterVol;
								element.NotifyVolumeUpdate();
							}
						}
					}
				} catch (Exception e) {
					Console.WriteLine(e);
				}
			}
		}


		public void Invoke(Action action) { // Helper functions
			Invoke<object>(() => {
				action();
				return null;
			});
		}
		public T Invoke<T>(Func<T> action) {
			return (T)Dispatcher.Invoke(DispatcherPriority.Normal, action);
		}
		private MMDevice GetSelectedDevice() {
			return Devices[Invoke(() => AudioOutputCombo.SelectedIndex)];
		}
		private string GetSessionName(AudioSessionControl sessionControl) {
			string displayName = sessionControl.DisplayName;
			using AudioSessionControl2 session2 = sessionControl.QueryInterface<AudioSessionControl2>();
			string name = (displayName == null || displayName.Length == 0) ? session2.Process.MainModule.ModuleName : displayName;

			return name.Equals(@"@%SystemRoot%\System32\AudioSrv.Dll,-202") ? "_System Sounds" : name;
		}

		private void RefreshAppsButton_Click(object sender, RoutedEventArgs e) {
			Task.Run(RefreshUIData);
		}

		private void MasterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
			MMDevice device = GetSelectedDevice();
			using AudioEndpointVolume masterVolume = AudioEndpointVolume.FromDevice(device);
			masterVolume.MasterVolumeLevelScalar = (float)(MasterSlider.Value / 100f);

			foreach (AudioSessionElement element in SessionControls) {
				element.NotifyVolumeUpdate();
			}
		}
	}
}
