using Avalonia.Controls;
using Avalonia.Interactivity;
using TopPos.Kiosk;
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