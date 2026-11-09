using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Flatsoft.Common.UI;
using TopPos.Kiosk.UI.Controls;
using Xunit;

namespace TopPos.Kiosk.UI.Tests
{
	/// <summary>
	/// Home taster (v2 ButtonPressBehavior): kratak pritisak → Start, dug → prozor statusa; ugašen taster (plaćanje, vrata) ne
	/// okida ništa, ni kad se ugasi dok se drži.
	/// </summary>
	public class PressBehaviorTests
	{
		private const int __threshold = 150;

		/// <summary>Prag za testove u kojima dug pritisak ne sme slučajno da se okine (spora CI mašina između dva koraka).</summary>
		private const int __longThreshold = 2000;

		private sealed class Fixture
		{
			public int Short;
			public int Long;
			public readonly Button Button;
			public readonly Window Window;

			public Fixture(int threshold = __threshold)
			{
				Button = new Button { Width = 100, Height = 100, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
				PressBehavior.SetShortPressCommand(Button, new RelayCommand(_ => Short++));
				PressBehavior.SetLongPressCommand(Button, new RelayCommand(_ => Long++));
				PressBehavior.SetLongPressThreshold(Button, threshold);
				Window = new Window { Width = 300, Height = 300, Content = Button };
				Window.Show();
			}

			public void Down() => Window.MouseDown(new Point(50, 50), MouseButton.Left);
			public void Up() => Window.MouseUp(new Point(50, 50), MouseButton.Left);
		}

		/// <summary>Pušta UI nit da radi (tajmer dugog pritiska) zadato vreme.</summary>
		private static void Wait(int milliseconds)
		{
			var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
			while (DateTime.UtcNow < until) {
				Dispatcher.UIThread.RunJobs();
				Thread.Sleep(10);
			}
		}

		/// <summary>
		/// Pušta UI nit da radi dok uslov ne bude ispunjen (najviše 5 s): fiksno čekanje je na CI-ju (windows-latest)
		/// bilo prekratko za tajmer dugog pritiska.
		/// </summary>
		private static void WaitUntil(Func<bool> condition)
		{
			var until = DateTime.UtcNow.AddSeconds(5);
			while (!condition() && DateTime.UtcNow < until) {
				Dispatcher.UIThread.RunJobs();
				Thread.Sleep(10);
			}
		}

		[AvaloniaFact]
		public void Short_press_runs_short_command_only()
		{
			var f = new Fixture(__longThreshold);
			f.Down();
			Wait(30);
			f.Up();
			Wait(__threshold * 2);

			Assert.Equal(1, f.Short);
			Assert.Equal(0, f.Long);
		}

		[AvaloniaFact]
		public void Long_press_runs_long_command_and_release_does_not_run_short()
		{
			var f = new Fixture();
			f.Down();
			WaitUntil(() => f.Long > 0);
			Wait(__threshold);      // tajmer se ne okida ponovo
			f.Up();
			Wait(30);

			Assert.Equal(0, f.Short);
			Assert.Equal(1, f.Long);
		}

		[AvaloniaFact]
		public void Disabled_button_runs_nothing()
		{
			var f = new Fixture();
			f.Button.IsEnabled = false;
			f.Down();
			Wait(__threshold * 3);
			f.Up();
			Wait(30);

			Assert.Equal(0, f.Short);
			Assert.Equal(0, f.Long);
		}

		/// <summary>Home se gasi pri ulasku u plaćanje dok ga korisnik drži: ni dug ni kratak pritisak se ne okida.</summary>
		[AvaloniaFact]
		public void Button_disabled_while_held_runs_nothing()
		{
			var f = new Fixture(__longThreshold);
			f.Down();
			Wait(30);
			f.Button.IsEnabled = false;
			Wait(__longThreshold + 500);
			f.Up();
			Wait(30);

			Assert.Equal(0, f.Short);
			Assert.Equal(0, f.Long);
		}
	}
}