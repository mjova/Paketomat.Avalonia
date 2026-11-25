using Serilog;
using TopPos.Kiosk.Workflow;
using TopPos.LockController;

namespace TopPos.Kiosk.ViewModels
{
	/// <summary>
	/// Sender deposit (pošiljalac ostavlja paket) flow — OnEntry handleri za SenderDepositBarcodeScan i
	/// SenderPackageDeposit. Paralela <see cref="CourierDepositActivator"/>-u, ali: ulaz sa Start dugmeta
	/// "Ostavi paket" (bez prompt ekrana) i bez petlje za sledeći paket (svaki završetak → Start).
	/// Bez confirmation ekrana: zatvaranje vrata se računa kao ostavljen paket — potvrda se šalje serveru
	/// inline (kao courier auto-confirm grana), pa pravo na Start.
	/// Šifra za server pozive dolazi iz <see cref="AppConfiguration.GeneralConfiguration.SenderDepositCourierCode"/>.
	/// Server pozivi i door-helperi se dele sa courier-om kroz <see cref="WorkflowActivatorBase"/> (host).
	/// v4: stranica je ViewModel (bez WPF Page objekata).
	/// </summary>
	internal sealed class SenderDepositActivator : WorkflowActivatorBase
	{
		private readonly SenderBarcodeScanViewModel _senderBarcodeScanViewModel;
		private readonly SenderPackageDepositViewModel _senderPackageDepositViewModel;
		// Odustani na ekranu za skeniranje prekida i zahtev koji je u toku (SenderDepositBarcodeValidating).
		private CancellationTokenSource? _ctsScan;

		public SenderDepositActivator(IWorkflowHost host) : base(host)
		{
			_senderBarcodeScanViewModel = new SenderBarcodeScanViewModel(_languageManager, _logger);
			_senderPackageDepositViewModel = new SenderPackageDepositViewModel(_languageManager, _logger);
		}

		// ─── SenderDepositBarcodeScan ───────────────────────────────────────────
		// Ulaz sa Start dugmeta (i povratak posle odbijenog barkoda). Skeniran barkod putuje uz trigger
		// SenderBarcodeScanned; provera na serveru (dodela vrata) je u SenderDepositBarcodeValidating.
		public ValueTask GoToSenderDepositBarcodeScanPage()
		{
			var contextLogger = _workflowContext.Logger;
			var session = CurrentSession;
			_currentWorkflowViewModel?.Deactivate();
			bool trackingStarted = false;
			try
			{
				_operationCountdownTimer.Stop();	// No plain timeouts here; the timeout will eventually come from the barcode scanner.
				var ctsScan = new CancellationTokenSource();
				_ctsScan = ctsScan;
				_barcodeScanTracker.Start(async barcode =>
											{
												try
												{
													if (string.IsNullOrEmpty(barcode)) {	// timeout
														await FireInSession(session, WorkflowTrigger.SenderBarcodeScanTimeout).ConfigureAwait(false);
														return (true, null);
													}
													await FireInSession(session, _workflowMachine.SenderBarcodeScanned, barcode).ConfigureAwait(false);
													return (true, null);
												}
												catch (Exception ex) {
													contextLogger.Error($"{nameof(SenderDepositActivator)}: {nameof(GoToSenderDepositBarcodeScanPage)} exception: {ex.Message}");
													await ResetInSession(session).ConfigureAwait(false);
													return (false, TimeSpan.FromSeconds(1));
												}
											});
				trackingStarted = true;

				_senderBarcodeScanViewModel.Barcode = null;
				_senderBarcodeScanViewModel.HandleCancelClicked = InSession(session, async () => {
										_barcodeScanTracker.Stop();
										ctsScan.Cancel();
										await FireInSession(session, WorkflowTrigger.SenderBarcodeScanCancelled).ConfigureAwait(false);
									});
				_senderBarcodeScanViewModel.Activate();
				_currentWorkflowViewModel = _senderBarcodeScanViewModel;
				this.CurrentPage = _senderBarcodeScanViewModel;
				SetCurrentStep(1);
				_audioPlayer.Play(MakeCurrentLanguagePath("sender-scan-package-to-deposit.mp3"));
				return ValueTask.CompletedTask;
			}
			catch (Exception ex) {
				// Pre praćenja skenera nema izlaznog trigera (opšti tajmer zaustavljen). Posle pokretanja izlaz daje praćenje (istek).
				contextLogger.Error($"{nameof(SenderDepositActivator)}: {nameof(GoToSenderDepositBarcodeScanPage)} exception: {ex.Message}");
				return trackingStarted ? ValueTask.CompletedTask : FireResetAsync();
			}
		}

