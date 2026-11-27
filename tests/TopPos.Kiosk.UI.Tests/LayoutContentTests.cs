using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using TopPos.Kiosk.UI.Controls;
using TopPos.Kiosk.UI.Views;
using Xunit;

namespace TopPos.Kiosk.UI.Tests
{
	/// <summary>
	/// Izbor layouta (v2 <c>LayoutState</c> i <c>LayoutTemplateSelector</c>): pravila veličine prozora, padanje na drugi
	/// šablon i promena layouta dok je sadržaj prikazan.
	/// </summary>
	public class LayoutContentTests
	{
		[Theory]
		[InlineData(1280, 800, false, ScreenSize.Large)]    // 12L iz v2 konfiguracije
		[InlineData(1024, 768, false, ScreenSize.Medium)]   // 10L
		[InlineData(600, 1024, true, ScreenSize.Small)]     // 07P: odlučuje širina, ne visina
		[InlineData(800, 1280, true, ScreenSize.Large)]
		[InlineData(768, 1024, true, ScreenSize.Medium)]
		[InlineData(800, 600, false, ScreenSize.Small)]
		[InlineData(1000, 699, false, ScreenSize.Small)]
		[InlineData(1000, 700, false, ScreenSize.Medium)]
		[InlineData(1000, 790, false, ScreenSize.Medium)]
		[InlineData(1000, 791, false, ScreenSize.Large)]
		[InlineData(800, 800, false, ScreenSize.Large)]     // kvadrat nije uspravan
		public void Layout_follows_v2_rules_for_window_size(double width, double height, bool portrait, ScreenSize size)
			=> Assert.Equal(new ScreenLayout(portrait, size), ScreenLayout.FromSize(width, height));

		public static TheoryData<bool, ScreenSize> AllLayouts => new() {
			{ false, ScreenSize.Large }, { false, ScreenSize.Medium }, { false, ScreenSize.Small },
			{ true, ScreenSize.Large }, { true, ScreenSize.Medium }, { true, ScreenSize.Small }
		};

		[Theory]
		[MemberData(nameof(AllLayouts))]
		public void Each_layout_gets_its_own_template_when_all_six_exist(bool portrait, ScreenSize size)
		{
			var layout = new ScreenLayout(portrait, size);

			Assert.Equal(layout.ToString(), LayoutContent.Select(layout, "LandscapeLarge", "LandscapeMedium", "LandscapeSmall",
																	"PortraitLarge", "PortraitMedium", "PortraitSmall"));
		}

		// v2 SenderBarcodeScanPage i SenderPackageDepositPage: samo Landscape, LandscapeMedium i PortraitSmall.
		[Theory]
		[InlineData(false, ScreenSize.Large, "LandscapeLarge")]
		[InlineData(false, ScreenSize.Medium, "LandscapeMedium")]
		[InlineData(false, ScreenSize.Small, "LandscapeMedium")]    // v2: prazno (null iz selektora)
		[InlineData(true, ScreenSize.Large, "PortraitSmall")]       // v2: prazno
		[InlineData(true, ScreenSize.Medium, "PortraitSmall")]      // v2: isto (Medium → Small)
		[InlineData(true, ScreenSize.Small, "PortraitSmall")]
		public void Missing_template_falls_back_to_closest_of_same_orientation(bool portrait, ScreenSize size, string expected)
			=> Assert.Equal(expected, LayoutContent.Select(new ScreenLayout(portrait, size), "LandscapeLarge", "LandscapeMedium", null, null, null, "PortraitSmall"));

		[Theory]
		[MemberData(nameof(AllLayouts))]
		public void Single_template_is_used_for_every_layout(bool portrait, ScreenSize size)
			=> Assert.Equal("LandscapeLarge", LayoutContent.Select(new ScreenLayout(portrait, size), "LandscapeLarge", null, null, null, null, null));

		[Fact]
		public void Without_templates_nothing_is_selected()
			=> Assert.Null(LayoutContent.Select<string>(ScreenLayout.LandscapeLarge, null, null, null, null, null, null));

