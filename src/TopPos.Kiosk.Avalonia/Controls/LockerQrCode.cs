using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Flatsoft.Common.UI;

namespace TopPos.Kiosk.UI.Controls
{
	/// <summary>
	/// QR kod sa identifikacijom paketomata (npr. „XP000111“) i čitljiv tekst ispod (v2 Controls/LockerQrCode). Prazan
	/// <see cref="Code"/> ili neuspelo generisanje sakriva kontrolu — QR je informativan.
	/// </summary>
	public sealed class LockerQrCode : StackPanel
	{
		public static readonly StyledProperty<string?> CodeProperty = AvaloniaProperty.Register<LockerQrCode, string?>(nameof(Code));
		public static readonly StyledProperty<double> QrSizeProperty = AvaloniaProperty.Register<LockerQrCode, double>(nameof(QrSize), 110);
		public static readonly StyledProperty<double> LabelFontSizeProperty = AvaloniaProperty.Register<LockerQrCode, double>(nameof(LabelFontSize), 14);

		private readonly Image _image = new() { Stretch = Stretch.Uniform };
		private readonly TextBlock _label = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), FontFamily = new FontFamily("monospace") };

		public LockerQrCode()
		{
			RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.None);
			_image.Width = _image.Height = QrSize;      // podrazumevane vrednosti ne prolaze kroz OnPropertyChanged
			_label.FontSize = LabelFontSize;
			Children.Add(new Border { Background = Brushes.White, Padding = new Thickness(6), CornerRadius = new CornerRadius(4), Child = _image });
			Children.Add(_label);
			IsVisible = false;
		}

		public string? Code { get => GetValue(CodeProperty); set => SetValue(CodeProperty, value); }
		public double QrSize { get => GetValue(QrSizeProperty); set => SetValue(QrSizeProperty, value); }
		public double LabelFontSize { get => GetValue(LabelFontSizeProperty); set => SetValue(LabelFontSizeProperty, value); }

		protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
		{
			base.OnPropertyChanged(change);
			if (change.Property == CodeProperty) {
				Render(Code);
			}
			else if (change.Property == QrSizeProperty) {
				_image.Width = _image.Height = QrSize;
			}
			else if (change.Property == LabelFontSizeProperty) {
				_label.FontSize = LabelFontSize;
			}
		}

		private void Render(string? code)
		{
			_label.Text = code;
			if (string.IsNullOrWhiteSpace(code)) {
				_image.Source = null;
				IsVisible = false;
				return;
			}
			try {
				_image.Source = new Bitmap(new MemoryStream(QrCodeService.GenerateQRCode(code)));
				IsVisible = true;
			}
			catch {
				_image.Source = null;
				IsVisible = false;
			}
		}
	}
}