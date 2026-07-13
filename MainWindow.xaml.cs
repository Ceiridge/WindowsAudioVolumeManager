using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WindowsAudioVolumeManager.Interop;
using WindowsAudioVolumeManager.ViewModels;

namespace WindowsAudioVolumeManager {
	public partial class MainWindow : Window {
		private static readonly TimeSpan GoodbyeDuration = TimeSpan.FromSeconds(2);

		private readonly SettingsViewModel _settings;
		private bool _goodbyeShown;
		private bool _readyToClose;

		/// <summary>Assigned by the App right after construction (the manager needs the window handle).</summary>
		public HotkeyManager? HotkeyManager { get; set; }

		public MainWindow(MainViewModel viewModel, SettingsViewModel settings) {
			InitializeComponent();
			DataContext = viewModel;
			_settings = settings;
			Icon = AppIcon.ImageSource;
		}

		public void ShowFromTray() {
			Show();
			WindowState = WindowState.Normal;
			Activate();
		}

		/// <summary>Skip the goodbye screen (e.g. Windows is logging off) and close immediately.</summary>
		public void ForceClose() {
			_readyToClose = true;
			Close();
		}

		/// <summary>Shows a bare "Goodbye" for 2 seconds, then really closes the window (= exits the app).</summary>
		public void ShowGoodbyeAndClose() {
			if (_goodbyeShown) {
				return;
			}
			_goodbyeShown = true;

			Content = new TextBlock {
				Text = "Goodbye",
				FontSize = 48,
				FontWeight = FontWeights.Light,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			};

			ShowFromTray(); // make sure the farewell is actually visible, even from the tray

			DispatcherTimer timer = new() { Interval = GoodbyeDuration };
			timer.Tick += (_, _) => {
				timer.Stop();
				ForceClose();
			};
			timer.Start();
		}

		protected override void OnClosing(CancelEventArgs e) {
			if (!_readyToClose) {
				e.Cancel = true;
				// Show()/WindowState must not be touched while the window is still in its
				// closing handshake (VerifyNotClosing throws) — defer until the cancel took effect.
				Dispatcher.BeginInvoke(ShowGoodbyeAndClose);
				return;
			}

			base.OnClosing(e);
		}

		private void Window_StateChanged(object sender, EventArgs e) {
			// Minimizing hides the window as long as it stays reachable —
			// via the tray icon or via a successfully registered hotkey.
			if (WindowState == WindowState.Minimized
				&& (_settings.TrayIconEnabled || HotkeyManager?.IsRegistered == true)) {
				Hide();
			}
		}

		private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e) {
			if (e.Key == Key.Escape) {
				SearchBox.Clear();
				e.Handled = true;
			}
		}

		private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e) {
			e.Handled = true;

			Key key = e.Key == Key.System ? e.SystemKey : e.Key;
			if (IsModifierKey(key) || key == Key.Escape || key == Key.Tab) {
				return;
			}

			uint modifiers = 0;
			ModifierKeys pressed = Keyboard.Modifiers;
			if (pressed.HasFlag(ModifierKeys.Control)) {
				modifiers |= HotkeyManager.ModControl;
			}
			if (pressed.HasFlag(ModifierKeys.Shift)) {
				modifiers |= HotkeyManager.ModShift;
			}
			if (pressed.HasFlag(ModifierKeys.Alt)) {
				modifiers |= HotkeyManager.ModAlt;
			}
			if (pressed.HasFlag(ModifierKeys.Windows)) {
				modifiers |= HotkeyManager.ModWin;
			}

			// Require a real modifier so a bare letter key is never grabbed globally.
			if ((modifiers & (HotkeyManager.ModControl | HotkeyManager.ModAlt | HotkeyManager.ModWin)) == 0) {
				return;
			}

			_settings.SetHotkey(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
		}

		private static bool IsModifierKey(Key key) =>
			key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
				or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
	}
}