		// ─── SenderDepositBarcodeValidating ─────────────────────────────────────
		/// <summary>
		/// OnEntry za <see cref="WorkflowState.SenderDepositBarcodeValidating"/> — server dodeljuje vrata za skenirani barkod.
		/// Ekran za skeniranje ostaje prikazan, skener se ne prati. Uspeh → <see cref="WorkflowTrigger.SenderBarcodeAccepted"/>;
		/// odbijen barkod → poruka pa <see cref="WorkflowTrigger.SenderBarcodeRejected"/> (nazad na skeniranje); zahtev
		/// prekinut sa Odustani → <see cref="WorkflowTrigger.SenderBarcodeScanCancelled"/>; izuzetak → Start.
		/// </summary>
		public async ValueTask RequestSenderDepositDoorAndRoute(string barcode)
		{
			var depositorCode = _configuration.General.SenderDepositCourierCode;
			var contextLogger = _workflowContext.Logger;
			var ctsScan = _ctsScan;
			_barcodeScanTracker.Stop();
			try {
				var (doorNumber, errorMessage) = await _serviceClient.CourierRequestPackageDeposit(depositorCode, barcode, ctsScan?.Token ?? CancellationToken.None)
																	.ConfigureAwait(false);
				if (ctsScan?.IsCancellationRequested == true) {
					// Odustani je prekinuo zahtev; isti triger šalje i dugme — koji god stigne prvi vodi na Start.
					await _workflowMachine.FireAsync(WorkflowTrigger.SenderBarcodeScanCancelled).ConfigureAwait(false);
					return;
				}
				if (!doorNumber.HasValue) {
					await DisplayMessage(errorMessage ?? this["SenderBarcodeScanPage.DoorInformationRequestFailure.Default"],
										MessageType.Error, _configuration.General.CourierMessageDisplayTimeInSeconds).ConfigureAwait(false);
					await _workflowMachine.FireAsync(WorkflowTrigger.SenderBarcodeRejected).ConfigureAwait(false);
					return;
				}
				// barkod+doorNumber putuju uz parametrizovan trigger; OnEntryFrom ih upisuje u kontekst.
				await _workflowMachine.FireAsync(_workflowMachine.SenderBarcodeAccepted, barcode, doorNumber.Value).ConfigureAwait(false);
			}
			catch (Exception ex) {
				// Bez izlaznog trigera kiosk bi ostao zaglavljen u zauzetom stanju; Reset je ovde dozvoljen.
				contextLogger.Error($"{nameof(SenderDepositActivator)}: {nameof(RequestSenderDepositDoorAndRoute)} exception: {ex.Message}");
				await FireResetAsync().ConfigureAwait(false);
			}
		}

