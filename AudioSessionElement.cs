using CSCore.CoreAudioAPI;
using System;

namespace WindowsAudioVolumeManager {
	public class AudioSessionElement : ObservablePropertyObject {
		public float ScalarVolume;
		private readonly MainWindow MainWindow;
		private readonly AudioSessionControl SessionControl;
		private SavedAudioSession SavedSession;

		public string Name { get; set; }

		public int Volume {
			get => (int)Math.Round(ScalarVolume * MainWindow.MasterSlider.Value);
			set {
				float newScalar = (float)(value / MainWindow.MasterSlider.Value);
				bool isDefault = Name.Equals(MainWindow.DEFAULT_APP_NAME);

				if (newScalar <= 1) {
					ScalarVolume = newScalar;
					NotifyVolumeUpdate();

					if (SessionControl != null) {
						using SimpleAudioVolume volume = SessionControl.QueryInterface<SimpleAudioVolume>();
						volume.MasterVolume = ScalarVolume;
					}

					if (!isDefault) {
						if (SavedSession != null) {
							SavedSession.ScalarVolume = ScalarVolume;
						} else {
							MainWindow.Invoke(() => MainWindow.SavedSessions.Add(SavedSession = new SavedAudioSession(Name, ScalarVolume)));
							OnPropertyChanged("Saved");
						}
					}

					if (MainWindow.Parser != null) {
						MainWindow.Parser.Dirty = true;

						if (MainWindow.Parser.SavedConfigObject != null && isDefault) {
							MainWindow.Parser.SavedConfigObject.DefaultVolumeScalar = ScalarVolume;
						}
					}
				}
			}
		}
		public string VolumeText => Volume + " (" + (int)(ScalarVolume * 100f) + "%)";

		public bool Saved {
			get => SavedSession != null;
			set {
				if (!value) { // checkbox unchecked
					MainWindow.Invoke(() => {
						if (MainWindow.SavedSessions.Contains(SavedSession)) {
							MainWindow.SavedSessions.Remove(SavedSession);
						}

						if (MainWindow.Parser != null) {
							MainWindow.Parser.Dirty = true;
						}
					});

					SavedSession = null;
					OnPropertyChanged("Saved");
				}
			}
		}

		public AudioSessionElement(string name, float scalarVolume, MainWindow mainWindow, AudioSessionControl sessionControl = null, SavedAudioSession savedSession = null) {
			Name = name;
			ScalarVolume = scalarVolume;
			MainWindow = mainWindow;
			SessionControl = sessionControl;
			SavedSession = savedSession;
		}

		public void NotifyVolumeUpdate() {
			OnPropertyChanged("Volume", "VolumeText");
		}
	}
}
