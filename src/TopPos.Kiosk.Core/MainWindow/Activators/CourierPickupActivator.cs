using Serilog;
using TopPos.Kiosk.Workflow;
using TopPos.LockController;
using TopPos.Service;

namespace TopPos.Kiosk.ViewModels
{
	/// <summary>
	/// Courier pickup (preuzimanje isteklih paketa) flow — OnEntry handleri za CourierPickupPrompt,
	/// CourierPackagePickup, CourierPackagePickupBarcodeScan. Premešteno iz MainWindowViewModel (Faza 5).
	/// Deljeni courier barcode scan VM/Page dolaze iz <see cref="WorkflowActivatorBase"/> (host).
	/// v4: stranica je ViewModel (bez WPF Page objekata).
	/// </summary>
	internal sealed class CourierPickupActivator : WorkflowActivatorBase
	{
        private readonly CourierPickupPromptViewModel _courierPickupPromptViewModel;
		private readonly CourierPackagePickupViewModel _courierPackagePickupViewModel;

		public CourierPickupActivator(IWorkflowHost host) : base(host)
		{
			_courierPickupPromptViewModel = SetUpCourierPickupPromptViewModel();
			_courierPackagePickupViewModel = SetUpCourierPackagePickupViewModel();
		}

		private CourierPickupPromptViewModel SetUpCourierPickupPromptViewModel()
			=> new (_languageManager, _logger);
		private CourierPackagePickupViewModel SetUpCourierPackagePickupViewModel()
			=> new (_languageManager, _logger);

		public ValueTask GoToCourierPickupPromptPage()
		{
			var contextLogger = _workflowContext.Logger;
			_currentWorkflowViewModel?.Deactivate();

			// Pop sledećeg isteklog paketa iz konteksta. Guard HasMoreExpiredPackages čita listu POSLE pop-a,
			// pa tekući paket više nije u njoj — isto kao legacy `remainingPackages.Count > 0` provera.
			var remainingExpiredPackages = _workflowContext.RemainingExpiredPackages!;
			var currentPackage = remainingExpiredPackages.First();
			remainingExpiredPackages.RemoveAt(0);
			_workflowContext.CurrentPickupPackage = currentPackage;
			_workflowContext.Logger = contextLogger.ForContext("PackageId", currentPackage.Barkod);
			_courierPickupPromptViewModel.CurrentPackage = currentPackage;
			_courierPickupPromptViewModel.RemainingExpiredPackagesCount = remainingExpiredPackages.Count() + 1;
			var session = CurrentSession;
			_courierPickupPromptViewModel.HandleCancelClicked = () => new ValueTask(FireInSession(session, WorkflowTrigger.PickupPromptCancelled));
			_courierPickupPromptViewModel.HandlePickUpExpiredPackageClicked = () => new ValueTask(FireInSession(session, WorkflowTrigger.PickupPromptPickupClicked));
			_courierPickupPromptViewModel.Activate();
			_currentWorkflowViewModel = _courierPickupPromptViewModel;

			this.CurrentPage = _courierPickupPromptViewModel;
			_operationCountdownTimer.Start();
			_audioPlayer.Play(MakeCurrentLanguagePath("courier-logged-in.mp3"));
			return ValueTask.CompletedTask;
		}