		[AvaloniaFact]
		public void Layout_change_rebuilds_content_with_the_same_view_model()
		{
			var viewModel = new object();
			var content = new LayoutContent {
				Content = viewModel,
				LandscapeLargeTemplate = new FuncDataTemplate<object>((_, _) => new Border()),
				PortraitSmallTemplate = new FuncDataTemplate<object>((_, _) => new TextBlock())
			};
			var window = new Window { Width = 1280, Height = 800, Content = content };
			window.Show();
			Dispatcher.UIThread.RunJobs();
			Assert.IsType<Border>(content.Presenter?.Child);

			LayoutContent.SetScreenLayout(window, new ScreenLayout(true, ScreenSize.Small));
			Dispatcher.UIThread.RunJobs();

			var child = Assert.IsType<TextBlock>(content.Presenter?.Child);
			Assert.Same(viewModel, child.DataContext);
		}

		[AvaloniaFact]
		public void Main_window_sets_layout_from_its_size()
		{
			var window = new MainWindow { Width = 600, Height = 1024 };
			window.Show();
			Dispatcher.UIThread.RunJobs();
			Assert.Equal(new ScreenLayout(true, ScreenSize.Small), LayoutContent.GetScreenLayout(window));

			window.ToggleDisplayOrientation();
			Dispatcher.UIThread.RunJobs();

			Assert.Equal(new ScreenLayout(false, ScreenSize.Small), LayoutContent.GetScreenLayout(window));
			window.Width = 1024;
			window.Height = 768;
			Dispatcher.UIThread.RunJobs();
			Assert.Equal(new ScreenLayout(false, ScreenSize.Medium), LayoutContent.GetScreenLayout(window));
		}

		/// <summary>Ime v2 layout fajla bez prefiksa: Landscape Large je „Landscape“, Portrait Large „Portrait“.</summary>
		private static string V2Suffix(ScreenLayout layout)
			=> layout.Size == ScreenSize.Large ? (layout.IsPortrait ? "Portrait" : "Landscape") : layout.ToString();

		/// <summary>Prikaz koji <paramref name="content"/> pravi za <paramref name="layout"/> (preko ContentTemplate, kao u radu).</summary>
		private static Control BuildFor(LayoutContent content, ScreenLayout layout)
		{
			LayoutContent.SetScreenLayout(content, layout);
			return content.ContentTemplate!.Build(null)!;
		}

		[AvaloniaTheory]
		[MemberData(nameof(AllLayouts))]
		public void Main_window_shows_v2_layout_for_each_screen(bool portrait, ScreenSize size)
		{
			var layout = new ScreenLayout(portrait, size);
			var content = new MainWindow().GetLogicalDescendants().OfType<LayoutContent>().Single();

			Assert.Equal($"MainWindow{V2Suffix(layout)}Layout", BuildFor(content, layout).GetType().Name);
		}

		public static TheoryData<string> MainWindowLayouts => new() {
			nameof(MainWindowLandscapeLayout), nameof(MainWindowLandscapeMediumLayout), nameof(MainWindowLandscapeSmallLayout),
			nameof(MainWindowPortraitLayout), nameof(MainWindowPortraitMediumLayout), nameof(MainWindowPortraitSmallLayout)
		};

		// Bez MainWindowViewModel-a (nema okruženja za njega): XAML i resursi layouta (crtež rotacije, logo) se učitavaju i crtaju.
		[AvaloniaTheory]
		[MemberData(nameof(MainWindowLayouts))]
		public void Main_window_layout_loads_and_renders(string name)
		{
			var layout = (Control) Activator.CreateInstance(typeof(MainWindowLandscapeLayout).Assembly.GetType($"TopPos.Kiosk.UI.Views.{name}", throwOnError: true)!)!;
			var window = new Window { Width = 1280, Height = 800, Content = layout };
			window.Show();
			Dispatcher.UIThread.RunJobs();

			Assert.NotNull(window.CaptureRenderedFrame());
		}
	}
}