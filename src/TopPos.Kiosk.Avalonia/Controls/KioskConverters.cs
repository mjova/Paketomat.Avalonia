using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using TopPos.Kiosk.ViewModels;

namespace TopPos.Kiosk.UI.Controls
{
	/// <summary>Konverteri prikaza (v2: WPF konverteri iz Flatsoft.Common.UI i resursi slika).</summary>
	public static class KioskConverters
	{
		/// <summary><c>bool?</c> → <c>true</c> samo za <c>true</c> (v2 NullableBooleanConverter: prozor je ugašen dok se ne zna).</summary>
		public static readonly IValueConverter IsTrue = new FuncValueConverter<bool?, bool>(value => value == true);

		/// <summary>PNG bajtovi (QR fiskalnog računa) → slika.</summary>
		public static readonly IValueConverter PngToBitmap = new FuncValueConverter<byte[]?, Bitmap?>(
			bytes => bytes is { Length: > 0 } ? new Bitmap(new MemoryStream(bytes)) : null);

		/// <summary>Vrsta poruke → ikona (crteži ErrorDI, SuccessDI, InformationDI iz Resources/*.axaml).</summary>
		public static readonly IValueConverter MessageIcon = new FuncValueConverter<MessageType?, IImage?>(kind => {
			if (kind == null) {
				return null;
			}
			string key = kind switch {
				MessageType.Success => "SuccessDI",
				MessageType.Error => "ErrorDI",
				_ => "InformationDI"
			};
			return Application.Current!.TryFindResource(key, out var image) ? image as IImage : null;
		});
	}

	/// <summary>Konverter iz lambde (Avalonia <c>FuncValueConverter</c> za tipove koji mogu biti null).</summary>
	internal sealed class FuncValueConverter<TIn, TOut>(Func<TIn?, TOut> convert) : IValueConverter
	{
		public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
			=> convert(value is TIn typed ? typed : default);

		public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
			=> throw new NotSupportedException();
	}
}