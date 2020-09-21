using CSCore.CoreAudioAPI;
using System;

namespace WindowsAudioVolumeManager {
	class AudioSessionElement : ObservablePropertyObject {
		public float ScalarVolume;
		private readonly MainWindow MainWindow;
		private readonly AudioSessionControl SessionControl;

		public string Name { get; set; }
		public int Volume {
			get => (int)Math.Round(ScalarVolume * MainWindow.MasterSlider.Value);
			set {
				float newScalar = (float)(value / MainWindow.MasterSlider.Value);
				if (newScalar <= 1) {
					ScalarVolume = newScalar;
					NotifyVolumeUpdate();

					if (SessionControl != null) {
						using SimpleAudioVolume volume = SessionControl.QueryInterface<SimpleAudioVolume>();
						volume.MasterVolume = ScalarVolume;
					}
				}
			}
		}
		public string VolumeText => Volume + " (" + (int)(ScalarVolume * 100f) + "%)";

		public AudioSessionElement(string name, float scalarVolume, MainWindow mainWindow, AudioSessionControl sessionControl = null) {
			Name = name;
			ScalarVolume = scalarVolume;
			MainWindow = mainWindow;
			SessionControl = sessionControl;
		}

		public void NotifyVolumeUpdate() {
			OnPropertyChanged("Volume", "VolumeText");
		}
	}
}
