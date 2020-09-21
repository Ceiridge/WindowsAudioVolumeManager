using System;
using System.Collections.Generic;
using System.Text;

namespace WindowsAudioVolumeManager {
	public class SavedAudioSession {
		public string Name { set; get; }
		public float ScalarVolume { set; get; }

		public SavedAudioSession() { }
		public SavedAudioSession(string name, float scalarVolume) {
			Name = name;
			ScalarVolume = scalarVolume;
		}
	}
}
