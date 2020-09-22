using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace WindowsAudioVolumeManager {
	public class HotkeyRegistrar {
		[DllImport("user32.dll")]
		private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
		[DllImport("user32.dll")]
		private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

		public event EventHandler OnHotkeyPressed;
		private IntPtr Handle;
		private int HotKeyId;
		private HwndSource hwndSource;

		public HotkeyRegistrar(IntPtr handle, int hotKeyId) {
			Handle = handle;
			HotKeyId = hotKeyId;
			hwndSource = HwndSource.FromHwnd(Handle);
		}

		public void RegisterHotkey(uint modifier, uint vk) {
			RegisterHotKey(Handle, HotKeyId, modifier, vk);
			hwndSource.AddHook(HwndHook);
		}

		public void UnregisterHotkey() {
			UnregisterHotKey(Handle, HotKeyId);
			hwndSource.RemoveHook(HwndHook);
		}

		private IntPtr HwndHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) {
			if (message == 0x312 && wParam.ToInt32() == HotKeyId) { // WM_HOTKEY
				OnHotkeyPressed?.Invoke(this, null);
				handled = true;
			}

			return IntPtr.Zero;
		}


		public enum HotkeyModifier : uint {
			MOD_ALT = 0x1,
			MOD_CONTROL = 0x2,
			MOD_NOREPEAT = 0x4000,
			MOD_SHIFT = 0x4,
			MOD_WIN = 0x8
		}
	}
}
