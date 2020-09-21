namespace WindowsAudioVolumeManager {
	class AudioSessionElement {
		public string Name { get; set; }
		public int Volume { get; set; }

		public AudioSessionElement(string name, int volume) {
			Name = name;
			Volume = volume;
		}
	}
}
