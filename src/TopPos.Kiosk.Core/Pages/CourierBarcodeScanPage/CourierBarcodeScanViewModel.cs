using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Flatsoft.Common;
using Flatsoft.Common.UI;

namespace TopPos.Kiosk.ViewModels
{
	public class CourierBarcodeScanViewModel : ViewModelBase, IWorkflowViewModel
	{
        public CourierBarcodeScanViewModel(LanguageManager langManager, ILogger? logger)
			:base(langManager, logger)
		{
			this.CancelCommand = new AsyncCommand<bool>(async (o, ct) => {
					_logger?.LogDebug($"{nameof(CourierBarcodeScanViewModel)}: executing {nameof(CancelCommand)}.");
					// v4: odustajanje odmah prekida i zahtev za dodelu vrata koji je u toku — odgovor servera u 200 ms odziva klika je
					// ranije otvarao vrata posle klika (odustajanje se zatim odbacivalo).
					var handler = this.HandleCancelClicked;
					if (handler != null) {
						await handler();
					}
					return true;
				});
		}

		private string? _barcode;
        public string? Barcode 
		{ 
			get => _barcode; 
			set
			{
				SetProperty(ref _barcode, value);	
			} 
		}

        private bool _packagePickup;
        public bool PackagePickup 
		{ 
			get => _packagePickup; 
			set
			{
				SetProperty(ref _packagePickup, value);
				OnPropertyChanged(nameof(PackageDeposit));
			}
		}
        public bool PackageDeposit => !_packagePickup;

		public ICommand CancelCommand { get; set; }

		public Func<ValueTask>? HandleCancelClicked { get; set; }

		public void Activate() {
			_logger?.LogInformation($"{GetType().Name}: aktivacija.");
		}

		public void Deactivate() {
			_logger?.LogInformation($"{GetType().Name}: deaktivacija.");
			_barcode = null;
		}
	}
}
