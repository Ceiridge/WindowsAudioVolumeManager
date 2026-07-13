using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WindowsAudioVolumeManager {
	/// <summary>
	/// Renders the application icon (a speaker glyph on a rounded blue square) at
	/// runtime, so no image asset needs to be shipped. Used for the window icon
	/// (WPF ImageSource) and the tray icon (System.Drawing.Icon; H.NotifyIcon does
	/// not accept a RenderTargetBitmap as IconSource).
	/// </summary>
	public static class AppIcon {
		private const int Size = 32;

		private static ImageSource? _imageSource;
		private static System.Drawing.Icon? _trayIcon;

		public static ImageSource ImageSource => _imageSource ??= Render();

		public static System.Drawing.Icon TrayIcon => _trayIcon ??= CreateIcon((BitmapSource)ImageSource);

		private static ImageSource Render() {
			DrawingVisual visual = new();

			using (DrawingContext context = visual.RenderOpen()) {
				context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x6F, 0xC8)), null,
					new Rect(0, 0, Size, Size), 6, 6);

				FormattedText glyph = new("🔊", CultureInfo.InvariantCulture,
					FlowDirection.LeftToRight, new Typeface("Segoe UI Emoji"), 18, Brushes.White, 1.0);
				context.DrawText(glyph, new Point((Size - glyph.Width) / 2, (Size - glyph.Height) / 2));
			}

			RenderTargetBitmap bitmap = new(Size, Size, 96, 96, PixelFormats.Pbgra32);
			bitmap.Render(visual);
			bitmap.Freeze();
			return bitmap;
		}

		/// <summary>Wraps the rendered bitmap as PNG inside a single-image .ico container.</summary>
		private static System.Drawing.Icon CreateIcon(BitmapSource bitmap) {
			using MemoryStream pngStream = new();
			PngBitmapEncoder encoder = new();
			encoder.Frames.Add(BitmapFrame.Create(bitmap));
			encoder.Save(pngStream);
			byte[] png = pngStream.ToArray();

			using MemoryStream icoStream = new();
			using (BinaryWriter writer = new(icoStream, System.Text.Encoding.UTF8, leaveOpen: true)) {
				writer.Write((ushort)0); // reserved
				writer.Write((ushort)1); // type: icon
				writer.Write((ushort)1); // image count
				writer.Write((byte)Size); // width
				writer.Write((byte)Size); // height
				writer.Write((byte)0); // color count (0 = true color)
				writer.Write((byte)0); // reserved
				writer.Write((ushort)1); // color planes
				writer.Write((ushort)32); // bits per pixel
				writer.Write(png.Length); // image data size
				writer.Write(6 + 16); // image data offset
				writer.Write(png);
			}

			icoStream.Position = 0;
			return new System.Drawing.Icon(icoStream);
		}
	}
}
