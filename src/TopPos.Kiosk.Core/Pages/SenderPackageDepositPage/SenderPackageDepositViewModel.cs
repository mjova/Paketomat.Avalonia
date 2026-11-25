using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Flatsoft.Common;
using Flatsoft.Common.UI;

namespace TopPos.Kiosk.ViewModels
{
	public class SenderPackageDepositViewModel : ViewModelBase, IWorkflowViewModel
	{
        public SenderPackageDepositViewModel(LanguageManager langManager, ILogger? logger)
			:base(langManager, logger)
		{
			this.ChangeBoxSizeCommand = new AsyncCommand<object?>(async (o, ct) => {
								_logger?.LogDebug($"{nameof(SenderPackageDepositViewModel)}: executing {nameof(ChangeBoxSizeCommand)}.");
								var handler = this.HandleChangeBoxClicked;
								if (handler != null) {
									// Prevent subsequent clicks (so that the handler code doesn't get executed multiple times). Leave Cancel/Logout button enabled.
									this.CanChangeBoxSize = false;
									await handler();
								}
								return ValueTask.CompletedTask;
							});

			this.CancelCommand = new AsyncCommand<object?>(async (o, ct) =>
				{
					_logger?.LogDebug($"{nameof(SenderPackageDepositViewModel)}: executing {nameof(CancelCommand)}.");
					var handler = this.HandleCancelClicked;	// pre odziva: pripada ekranu na kome je kliknuto
					await DelayForClickFeedback(ct);
					if (handler != null) {
						// Prevent subsequent clicks (so that the handler code doesn't get executed multiple times). Also disable ChangeBoxSize button, operation is cancelled.
						this.CanCancel = false;
						this.CanChangeBoxSize = false;
						await handler();
					}
					return null;
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

		public IAsyncCommand ChangeBoxSizeCommand { get; set; }
		private bool _canChangeBoxSize = true;
		public bool  CanChangeBoxSize
		{
			get => _canChangeBoxSize;
			set {
				if (_canChangeBoxSize != value) {
					SetProperty(ref _canChangeBoxSize, value);
				}
			}
		}
		public Func<ValueTask>? HandleChangeBoxClicked { get; set; }

		public ICommand CancelCommand { get; set; }
		private bool _canCancel = true;
		public bool  CanCancel
		{
			get => _canCancel;
			set {
				if (_canCancel != value) {
					SetProperty(ref _canCancel, value);
				}
			}
		}
		public Func<ValueTask>? HandleCancelClicked { get; set; }

		public void Activate() {
			_logger?.LogInformation($"{GetType().Name}: aktivacija.");
		}

		public void Deactivate() {
			_logger?.LogInformation($"{GetType().Name}: deaktivacija.");
			this.Barcode = null;
			// Reset state for next usage
			this.CanCancel = true;
			this.CanChangeBoxSize = true;
		}
	}
}