		// ─── SenderPackageDeposit ───────────────────────────────────────────────
		// Otvara vrata, prati zatvaranje preko closure state-machine (semafor + flagovi). Zatvaranje vrata =
		// paket ostavljen: potvrda se šalje serveru inline (kao courier auto-confirm grana), pa SenderDoorClosedConfirm
		// vodi pravo na Start — bez confirmation ekrana.
		public async ValueTask GoToSenderPackageDepositPage()
		{
			var depositorCode = _configuration.General.SenderDepositCourierCode;
			var barcode = _workflowContext.DepositBarcode!;
			var depositDoorNumber = _workflowContext.DepositDoorNumber!.Value;
			var contextLogger = _workflowContext.Logger;
			var session = CurrentSession;
			_currentWorkflowViewModel?.Deactivate();
			bool trackingStarted = false;
			try
			{
				_operationCountdownTimer.Stop();	// No plain timeouts here; the timeout will eventually come from a door not being locked back.

				var doorStatusCode = await OpenDepositDoor(depositDoorNumber, contextLogger).ConfigureAwait(false);	// v4: poruka dok traje

				if (doorStatusCode?.IsOpen != true)
				{
					await DisplayMessage(this["General.DoorOpeningFailed.Courier.Template"].Replace("{doorNumber}", depositDoorNumber.ToString()),
										MessageType.Error, _configuration.General.CourierMessageDisplayTimeInSeconds)
							.ConfigureAwait(false);
					await HandleDoorActionFailure(depositDoorNumber,
												doorStatusCode,
												_configuration.General.CourierMessageDisplayTimeInSeconds,
												() => new ValueTask(_workflowMachine.FireAsync(WorkflowTrigger.SenderDoorOpenFailed)),
												contextLogger,
												CancellationToken.None)
							.ConfigureAwait(false);
					return;
				}
				var syncResize = new SemaphoreSlim(1, 1);	// Synchronizes access to state variables below, between the door status tracker and click handlers
				bool doorClosed = false;
				bool largerBoxSizeRequested = false;
				bool logoutRequested = false;
				_ = TrackDoorStatus(depositDoorNumber, DoorStatusCode.Closed,
										async statusCode =>
										{
											if (statusCode?.IsClosed != true) {
												await HandleDoorActionFailure(depositDoorNumber,
																			statusCode,
																			_configuration.General.CourierMessageDisplayTimeInSeconds,
																			() => ResetInSession(session),
																			contextLogger,
																			CancellationToken.None)
														.ConfigureAwait(false);
												return;
											}

											// Vrata su zatvorena. Pod semaforom odluči sledeći prelaz; trigger se fire-uje TEK posle Release-a
											// (ciljna stanja ne koriste syncResize, pa nema deadlock-a, a izbegava se držanje semafora kroz tranziciju).
											WorkflowTrigger nextTrigger;
											DepositConfirmFailure? confirmFailure = null;
											try
											{
												await syncResize.WaitAsync().ConfigureAwait(false);
												doorClosed = true;

												if (logoutRequested) {
													nextTrigger = WorkflowTrigger.SenderCancelled;
												}
												else if (largerBoxSizeRequested) {
													await TryChangeBoxSize(depositorCode, barcode, depositDoorNumber, contextLogger).ConfigureAwait(false);
													largerBoxSizeRequested = false; // To prevent any additional clicks on the button to change the size
													nextTrigger = WorkflowTrigger.SenderChangeBoxRequested;
												}
												else {
													// Zatvaranje vrata = paket ostavljen: potvrdi serveru odmah (kao courier auto-confirm), pa pravo na Start;
													// bez potvrde pošiljalac ne odlazi: ekran sa izborom (DepositConfirmationFailedActivator).
													confirmFailure = await SendDepositConfirmation(depositorCode, barcode, depositDoorNumber, contextLogger).ConfigureAwait(false);
													nextTrigger = WorkflowTrigger.SenderDoorClosedConfirm;
												}
											}
											finally {
												syncResize.Release();
											}
											await (confirmFailure != null
													? FireInSession(session, _workflowMachine.SenderConfirmationFailed, confirmFailure)
													: FireInSession(session, nextTrigger)).ConfigureAwait(false);
										});
				trackingStarted = true;

				_senderPackageDepositViewModel.Barcode = barcode;
				// Kad su vrata zatvorena, sledeći korak je već odredio door tracker (uz potvrdu servera, koja traje); klik koji je za
				// to vreme čekao semafor se ignoriše — inače bi otkaz mogao da preskoči ekran neuspele potvrde ostavljanja.
				_senderPackageDepositViewModel.HandleChangeBoxClicked = InSession(session, async () => {
										try {
											await syncResize.WaitAsync().ConfigureAwait(false);
											if (doorClosed) {
												contextLogger.Information($"{nameof(SenderDepositActivator)}: promena velicine ignorisana — vrata {depositDoorNumber} su vec zatvorena.");
												return;
											}
											// Vrata još otvorena: zapamti zahtev, obradi se kad se vrata zatvore (door tracker). Bez navigacije.
											var	packageRequestPendingMessage = this["SenderPackageDepositPage.PackageSizeChangeRequestPending.Template"].Replace("{depositDoorNumber}", depositDoorNumber.ToString());
											await DisplayMessage(packageRequestPendingMessage, MessageType.Information, _configuration.General.CourierMessageDisplayTimeInSeconds).ConfigureAwait(false);
											largerBoxSizeRequested = true;
										}
										finally {
											syncResize.Release();
										}
									});
				_senderPackageDepositViewModel.HandleCancelClicked = InSession(session, async () => {
										try {
											await syncResize.WaitAsync().ConfigureAwait(false);
											if (doorClosed) {
												contextLogger.Information($"{nameof(SenderDepositActivator)}: otkaz ignorisan — vrata {depositDoorNumber} su vec zatvorena.");
												return;
											}
											// Vrata još otvorena: zapamti otkaz, izvrši se kad se vrata zatvore (door tracker). Bez navigacije.
											var	cancelPendingMessage = this["SenderPackageDepositPage.CancelRequestPending.Template"].Replace("{depositDoorNumber}", depositDoorNumber.ToString());
											await DisplayMessage(cancelPendingMessage, MessageType.Information, _configuration.General.CourierMessageDisplayTimeInSeconds).ConfigureAwait(false);
											logoutRequested = true;
										}
										finally {
											syncResize.Release();
										}
									});

				_senderPackageDepositViewModel.Activate();
				_currentWorkflowViewModel = _senderPackageDepositViewModel;
				this.CurrentPage = _senderPackageDepositViewModel;
				SetCurrentStep(2);
				_audioPlayer.Play(MakeCurrentLanguagePath("sender-deposit-package-close-door.mp3"));
			}
			catch (Exception ex) {
				// Pre praćenja vrata nema izlaznog trigera (Home ugašen, opšti tajmer zaustavljen) — kiosk bi ostao zaglavljen.
				// Posle pokretanja izlaz daje praćenje (zatvaranje ili istek); Reset bi tada prekinuo započet tok.
				contextLogger.Error($"{nameof(SenderDepositActivator)}: {nameof(GoToSenderPackageDepositPage)} exception: {ex.Message}");
				if (!trackingStarted) {
					await FireResetAsync().ConfigureAwait(false);
				}
			}
		}

