using Avalonia.Controls;
using TopPos.Kiosk.ViewModels;

namespace TopPos.Kiosk.UI.Controls
{
	public partial class NumericKeypad : UserControl
	{
		public NumericKeypad()
		{
			InitializeComponent();
			// Grafički simboli (✕/✓) na svim veličinama ekrana (v2 SyncGraphicalKeysWithLayout).
			DataContextChanged += (_, _) => {
				if (DataContext is NumericKeypadViewModel viewModel) {
					viewModel.UseGraphicalKeys = true;
				}
			};
		}
	}
}