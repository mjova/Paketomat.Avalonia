using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TopPos.Kiosk.ViewModels;

namespace TopPos.Kiosk.UI.Views
{
	public partial class RemoteServiceView : UserControl
	{
		public RemoteServiceView()
		{
			InitializeComponent();
			// Svaki dodir na stranici produžava rok neaktivnosti servisne sesije (v2 PreviewMouseDown/PreviewTouchDown).
			AddHandler(PointerPressedEvent, (_, _) => (DataContext as RemoteServiceViewModel)?.Touch(), RoutingStrategies.Tunnel);
		}
	}
}