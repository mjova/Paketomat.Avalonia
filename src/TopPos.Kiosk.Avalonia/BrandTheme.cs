using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace TopPos.Kiosk.UI
{
	/// <summary>
	/// Tema brenda iz v2 formata (<c>Brands/&lt;Brand&gt;/theme.xaml</c>: <c>SolidColorBrush</c> sa <c>x:Key</c> i <c>Color</c>,
	/// <c>ImageBrush</c> sa <c>ImageSource</c>), bez učitavanja XAML-a u toku rada: brendovi iz v2 se prenose nepromenjeni.
	/// Boje postaju resursi aplikacije (<c>DynamicResource</c> u prikazu), slika <c>ApplicationLogo</c> je <see cref="Bitmap"/>.
	/// </summary>
	internal static class BrandTheme
	{
		private static readonly XNamespace __x = "http://schemas.microsoft.com/winfx/2006/xaml";

		public static void Apply(string brandDirectory, IResourceDictionary resources)
		{
			var document = XDocument.Load(Path.Combine(brandDirectory, "theme.xaml"));
			foreach (var element in document.Root!.Elements()) {
				var key = (string?) element.Attribute(__x + "Key");
				if (key == null) {
					continue;
				}
				switch (element.Name.LocalName) {
					case "SolidColorBrush":
						resources[key] = new SolidColorBrush(Color.Parse((string) element.Attribute("Color")!));
						break;
					case "ImageBrush":
						resources[key] = new Bitmap(Path.Combine(brandDirectory, (string) element.Attribute("ImageSource")!));
						break;
					default:
						throw new FormatException($"theme.xaml: nepoznat element {element.Name.LocalName} ({key}).");
				}
			}
		}
	}
}