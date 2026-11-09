using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Flatsoft.Common.UI;

namespace TopPos.Kiosk.UI
{
	public partial class App : Application
	{
		private KioskHost? _host;

		public override void Initialize() => AvaloniaXamlLoader.Load(this);

		public override void OnFrameworkInitializationCompleted()
		{
			if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
				// ViewModel-i iz jezgra javljaju promene i sa pozadinskih niti (v2); obaveštenja idu na UI nit.
				UiThread.Dispatcher = new AvaloniaUiDispatcher();

				var window = new MainWindow();
				desktop.MainWindow = window;
				var host = new KioskHost(desktop, window);
				_host = host;
				desktop.ShutdownRequested += (_, e) => host.OnShutdownRequested(e);
				desktop.Exit += (_, _) => host.CompleteShutdown();
				// Start tek kad lifetime pokrene petlju i prikaže prozor: rani izlaz (druga instanca, greška) iz ove metode bi zatvorio
				// prozor koji lifetime posle pokušava da prikaže.
				Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = host.StartAsync());
			}
			base.OnFrameworkInitializationCompleted();
		}

		/// <summary>Slika zastave jezika (<c>LanguageData.ImageResourceName</c>, crteži iz <c>Languages/&lt;id&gt;/&lt;id&gt;.axaml</c>).</summary>
		public static object? FindLanguageImage(string? resourceName)
			=> resourceName != null && Current!.TryFindResource(resourceName, out var resource) && resource is Drawing drawing
				? new DrawingImage(drawing)
				: null;
	}
}