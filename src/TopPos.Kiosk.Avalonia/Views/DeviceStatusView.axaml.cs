using Avalonia.Controls;

namespace TopPos.Kiosk.UI.Views
{
	public partial class DeviceStatusView : UserControl
	{
		public DeviceStatusView()
		{
			InitializeComponent();
			// Fokus prelazi sa Home (kiosk ispod je ugašen) na „Zatvori“, kao u v2 modalnom prozoru.
			Loaded += (_, _) => CloseButton.Focus();
		}
	}
}