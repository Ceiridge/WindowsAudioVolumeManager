using CSCore.CoreAudioAPI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WindowsAudioVolumeManager {
	public partial class MainWindow : Window {
		public const string DEFAULT_APP_NAME = "_Default_App";

		private readonly List<MMDevice> Devices = new List<MMDevice>();
		private readonly ObservableCollection<AudioSessionElement> SessionElements = new ObservableCollection<AudioSessionElement>();
		private HotkeyRegistrar HotkeyRegistrar;

		public readonly ConfigParser Parser;
		public List<SavedAudioSession> SavedSessions = new List<SavedAudioSession>();
		public readonly AudioSessionElement DefaultSessionElement;
		public bool HasModifiedDefaultSession = false;

		public MainWindow() {
			InitializeComponent();
			SessionListView.DataContext = SessionElements;
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

			Task.Run(() => {
				Thread.Sleep(5000); // Delay the hotkey registration to make sure the window exists
				Invoke(() => {
					HotkeyRegistrar = new HotkeyRegistrar(new WindowInteropHelper(this).Handle, unchecked((int)0xCE161D6E));
					HotkeyRegistrar.OnHotkeyPressed += OnHotkeyPressed;
					HotkeyRegistrar.RegisterHotkey((uint)HotkeyRegistrar.HotkeyModifier.MOD_CONTROL | (uint)HotkeyRegistrar.HotkeyModifier.MOD_SHIFT, 0x56); // VK_V
				});
			});
		}

		/// Has to run in an MTA thread
		private void RefreshUIData() {
			MMDevice device = GetSelectedDevice();
			using AudioEndpointVolume masterVolume = AudioEndpointVolume.FromDevice(device);

			using AudioSessionManager2 sessionManager = AudioSessionManager2.FromMMDevice(device);
			using AudioSessionEnumerator sessionEnumerator = sessionManager.GetSessionEnumerator();

			Invoke(SessionElements.Clear);
			foreach (AudioSessionControl session in sessionEnumerator) {
				using SimpleAudioVolume volume = session.QueryInterface<SimpleAudioVolume>();
				string name = GetSessionName(session);
				SavedAudioSession savedSession = Invoke(() => SavedSessions.FirstOrDefault((ss) => ss.Name.Equals(name)));

				if (savedSession == null) {
					volume.MasterVolume = DefaultSessionElement.ScalarVolume;
				}

				Invoke(() => AddSessionElement(name, volume.MasterVolume, session, savedSession));
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
						AudioSessionElement element = Invoke(() => SessionElements.FirstOrDefault((element) => element.Name.Equals(name)));

						if (element == null) {
							SavedAudioSession savedSession = Invoke(() => SavedSessions.FirstOrDefault((ss) => ss.Name.Equals(name)));
							volume.MasterVolume = savedSession != null ? savedSession.ScalarVolume : DefaultSessionElement.ScalarVolume;

							Invoke(() => AddSessionElement(name, volume.MasterVolume, session, savedSession));
						} else {
							float masterVol = volume.MasterVolume;

							if (element.ScalarVolume != masterVol) {
								element.ScalarVolume = masterVol;
								element.NotifyVolumeUpdate();
							}
						}
					}

					for (int i = SessionElements.Count - 1; i >= 0; i--) { // Reverse iteration to allow removal
						AudioSessionElement element = SessionElements[i];
						if (!sessionEnumerator.Any((session) => element.Name.Equals(GetSessionName(session)))) {
							Invoke(() => SessionElements.RemoveAt(i));
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
		private void AddSessionElement(string name, float scalarVolume, AudioSessionControl session, SavedAudioSession savedSession) {
			if (!SessionElements.Any((element) => element.Name.Equals(name))) {
				SessionElements.Add(new AudioSessionElement(name, scalarVolume, this, session, savedSession));
			}
		}



		private void RefreshAppsButton_Click(object sender, RoutedEventArgs e) {
			Task.Run(RefreshUIData);
		}

		private void MasterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
			MMDevice device = GetSelectedDevice();
			using AudioEndpointVolume masterVolume = AudioEndpointVolume.FromDevice(device);
			masterVolume.MasterVolumeLevelScalar = (float)(MasterSlider.Value / 100f);

			foreach (AudioSessionElement element in SessionElements) {
				element.NotifyVolumeUpdate();
			}
		}

		private void Window_StateChanged(object sender, EventArgs e) {
			if (WindowState == WindowState.Minimized) {
				Hide();
			}
		}

		private void OnHotkeyPressed(object sender, EventArgs e) {
			Show();
			Focus();
			WindowState = WindowState.Normal;
		}

		private bool rendered;
		protected override void OnContentRendered(EventArgs e) {
			base.OnContentRendered(e);

			if (rendered) {
				return;
			}

			rendered = true;
			WindowState = WindowState.Minimized; // Minimize by default
			Hide();
		}
	}
}
