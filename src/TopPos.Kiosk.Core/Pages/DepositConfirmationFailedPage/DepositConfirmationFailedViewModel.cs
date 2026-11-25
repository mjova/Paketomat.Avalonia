using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Flatsoft.Common;
using Flatsoft.Common.UI;
using TopPos.Service;

namespace TopPos.Kiosk.ViewModels
{
	/// <summary>
	/// Ekran posle neuspele potvrde ostavljanja (kurir i pošiljalac). Dugmad zavise od ishoda: „Ponovi slanje" kad server nije
	/// odbio potvrdu; „Prekini i preuzmi paket" samo kad server sigurno nije upisao ostavljanje; „Ostavi paket i nastavi" kad
	/// se ne zna da li ga je upisao (paket se tada ne vraća — kupac bi mogao da otvori prazna vrata).
	/// </summary>
	public class DepositConfirmationFailedViewModel : ViewModelBase, IWorkflowViewModel
	{
		private DepositConfirmResult _result = DepositConfirmResult.NotDelivered;
		private string? _serverMessage;

		public DepositConfirmationFailedViewModel(LanguageManager langManager, ILogger? logger)
			:base(langManager, logger)
		{
			this.RetryCommand = CreateCommand(nameof(RetryCommand), () => this.HandleRetryClicked);
			this.ReturnPackageCommand = CreateCommand(nameof(ReturnPackageCommand), () => this.HandleReturnPackageClicked);
			this.LeavePackageCommand = CreateCommand(nameof(LeavePackageCommand), () => this.HandleLeavePackageClicked);
		}

		private AsyncCommand<object?> CreateCommand(string name, Func<Func<ValueTask>?> handler)
			=> new(async (o, ct) => {
					_logger?.LogDebug($"{nameof(DepositConfirmationFailedViewModel)}: executing {name}.");
					await InvokeAfterClickFeedback(handler(), ct);
					return null;
				});

		public ICommand RetryCommand { get; }

		public ICommand ReturnPackageCommand { get; }

		public ICommand LeavePackageCommand { get; }

		/// <summary>Ishod poslednjeg slanja i poruka servera (samo uz odbijanje); menja tekst i dugmad.</summary>
		public void SetResult(DepositConfirmResult result, string? serverMessage)
		{
			_result = result;
			_serverMessage = serverMessage;
			OnPropertyChanged(nameof(Message));
			OnPropertyChanged(nameof(CanRetry));
			OnPropertyChanged(nameof(CanReturnPackage));
			OnPropertyChanged(nameof(CanLeavePackage));
		}

		public string Message => _result switch {
			DepositConfirmResult.Rejected when !string.IsNullOrWhiteSpace(_serverMessage)
				=> this["DepositConfirmationFailedPage.Rejected.ServerMessage.Template"].Replace("{serverMessage}", _serverMessage.Trim().TrimEnd('.')),
			DepositConfirmResult.Rejected => this["DepositConfirmationFailedPage.Rejected"],
			DepositConfirmResult.Unknown => this["DepositConfirmationFailedPage.Unknown"],
			_ => this["DepositConfirmationFailedPage.NotDelivered"],
		};

		public bool CanRetry => _result is DepositConfirmResult.NotDelivered or DepositConfirmResult.Unknown;

		public bool CanReturnPackage => _result is DepositConfirmResult.NotDelivered or DepositConfirmResult.Rejected;

		public bool CanLeavePackage => _result == DepositConfirmResult.Unknown;

		protected override void OnLanguageChanged()
		{
			base.OnLanguageChanged();
			OnPropertyChanged(nameof(Message));
		}

		public void Activate() {
			_logger?.LogInformation($"{GetType().Name}: aktivacija.");
		}

		public void Deactivate() {
			_logger?.LogInformation($"{GetType().Name}: deaktivacija.");
		}

		public Func<ValueTask>? HandleRetryClicked { get; set; }

		public Func<ValueTask>? HandleReturnPackageClicked { get; set; }

		public Func<ValueTask>? HandleLeavePackageClicked { get; set; }
	}
}
