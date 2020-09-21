using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace WindowsAudioVolumeManager {
	public class ConfigParser {
		const string configFile = "volumeConfig.json";

		public ConfigObject SavedConfigObject;
		public bool Dirty = false;
		private readonly MainWindow Window;

		public ConfigParser(MainWindow window) {
			Window = window;
		}

		public void LoadConfig() {
			if (File.Exists(configFile)) {
				string jsonString = File.ReadAllText(configFile);
				SavedConfigObject = JsonSerializer.Deserialize<ConfigObject>(jsonString);

				Window.DefaultSessionElement.ScalarVolume = SavedConfigObject.DefaultVolumeScalar;
				Window.HasModifiedDefaultSession = true;
				Window.SavedSessions = SavedConfigObject.SavedSessions;
			}
		}

		public void SaveConfig() {
			if(SavedConfigObject == null) {
				SavedConfigObject = new ConfigObject();
				SavedConfigObject.DefaultVolumeScalar = Window.DefaultSessionElement.ScalarVolume;
				SavedConfigObject.SavedSessions = Window.SavedSessions;
			}

			File.WriteAllText(configFile, JsonSerializer.Serialize(SavedConfigObject));
		}

		public void DirtySaverThread() {
			while (true) {
				Thread.Sleep(1000);

				try {
					if (Dirty) {
						SaveConfig();
						Dirty = false;
					}
				} catch (Exception e) {
					Console.WriteLine(e);
				}
			}
		}

		public class ConfigObject {
			public float DefaultVolumeScalar { get; set; }
			public List<SavedAudioSession> SavedSessions { get; set; }
		}
	}
}
