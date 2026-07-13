using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WindowsAudioVolumeManager.Interop {
	/// <summary>
	/// Registers the global show-window hotkey. Unlike the legacy implementation this
	/// creates the window handle deterministically (no 5-second sleep), checks the
	/// RegisterHotKey result, and supports live re-registration when the user picks
	/// a different hotkey in the settings.
	/// </summary>
	public class HotkeyManager : IDisposable {
		public const uint DefaultModifiers = ModControl | ModShift;
		public const uint DefaultKey = 0x56; // V

		public const uint ModAlt = 0x1;
		public const uint ModControl = 0x2;
		public const uint ModShift = 0x4;
		public const uint ModWin = 0x8;
		private const uint ModNoRepeat = 0x4000;

		private const int WmHotkey = 0x0312;
		private const int HotkeyId = 0x1D6E;

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
		[DllImport("user32.dll")]
		private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

		public event Action? HotkeyPressed;

		/// <summary>True while a hotkey is successfully registered with Windows.</summary>
		public bool IsRegistered => _registered;

		private readonly IntPtr _handle;
		private readonly HwndSource _source;
		private bool _registered;
		private bool _disposed;

		public HotkeyManager(Window window) {
			_handle = new WindowInteropHelper(window).EnsureHandle();
			_source = HwndSource.FromHwnd(_handle)!;
			_source.AddHook(WndProcHook);
		}

		/// <summary>Returns false if the hotkey is taken by another application.</summary>
		public bool Register(uint modifiers, uint virtualKey) {
			Unregister();
			_registered = RegisterHotKey(_handle, HotkeyId, modifiers | ModNoRepeat, virtualKey);
			return _registered;
		}

		public void Unregister() {
			if (_registered) {
				UnregisterHotKey(_handle, HotkeyId);
				_registered = false;
			}
		}

		private IntPtr WndProcHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) {
			if (message == WmHotkey && wParam.ToInt32() == HotkeyId) {
				HotkeyPressed?.Invoke();
				handled = true;
			}

			return IntPtr.Zero;
		}

		public static string Describe(uint modifiers, uint virtualKey) {
			StringBuilder text = new();

			if ((modifiers & ModControl) != 0) {
				text.Append("Ctrl + ");
			}
			if ((modifiers & ModShift) != 0) {
				text.Append("Shift + ");
			}
			if ((modifiers & ModAlt) != 0) {
				text.Append("Alt + ");
			}
			if ((modifiers & ModWin) != 0) {
				text.Append("Win + ");
			}

			Key key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
			text.Append(key == Key.None ? "?" : key.ToString());
			return text.ToString();
		}

		public void Dispose() {
			if (_disposed) {
				return;
			}
			_disposed = true;

			Unregister();
			_source.RemoveHook(WndProcHook);
			GC.SuppressFinalize(this);
		}
	}
}
