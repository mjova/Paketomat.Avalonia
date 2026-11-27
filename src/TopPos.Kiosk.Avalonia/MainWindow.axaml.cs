using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TopPos.Kiosk;
using TopPos.Kiosk.UI.Controls;
using TopPos.Kiosk.ViewModels;

namespace TopPos.Kiosk.UI
{
	public partial class MainWindow : Window
	{
		public MainWindow()
		{
			InitializeComponent();
			// Zvuk klika na svako dugme (v2: WPF Button.ClickEvent handler na glavnom prozoru).
			AddHandler(Button.ClickEvent, (_, _) => (DataContext as MainWindowViewModel)?.OnButtonPressed(), RoutingStrategies.Bubble, handledEventsToo: true);
		}

		/// <summary>Layout po veličini prozora (v2 <c>OnRenderSizeChanged</c>); nasleđuju ga glavni prozor i stranice.</summary>
		protected override void OnSizeChanged(SizeChangedEventArgs e)
		{
			base.OnSizeChanged(e);
			LayoutContent.SetScreenLayout(this, ScreenLayout.FromSize(e.NewSize.Width, e.NewSize.Height));
		}

		/// <summary>
		/// Samo DEBUG (dugme <c>Header.Rotate</c>, v2 <c>ToggleDisplayOrientation</c>): zamenjuje širinu i visinu prozora i
		/// centrira ga na ekranu, za proveru layouta bez rotacije ekrana. Prozor preko celog ekrana prvo postaje običan
		/// (Avalonia ne menja veličinu prozora u FullScreen režimu). Prozor viši ili širi od radne površine se poravnava sa
		/// njenim gornjim, odnosno levim rubom, da zaglavlje (i ovo dugme) ostane na ekranu.
		/// </summary>
		public void ToggleDisplayOrientation()
		{
			var size = ClientSize;
			WindowState = WindowState.Normal;
			Width = size.Height;
			Height = size.Width;
			if (Screens.ScreenFromWindow(this) is { } screen) {
				var area = screen.WorkingArea;
				var scaling = screen.Scaling;
				Position = new PixelPoint(area.X + Math.Max(0, (int) ((area.Width - Width * scaling) / 2)),
										  area.Y + Math.Max(0, (int) ((area.Height - Height * scaling) / 2)));
			}
		}

		/// <summary>Poruka umesto kioska (start, neuspela inicijalizacija).</summary>
		public void ShowStartupMessage(string message)
		{
			StartupMessage.Text = message;
			StartupMessage.IsVisible = true;
			Kiosk.IsVisible = false;
		}

		/// <summary>
		/// Kiosk spreman: ViewModel i veličina prozora. <paramref name="screenResolution"/> (samo Debug,
		/// app-configuration.ScreenResolution.json) daje prozor zadate veličine za razvoj; inače ceo ekran.
		/// </summary>
		public void Attach(MainWindowViewModel viewModel, ScreenResolutionConfiguration? screenResolution)
		{
			DataContext = viewModel;
			StartupMessage.IsVisible = false;
			Kiosk.IsVisible = true;
			if (screenResolution != null) {
				WindowState = WindowState.Normal;
				Width = screenResolution.Width;
				Height = screenResolution.Height;
			}
			else {
				WindowState = WindowState.FullScreen;
			}
		}
	}
}