		public async ValueTask GoToCourierPackagePickupPage()
		{
			var currentPackage = _workflowContext.CurrentPickupPackage!;
			var contextLogger = _workflowContext.Logger;
			var session = CurrentSession;
			_currentWorkflowViewModel?.Deactivate();
			bool trackingStarted = false;

			try
			{
				_operationCountdownTimer.Stop();	// No plain timeouts here; the timeout will eventually come from a door not being locked back.

				var doorStatusCode = await OpenDoorSingle(currentPackage.BrojPozicije,
																				TimeSpan.FromMilliseconds(_configuration.General.DoorStatusResponseTimeout),
																				CancellationToken.None).ConfigureAwait(false);	// TODO: operation cancellation token
				if (doorStatusCode?.IsOpen != true)
				{
					await DisplayMessage(this["General.DoorOpeningFailed.Courier.Template"].Replace("{doorNumber}", currentPackage.BrojPozicije.ToString()),
										MessageType.Error, _configuration.General.CourierMessageDisplayTimeInSeconds)
						.ConfigureAwait(false);
					// Machine PermitIf rutira po HasMoreExpiredPackages: ima još → PickupPrompt (sledeći paket); nema → DepositPrompt.
					await HandleDoorActionFailure(currentPackage.BrojPozicije,
												doorStatusCode,
												_configuration.General.CourierMessageDisplayTimeInSeconds,
												() => new ValueTask(_workflowMachine.FireAsync(WorkflowTrigger.PickupDoorActionFailed)),
												contextLogger,
												CancellationToken.None)
						.ConfigureAwait(false);
					return;
				}
				_ = TrackDoorStatus(currentPackage.BrojPozicije, DoorStatusCode.Closed,
											async statusCode =>
											{
												if (statusCode?.IsClosed != true) {
													try {
														// Show the overlay
														_courierPackagePickupViewModel.ShowCloseDoorMessage = true;
														await AskToCloseTheDoor(currentPackage.BrojPozicije, session, contextLogger).ConfigureAwait(false);
													}
													catch (Exception ex) {
														contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToCourierPackagePickupPage)} exception: {ex.Message}");
														await ResetInSession(session).ConfigureAwait(false);
													}
												}
												else {
													await FireInSession(session, WorkflowTrigger.PickupDoorClosedAfterOpen).ConfigureAwait(false);
												}
											});
				trackingStarted = true;