		// Duplikat courier TryChangeBoxSize (odluka: ne izvlačiti u zajednički helper).
		private async ValueTask TryChangeBoxSize(string depositorCode, string barcode, int inadequateDoorNumber, ILogger contextLogger)
		{
			contextLogger.Information($"{nameof(SenderDepositActivator)}: pošiljalac je otkazao stavljanje paketa. Slanje zahteva za veće sanduče je pokrenuto.");

			try
			{
				var (newSize, errorMessage) = await _serviceClient.CourierUpdatePackageSize(depositorCode, barcode, CancellationToken.None).ConfigureAwait(false);
				if (newSize.HasValue) {
					contextLogger.Information($"{nameof(SenderDepositActivator)}: odobreno novo sanduče veličine {newSize}.");
					await DisplayMessage(this["SenderPackageDepositPage.NewPackageSize.Template"].Replace("{newSize}", newSize.Value.ToString()),
										MessageType.Information, _configuration.General.CourierMessageDisplayTimeInSeconds).ConfigureAwait(false);
				}
				else {
					var logErrorMessage = errorMessage ?? "zahtev za većim sandučetom nije uspeo";
					contextLogger.Error($"{nameof(SenderDepositActivator)}: {logErrorMessage}");
					await DisplayMessage(errorMessage ?? this["SenderPackageDepositPage.PackageSizeChangeFailure.Default"],
										MessageType.Error, _configuration.General.CourierMessageDisplayTimeInSeconds).ConfigureAwait(false);
				}
			}
			catch (Exception ex) {
				contextLogger.Error($"{nameof(SenderDepositActivator)}: {nameof(TryChangeBoxSize)} exception: {ex.Message}");
			}
		}

	}
}
