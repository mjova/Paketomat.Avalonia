using Avalonia;
using Avalonia.Headless;
using TopPos.Kiosk.UI;

[assembly: AvaloniaTestApplication(typeof(TopPos.Kiosk.UI.Tests.TestApp))]

namespace TopPos.Kiosk.UI.Tests
{
	/// <summary>
	/// Prava aplikacija (stilovi, resursi, View-ovi), bez ekrana. Bez desktop lifetime-a App ne pravi KioskHost (ni uređaje
	/// ni server). Skia crta stvarne slike, pa se ekrani mogu i snimiti.
	/// </summary>
	public static class TestApp
	{
		public static AppBuilder BuildAvaloniaApp()
			=> AppBuilder.Configure<App>()
						 .UseSkia()
						 .WithInterFont()
						 .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
	}
}