using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TopPos.Kiosk.UI.Controls;
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

		/// <summary>
		/// Jedan prikaz za sve ekrane (v2 jedan šablon): na malom ekranu (600×1024) klasa <c>small</c> smanjuje dugmad dole.
		/// </summary>
		protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
		{
			base.OnPropertyChanged(change);
			if (change.Property == LayoutContent.ScreenLayoutProperty) {
				Classes.Set("small", LayoutContent.GetScreenLayout(this).Size == ScreenSize.Small);
			}
		}
	}
}