using Avalonia;

namespace TopPos.Kiosk.UI
{
	internal static class Program
	{
		// Pre AppMain-a ne koristiti Avalonia API ni SynchronizationContext.
		[STAThread]
		public static int Main(string[] args)
			=> BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnMainWindowClose);

		// Koristi ga i dizajner.
		public static AppBuilder BuildAvaloniaApp()
			=> AppBuilder.Configure<App>()
						 .UsePlatformDetect()
						 .WithInterFont()
						 .LogToTrace();
	}
}