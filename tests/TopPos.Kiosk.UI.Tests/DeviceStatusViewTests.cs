using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Flatsoft.Common;
using TopPos.Kiosk.ViewModels;
using Xunit;

namespace TopPos.Kiosk.UI.Tests
{
	/// <summary>
	/// Status uređaja (dug pritisak na Home): dugme „Zatvori“ ostaje čitljivo kad je miš iznad njega i dok je pritisnuto.
	/// Fluent u tim stanjima menja pozadinu i tekst u šablonu; uređaj 25.11.2026: dugme je bilo crno sa crnim tekstom.
	/// </summary>
	public class DeviceStatusViewTests
	{
		[AvaloniaFact]
		public void Close_button_stays_readable_on_hover_and_press()
		{
			var languages = new LanguageManager(null);
			languages.LoadLanguageResources(Path.Combine(AppContext.BaseDirectory, "Languages"), ".lang", "sr");
			var vm = new DeviceStatusViewModel(null, null, null, languages, null) { CheckDelay = TimeSpan.Zero };
			Assert.True(vm.Initialize().IsCompleted);
			var window = new Window { Width = 1280, Height = 600, Content = new ContentControl { Content = vm } };
			window.Show();
			Dispatcher.UIThread.RunJobs();

			var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CloseButton");
			var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
			var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;

			AssertReadable(presenter, "bez miša");

			window.MouseMove(center);
			Dispatcher.UIThread.RunJobs();
			Assert.True(button.IsPointerOver);
			AssertReadable(presenter, "miš iznad");

			window.MouseDown(center, MouseButton.Left);
			Dispatcher.UIThread.RunJobs();
			Assert.True(button.IsPressed);
			AssertReadable(presenter, "pritisnuto");

			window.MouseUp(center, MouseButton.Left);
			Dispatcher.UIThread.RunJobs();
			Assert.True(vm.Closed.IsCompleted);
		}

		// Svetla pozadina i crn tekst (kao podrazumevano WPF dugme u v2).
		private static void AssertReadable(ContentPresenter presenter, string state)
		{
			var background = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color;
			var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Foreground).Color;
			Assert.True(background.A == 255 && background.R + background.G + background.B > 3 * 180, $"{state}: pozadina {background}");
			Assert.True(foreground == Colors.Black, $"{state}: tekst {foreground}");
		}
	}
}