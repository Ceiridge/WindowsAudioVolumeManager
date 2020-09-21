using System.ComponentModel;

namespace WindowsAudioVolumeManager {
	abstract class ObservablePropertyObject : INotifyPropertyChanged {
		public event PropertyChangedEventHandler PropertyChanged;

		protected void OnPropertyChanged(params string[] propertyNames) {
			foreach (string propertyName in propertyNames) {
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
			}
		}
	}
}
