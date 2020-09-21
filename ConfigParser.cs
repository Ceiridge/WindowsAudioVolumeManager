using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WindowsAudioVolumeManager {
	public class ConfigParser {
		const string configFile = "volumeConfig.json";

		public ConfigObject SavedConfigObject;
		private MainWindow Window;

		public ConfigParser(MainWindow window) {
			Window = window;
		}

		public void LoadConfig() {
			if (File.Exists(configFile)) {
				string jsonString = File.ReadAllText(configFile);
				SavedConfigObject = JsonSerializer.Deserialize<ConfigObject>(jsonString);

				Window.DefaultSessionElement.ScalarVolume = SavedConfigObject.DefaultVolumeScalar;
				Window.SavedSessions = SavedConfigObject.SavedSessions;
			}
		}

		public void SaveConfig() {
			File.WriteAllText(configFile, JsonSerializer.Serialize(SavedConfigObject));
		}

		public class ConfigObject {
			public float DefaultVolumeScalar { get; set; }
			public List<SavedAudioSession> SavedSessions { get; set; }
		}
	}
}
