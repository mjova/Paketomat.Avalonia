using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Flatsoft.Common;
using Flatsoft.Common.UI;

namespace TopPos.Client.WPF.ViewModels
{
	/// <summary>
	/// Prihvaćen početak sesije: kiosk je već rezervisan. <see cref="StartAsync"/> predaje triger mašini, <see cref="Cancel"/>
	/// oslobađa rezervaciju ako triger nije ni predat (npr. otkazan odziv dugmeta).
	/// </summary>
	public sealed class PendingStart(Func<Task> start, Action cancel)
	{
		public Task StartAsync() => start();
		public void Cancel() => cancel();
	}

	public class StartViewModel : ViewModelBase, IWorkflowViewModel
	{
		private readonly BarcodeScannerClient _barcodeScannerClient;

        public StartViewModel(BarcodeScannerClient barcodeScannerClient, string lockerQrCodeText, LanguageManager langManager, ILogger? logger)
			:base(langManager, logger)
		{
			_barcodeScannerClient = barcodeScannerClient;
			this.LockerQrCodeText = lockerQrCodeText;
			this.RecipientPickUpPackageCommand = new AsyncCommand<bool>(async (o,ct) =>
												{
													_logger?.LogDebug($"{nameof(StartViewModel)}: executing {nameof(RecipientPickUpPackageCommand)}.");
													await StartAfterClickFeedback(this.BeginRecipientPickUpPackage, ct);
													return true;
												});

			this.SenderDepositPackageCommand = new AsyncCommand<bool>(async (o,ct) =>
												{
													_logger?.LogDebug($"{nameof(StartViewModel)}: executing {nameof(SenderDepositPackageCommand)}.");
													await StartAfterClickFeedback(this.BeginSenderDepositPackage, ct);
													return true;
												});
        }

		/// <summary>
		/// Kiosk se rezerviše odmah na klik, pre odziva dugmeta — za tih 200 ms niko drugi (skener, Agent) ne može da ga
		/// preuzme. Odbijen klik (kiosk zauzet) nema ni odziv ni triger.
		/// </summary>
		private async Task StartAfterClickFeedback(Func<PendingStart?>? begin, CancellationToken ct)
		{
			var pending = begin?.Invoke();
			if (pending == null) {
				return;
			}
			try {
				await DelayForClickFeedback(ct);
			}
			catch {
				pending.Cancel();
				throw;
			}
			await pending.StartAsync();
        }

        public void Activate()
		{
			_logger?.LogInformation($"{GetType().Name}: aktivacija.");
			_logger?.LogInformation($"{GetType().Name}: postavljanje barkod hendlera.");
			_barcodeScannerClient.ProcessBarcode = this.HandleScannedBarcode;
		}

		public void Deactivate() {
			_logger?.LogInformation($"{GetType().Name}: deaktivacija.");
			_logger?.LogInformation($"{GetType().Name}: uklanjanje barkod hendlera.");
			_barcodeScannerClient.ProcessBarcode = null;
		}

		/// <summary>Sadržaj QR koda na Start ekranu — identifikacija paketomata (npr. "XP000111").</summary>
		public string LockerQrCodeText { get; }

		public ICommand RecipientPickUpPackageCommand { get; set; }

		public ICommand SenderDepositPackageCommand { get; set; }

        public Func<string, ValueTask<bool>>? HandleScannedBarcode { get; set; }

		/// <summary>Rezervacija za „Preuzmi paket“; null = kiosk je zauzet, klik se odbacuje.</summary>
		public Func<PendingStart?>? BeginRecipientPickUpPackage { get; set; }

		/// <summary>Rezervacija za „Ostavi paket“; null = kiosk je zauzet, klik se odbacuje.</summary>
		public Func<PendingStart?>? BeginSenderDepositPackage { get; set; }

		// Kontroliše vidljivost "Ostavi paket" dugmeta na Start ekranu (General.EnablePackageDeposition).
		private bool _isPackageDepositionEnabled;
		public bool IsPackageDepositionEnabled
		{
			get => _isPackageDepositionEnabled;
			set => SetProperty(ref _isPackageDepositionEnabled, value);
		}
    }
}