				_courierPackagePickupViewModel.Activate();
				_currentWorkflowViewModel = _courierPackagePickupViewModel;
				this.CurrentPage = _courierPackagePickupViewModel;
				SetCurrentStep(1);
			}
			catch (Exception ex) {
				// Pre praćenja vrata nema izlaznog trigera (Home ugašen, opšti tajmer zaustavljen) — kiosk bi ostao zaglavljen.
				// Posle pokretanja izlaz daje praćenje (zatvaranje ili istek); Reset bi tada prekinuo započet tok.
				contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToCourierPackagePickupPage)} exception: {ex.Message}");
				if (!trackingStarted) {
					await FireResetAsync().ConfigureAwait(false);
				}
			}
		}

		private ValueTask AskToCloseTheDoor(int openedDoorNumber, KioskLease? session, ILogger contextLogger)
		{
			// No deactivation of the current step here. We just display an overlay window.

			try
			{
				contextLogger.Debug($"{nameof(MainWindowViewModel)}: {nameof(AskToCloseTheDoor)}: asking to close the door.");
				_operationCountdownTimer.Stop();	// Should not be active anyway, but let's be sure.

				_ = TrackDoorStatus(openedDoorNumber, DoorStatusCode.Closed,
											async statusCode =>
											{
												if (statusCode?.IsClosed != true) {
													await HandleDoorActionFailure(openedDoorNumber,
																					statusCode,
																					_configuration.General.CourierMessageDisplayTimeInSeconds,
																					() => ResetInSession(session),
																					contextLogger, CancellationToken.None)
															.ConfigureAwait(false);
													return;
												}
												// Zatvorena posle podsetnika: paket je izvađen, pa ga kurir skenira kao i kad zatvori na vreme —
												// bez skeniranja nema potvrde pražnjenja i server bi paket i dalje vodio u vratima.
												await FireInSession(session, WorkflowTrigger.PickupDoorClosedAfterOpen).ConfigureAwait(false);
											});
				// No setting of the current page, just displaying of an overlay window.

			}
			catch (Exception ex) {
				contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(AskToCloseTheDoor)} exception: {ex.Message}");
				return ResetInSession(session);
			}
			return ValueTask.CompletedTask;
		}


		public ValueTask GoToCourierPackagePickupBarcodeScanPage()
		{
			var courierCode = _workflowContext.CourierCode!;
			var currentPackage = _workflowContext.CurrentPickupPackage!;
			var contextLogger = _workflowContext.Logger;
			var session = CurrentSession;
			_currentWorkflowViewModel?.Deactivate();
			bool trackingStarted = false;
			try
			{
				_operationCountdownTimer.Stop();	// No plain timeouts here; the timeout will eventually come from the barcode scanner.
				int retriesOnIncorrectBarcode = 2;
				_barcodeScanTracker.Start(async barcode =>
											{
												try
												{
													barcode = barcode?.Trim();
													if (string.IsNullOrEmpty(barcode))	// Timeout: go to the start page
													{
														await FireInSession(session, WorkflowTrigger.PickupBarcodeScanTimeout).ConfigureAwait(false);
														return (true, null);
													}

													bool barcodeConfirmed = false;
													if (barcode != currentPackage.Barkod)
													{
														contextLogger.Warning($"{nameof(GoToCourierPackagePickupBarcodeScanPage)}: skeniran je neodgovarajući barkod {barcode}. Preostali broj pokušaja: {retriesOnIncorrectBarcode}");
														await DisplayMessage(this["CourierBarcodeScanPage.BarcodeMismatch.Template"]
																			.Replace("{scannedBarcode}", barcode).Replace("{expectedBarcode}", currentPackage.Barkod),
																			MessageType.Error, _configuration.General.CourierMessageDisplayTimeInSeconds).ConfigureAwait(false);
														retriesOnIncorrectBarcode--;
														if (retriesOnIncorrectBarcode == 0) {
															contextLogger.Warning($"{nameof(GoToCourierPackagePickupBarcodeScanPage)}: neodgovarajući barkod je skreniran previše puta. Procedura skeniranja barkoda {currentPackage.Barkod} je prekinuta.");
														}
													}
													else
													{
														// TODO: what to do if the confirmation call fails for whatever reason?
														var (confirmed, errorMessage) = await _serviceClient.ConfirmExpiredPackagePickup(courierCode, barcode, currentPackage.BrojPozicije, CancellationToken.None) // TODO
																											.ConfigureAwait(false);
														if (!confirmed) {
															await DisplayMessage(errorMessage ?? this["CourierBarcodeScanPage.BarcodeScanConfirationFailure.Template"].Replace("{barcode}", barcode),
																									MessageType.Error, _configuration.General.CourierMessageDisplayTimeInSeconds).ConfigureAwait(false);
														}
														barcodeConfirmed = confirmed;
													}

													// Got service confirmation, or exceeded the number of incorrect inputs? Machine PermitIf rutira po
													// HasMoreExpiredPackages: ima još → PickupPrompt (sledeći paket); nema → DepositPrompt.
													if (barcodeConfirmed || retriesOnIncorrectBarcode == 0)
													{
														await FireInSession(session, WorkflowTrigger.PickupBarcodeResolved).ConfigureAwait(false);
														return (true, null);
													}
													return (false, null);	// null: extend time for another initial tracking period
												}
												catch (Exception ex) {
													contextLogger.Error($"{nameof(GoToCourierPackagePickupBarcodeScanPage)}: exception: {ex.Message}");
													return (false, TimeSpan.FromSeconds(1));
												}
											});
				trackingStarted = true;

				_courierBarcodeScanViewModel.Barcode = currentPackage.Barkod;
				_courierBarcodeScanViewModel.PackagePickup = true;
				_courierBarcodeScanViewModel.HandleCancelClicked = null;
				_courierBarcodeScanViewModel.Activate();
				_currentWorkflowViewModel = _courierBarcodeScanViewModel;
				this.CurrentPage = _courierBarcodeScanViewModel;
				SetCurrentStep(2);
				_audioPlayer.Play(MakeCurrentLanguagePath("courier-scan-picked-up-package.mp3"));
				return ValueTask.CompletedTask;
			}
			catch (Exception ex) {
				// Pre praćenja skenera nema izlaznog trigera (opšti tajmer zaustavljen). Posle pokretanja izlaz daje praćenje (istek).
				contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToCourierPackagePickupBarcodeScanPage)} exception: {ex.Message}");
				return trackingStarted ? ValueTask.CompletedTask : FireResetAsync();
			}
		}
	}
}
