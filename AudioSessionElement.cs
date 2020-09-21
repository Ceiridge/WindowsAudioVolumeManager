using System;

namespace WindowsAudioVolumeManager {
	class AudioSessionElement : ObservablePropertyObject {
		public float ScalarVolume;
		private readonly MainWindow MainWindow;

		public string Name { get; set; }
		public int Volume {
			get => (int)Math.Round(ScalarVolume * MainWindow.MasterSlider.Value);
			set {
				float newScalar = (float)(value / MainWindow.MasterSlider.Value);
				if (newScalar <= 1) {
					ScalarVolume = newScalar;
					OnPropertyChanged("Volume", "VolumeText");
				}
			}
		}
		public string VolumeText => Volume + " (" + (int)(ScalarVolume * 100f) + "%)";

		public AudioSessionElement(string name, float scalarVolume, MainWindow mainWindow) {
			Name = name;
			ScalarVolume = scalarVolume;
			MainWindow = mainWindow;
		}
	}
}
