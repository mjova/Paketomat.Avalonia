using Serilog;
using TopPos.Kiosk.Workflow;
using TopPos.LockController;

namespace TopPos.Kiosk.ViewModels
{
	/// <summary>
	/// Courier deposit (ostavljanje paketa) flow — OnEntry handleri za CourierDepositPrompt,
	/// CourierPackageDepositBarcodeScan, CourierPackageDeposit, CourierDepositConfirmation. Premešteno iz
	/// MainWindowViewModel (Faza 5). Closure state-machine (SemaphoreSlim + flagovi) u
	/// <see cref="GoToCourierPackageDepositPage"/> ostaje nepromenjena; deljeni courier barcode scan VM/Page
	/// dolaze iz <see cref="WorkflowActivatorBase"/> (host).
	/// v4: stranica je ViewModel (bez WPF Page objekata).
	/// </summary>
	internal sealed class CourierDepositActivator : WorkflowActivatorBase
	{
		private readonly CourierDepositPromptViewModel _courierDepositPromptViewModel;
		private readonly CourierPackageDepositViewModel _courierPackageDepositViewModel;
		private readonly CourierDepositConfirmationViewModel _courierDepositConfirmationViewModel;
		// Odustani na ekranu za skeniranje prekida i zahtev koji je u toku (CourierDepositBarcodeValidating).
		private CancellationTokenSource? _ctsScan;

		public CourierDepositActivator(IWorkflowHost host) : base(host)
		{
			_courierDepositPromptViewModel = SetUpCourierDepositPromptViewModel();
			_courierPackageDepositViewModel = SetUpCourierPackageDepositViewModel();
			_courierDepositConfirmationViewModel = SetUpCourierDepositConfirmationViewModel();
		}

		private CourierDepositPromptViewModel SetUpCourierDepositPromptViewModel()
			=> new (_languageManager, _logger);

		private CourierPackageDepositViewModel SetUpCourierPackageDepositViewModel()
			=> new (_languageManager, _logger);

		private CourierDepositConfirmationViewModel SetUpCourierDepositConfirmationViewModel()
			=> new (_languageManager, _logger);

		/// <summary>
		/// OnEntry za <see cref="WorkflowState.CourierDepositPrompt"/>. Čita fromPickup iz konteksta
		/// (<see cref="WorkflowContext.ExpiredPackagesPickedUp"/>) — utiče samo na prikazani tekst/audio.
		/// </summary>
		public ValueTask GoToCourierDepositPromptPage()
		{
			var fromPickup = _workflowContext.ExpiredPackagesPickedUp;
			var session = CurrentSession;
			_currentWorkflowViewModel?.Deactivate();
			_courierDepositPromptViewModel.HandleCancelClicked = () => new ValueTask(FireInSession(session, WorkflowTrigger.DepositPromptCancelled));
			_courierDepositPromptViewModel.HandleDepositPackageClicked = () => new ValueTask(FireInSession(session, WorkflowTrigger.DepositPromptDepositClicked));
			_courierDepositPromptViewModel.ExpiredPackagesPickedUp = fromPickup;	// Affects displayed text only
			_courierDepositPromptViewModel.Activate();
			_currentWorkflowViewModel = _courierDepositPromptViewModel;

			this.CurrentPage = _courierDepositPromptViewModel;
			_operationCountdownTimer.Start();
			_audioPlayer.Play(MakeCurrentLanguagePath("courier-logged-in.mp3"));
			if (!fromPickup) {
				_audioPlayer.Play(MakeCurrentLanguagePath("courier-no-packages-for-pickup.mp3"));
			}
			return ValueTask.CompletedTask;
		}

		/// <summary>
		/// OnEntry za <see cref="WorkflowState.CourierPackageDepositBarcodeScan"/> (i povratak posle odbijenog barkoda).
		/// Skeniran barkod putuje uz trigger <see cref="WorkflowTrigger.DepositBarcodeScanned"/>; provera na serveru (dodela
		/// vrata) je u <see cref="WorkflowState.CourierDepositBarcodeValidating"/>.
		/// </summary>
		public ValueTask GoToCourierPackageDepositBarcodeScanPage()
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
														await FireInSession(session, WorkflowTrigger.DepositBarcodeScanTimeout).ConfigureAwait(false);
														return (true, null);
													}
													await FireInSession(session, _workflowMachine.DepositBarcodeScanned, barcode).ConfigureAwait(false);
													return (true, null);
												}
												catch (Exception ex) {
													contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToCourierPackageDepositBarcodeScanPage)} exception: {ex.Message}");
													await ResetInSession(session).ConfigureAwait(false);
													return (false, TimeSpan.FromSeconds(1));
												}
											});
				trackingStarted = true;

				_courierBarcodeScanViewModel.Barcode = null;
				_courierBarcodeScanViewModel.PackagePickup = false;
				_courierBarcodeScanViewModel.HandleCancelClicked = InSession(session, async () => {
										_barcodeScanTracker.Stop();
										ctsScan.Cancel();
										await FireInSession(session, WorkflowTrigger.DepositBarcodeScanCancelled).ConfigureAwait(false);
									});
				_courierBarcodeScanViewModel.Activate();
				_currentWorkflowViewModel = _courierBarcodeScanViewModel;
				this.CurrentPage = _courierBarcodeScanViewModel;
				SetCurrentStep(1);
				_audioPlayer.Play(MakeCurrentLanguagePath("courier-scan-package-to-deposit.mp3"));
				return ValueTask.CompletedTask;
			}
			catch (Exception ex) {
				// Pre praćenja skenera nema izlaznog trigera (opšti tajmer zaustavljen). Posle pokretanja izlaz daje praćenje (istek).
				contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToCourierPackageDepositBarcodeScanPage)} exception: {ex.Message}");
				return trackingStarted ? ValueTask.CompletedTask : FireResetAsync();
			}
		}

		/// <summary>
		/// OnEntry za <see cref="WorkflowState.CourierDepositBarcodeValidating"/> — server dodeljuje vrata za skenirani barkod.
		/// Ekran za skeniranje ostaje prikazan, skener se ne prati. Uspeh → <see cref="WorkflowTrigger.DepositBarcodeAccepted"/>;
		/// odbijen barkod → poruka pa <see cref="WorkflowTrigger.DepositBarcodeRejected"/> (nazad na skeniranje); zahtev
		/// prekinut sa Odustani → <see cref="WorkflowTrigger.DepositBarcodeScanCancelled"/>; izuzetak → Start.
		/// </summary>
		public async ValueTask RequestCourierDepositDoorAndRoute(string barcode)
		{
			var courierCode = _workflowContext.CourierCode!;
			var contextLogger = _workflowContext.Logger;
			var ctsScan = _ctsScan;
			_barcodeScanTracker.Stop();
			try {
				// TODO: what to do if the confirmation call fails for whatever reason?
				var (doorNumber, errorMessage) = await _serviceClient.CourierRequestPackageDeposit(courierCode, barcode, ctsScan?.Token ?? CancellationToken.None)
																	.ConfigureAwait(false);
				if (ctsScan?.IsCancellationRequested == true) {
					// Odustani je prekinuo zahtev; isti triger šalje i dugme — koji god stigne prvi vodi na Start.
					await _workflowMachine.FireAsync(WorkflowTrigger.DepositBarcodeScanCancelled).ConfigureAwait(false);
					return;
				}
				if (!doorNumber.HasValue) {
					await DisplayMessage(errorMessage ?? this["CourierPackageDepositPage.DoorInformationRequestFailure.Default"],
										MessageType.Error, _configuration.General.CourierMessageDisplayTimeInSeconds).ConfigureAwait(false);
					await _workflowMachine.FireAsync(WorkflowTrigger.DepositBarcodeRejected).ConfigureAwait(false);
					return;
				}
				// barcode+doorNumber putuju uz parametrizovan trigger; OnEntryFrom ih upisuje u kontekst i enrich-uje PackageId logger.
				await _workflowMachine.FireAsync(_workflowMachine.DepositBarcodeAccepted, barcode, doorNumber.Value).ConfigureAwait(false);
			}
			catch (Exception ex) {
				// Bez izlaznog trigera kiosk bi ostao zaglavljen u zauzetom stanju; Reset je ovde dozvoljen.
				contextLogger.Error($"{nameof(CourierDepositActivator)}: {nameof(RequestCourierDepositDoorAndRoute)} exception: {ex.Message}");
				await FireResetAsync().ConfigureAwait(false);
			}
		}

		public async ValueTask GoToCourierPackageDepositPage()
		{
			var courierCode = _workflowContext.CourierCode!;
			var barcode = _workflowContext.DepositBarcode!;
			var depositDoorNumber = _workflowContext.DepositDoorNumber!.Value;
			var contextLogger = _workflowContext.Logger;
			var session = CurrentSession;
			_currentWorkflowViewModel?.Deactivate();
			bool trackingStarted = false;
			try
			{
				_operationCountdownTimer.Stop();	// No plain timeouts here; the timeout will eventually come from a door not being locked back.

				DoorStatusCode? doorStatusCode = null;
				int retries = 0;
				do {
					if (retries > 0) {
						await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
					}
					doorStatusCode = await OpenDoorSingle(depositDoorNumber,
																				TimeSpan.FromMilliseconds(_configuration.General.DoorStatusResponseTimeout),
																				CancellationToken.None).ConfigureAwait(false);	// TODO: operation cancellation token
				} while (doorStatusCode?.IsOpen != true && ++retries < 2);

				if (doorStatusCode?.IsOpen != true)
				{
					await DisplayMessage(this["General.DoorOpeningFailed.Courier.Template"].Replace("{doorNumber}", depositDoorNumber.ToString()),
										MessageType.Error, _configuration.General.CourierMessageDisplayTimeInSeconds)
							.ConfigureAwait(false);
					await HandleDoorActionFailure(depositDoorNumber,
												doorStatusCode,
												_configuration.General.CourierMessageDisplayTimeInSeconds,
												() => new ValueTask(_workflowMachine.FireAsync(WorkflowTrigger.DepositDoorOpenFailed)),
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
													nextTrigger = WorkflowTrigger.DepositCancelledLogout;
												}
												else if (largerBoxSizeRequested) {
													await TryChangeBoxSize(courierCode, barcode, depositDoorNumber, contextLogger).ConfigureAwait(false);
													largerBoxSizeRequested = false; // To prevent any additional clicks on the button to change the size
													nextTrigger = WorkflowTrigger.DepositChangeBoxRequested;
												}
												else if (_configuration.General.ConfirmPackageDeposition)	// explicit
												{
													nextTrigger = WorkflowTrigger.DepositDoorClosedExplicitConfirm;
												}
												else // automatic
												{
													// bez potvrde servera kurir ne nastavlja: ekran sa izborom (DepositConfirmationFailedActivator)
													confirmFailure = await SendDepositConfirmation(courierCode, barcode, depositDoorNumber, contextLogger).ConfigureAwait(false);
													nextTrigger = WorkflowTrigger.DepositDoorClosedAutoConfirm;
												}
											}
											finally {
												syncResize.Release();
											}
											await (confirmFailure != null
													? FireInSession(session, _workflowMachine.DepositConfirmationFailed, confirmFailure)
													: FireInSession(session, nextTrigger)).ConfigureAwait(false);
										});
				trackingStarted = true;

				_courierPackageDepositViewModel.Barcode = barcode;
				// Kad su vrata zatvorena, sledeći korak je već odredio door tracker (uz potvrdu servera, koja traje); klik koji je za
				// to vreme čekao semafor se ignoriše — inače bi odjava mogla da preskoči ekran neuspele potvrde ostavljanja.
				_courierPackageDepositViewModel.HandleChangeBoxClicked = InSession(session, async () => {
										try {
											await syncResize.WaitAsync().ConfigureAwait(false);
											if (doorClosed) {
												contextLogger.Information($"{nameof(MainWindowViewModel)}: promena velicine ignorisana — vrata {depositDoorNumber} su vec zatvorena.");
												return;
											}
											// Vrata još otvorena: zapamti zahtev, obradi se kad se vrata zatvore (door tracker). Bez navigacije.
											var	packageRequestPendingMessage = this["CourierPackageDepositPage.PackageSizeChangeRequestPending.Template"].Replace("{depositDoorNumber}", depositDoorNumber.ToString());
											await DisplayMessage(packageRequestPendingMessage, MessageType.Information, _configuration.General.CourierMessageDisplayTimeInSeconds).ConfigureAwait(false);
											largerBoxSizeRequested = true;
										}
										finally {
											syncResize.Release();
										}
									});
				_courierPackageDepositViewModel.HandleCancelClicked = InSession(session, async () => {
										try {
											await syncResize.WaitAsync().ConfigureAwait(false);
											if (doorClosed) {
												contextLogger.Information($"{nameof(MainWindowViewModel)}: odjava ignorisana — vrata {depositDoorNumber} su vec zatvorena.");
												return;
											}
											// Vrata još otvorena: zapamti logout, izvrši se kad se vrata zatvore (door tracker). Bez navigacije.
											var	logoutPendingMessage = this["CourierPackageDepositPage.CancelRequestPending.Template"].Replace("{depositDoorNumber}", depositDoorNumber.ToString());
											await DisplayMessage(logoutPendingMessage, MessageType.Information, _configuration.General.CourierMessageDisplayTimeInSeconds).ConfigureAwait(false);
											logoutRequested = true;
										}
										finally {
											syncResize.Release();
										}
									});

				_courierPackageDepositViewModel.Activate();
				_currentWorkflowViewModel = _courierPackageDepositViewModel;
				this.CurrentPage = _courierPackageDepositViewModel;
				SetCurrentStep(2);
				_audioPlayer.Play(MakeCurrentLanguagePath("courier-deposit-package-close-door.mp3"));
			}
			catch (Exception ex) {
				// Pre praćenja vrata nema izlaznog trigera (Home ugašen, opšti tajmer zaustavljen) — kiosk bi ostao zaglavljen.
				// Posle pokretanja izlaz daje praćenje (zatvaranje ili istek); Reset bi tada prekinuo započet tok.
				contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToCourierPackageDepositPage)} exception: {ex.Message}");
				if (!trackingStarted) {
					await FireResetAsync().ConfigureAwait(false);
				}
			}
		}

		private async ValueTask TryChangeBoxSize(string courierCode, string barcode, int inadequateDoorNumber, ILogger contextLogger)
		{
			contextLogger.Information($"{nameof(MainWindowViewModel)}: kurir {courierCode} je otkazao stavljanje paketa. Slanje zahteva za veće sanduče je pokrenuto.");

			try
			{
				var (newSize, errorMessage) = await _serviceClient.CourierUpdatePackageSize(courierCode, barcode, CancellationToken.None).ConfigureAwait(false);
				if (newSize.HasValue) {
					contextLogger.Information($"{nameof(MainWindowViewModel)}: odobreno novo sanduče veličine {newSize}.");
					await DisplayMessage(this["CourierPackageDepositPage.NewPackageSize.Template"].Replace("{newSize}", newSize.Value.ToString()),
										MessageType.Information, _configuration.General.CourierMessageDisplayTimeInSeconds).ConfigureAwait(false);
				}
				else {
					var logErrorMessage = errorMessage ?? "zahtev za većim sandučetom nije uspeo";
					contextLogger.Error($"{nameof(MainWindowViewModel)}: {logErrorMessage}");
					await DisplayMessage(errorMessage ?? this["CourierPackageDepositPage.PackageSizeChangeFailure.Default"],
										MessageType.Error, _configuration.General.CourierMessageDisplayTimeInSeconds).ConfigureAwait(false);
				}
			}
			catch (Exception ex) {
				contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(TryChangeBoxSize)} exception: {ex.Message}");
			}
		}

		public ValueTask GoToCourierDepositConfirmationPage()
		{
			var courierCode = _workflowContext.CourierCode!;
			var barcode = _workflowContext.DepositBarcode!;
			var depositDoorNumber = _workflowContext.DepositDoorNumber!.Value;
			var contextLogger = _workflowContext.Logger;
			var session = CurrentSession;
			_currentWorkflowViewModel?.Deactivate();
			// Vrata su zatvorena: odluku daje Potvrdi, Otkaži ili istek vremena (= potvrda), i samo prva se računa. Potvrda koja
			// čeka server se ne prekida — ishod (sledeći paket ili ekran neuspele potvrde) određuje samo odgovor servera.
			// Odluka posle kraja sesije (istek roka ili klik stare sesije) se ignoriše: bez slanja i bez trigera.
			var decided = 0;
			var ctsTimeout = new CancellationTokenSource();
			bool TryDecide(string decision)
			{
				if (Interlocked.Exchange(ref decided, 1) != 0) {
					contextLogger.Information($"{nameof(MainWindowViewModel)}: {decision} ignorisano — odluka o potvrdi ostavljanja je vec doneta.");
					return false;
				}
				ctsTimeout.Cancel();
				contextLogger.Information($"{nameof(MainWindowViewModel)}: {decision}.");
				return true;
			}
			async ValueTask ConfirmDeposit(string decision)
			{
				if (!IsSessionActive(session)) {
					contextLogger.Information($"{nameof(MainWindowViewModel)}: {decision} ignorisano — sesija je zavrsena.");
					return;
				}
				if (!TryDecide(decision)) {
					return;
				}
				try {
					// Bez potvrde servera kurir ne nastavlja: ekran sa izborom (DepositConfirmationFailedActivator). Slanje je praćen
					// posao sesije — nova sesija ne počinje dok traje (poruka o slanju, odgovor servera).
					DepositConfirmFailure? failure = null;
					var sending = RunInSession(session, async () => {
										failure = await SendDepositConfirmation(courierCode, barcode, depositDoorNumber, contextLogger).ConfigureAwait(false);
									});
					if (sending == null) {
						return;		// sesija se završava
					}
					await sending.ConfigureAwait(false);
					await (failure != null
							? FireInSession(session, _workflowMachine.DepositConfirmationFailed, failure)
							: FireInSession(session, WorkflowTrigger.DepositConfirmationConfirmed)).ConfigureAwait(false);
				}
				catch (Exception ex) {
					contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToCourierDepositConfirmationPage)} exception: {ex.Message}");
					await ResetInSession(session).ConfigureAwait(false);
				}
			}
			async Task ConfirmOnTimeout()
			{
				try {
					await Task.Delay(TimeSpan.FromMilliseconds(_configuration.General.OperationTimeout), ctsTimeout.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) {
					return;
				}
				await ConfirmDeposit($"isteklo vreme za potvrdu ostavljanja; vrata {depositDoorNumber} su zatvorena, potvrda se salje").ConfigureAwait(false);
			}

			_courierDepositConfirmationViewModel.HandleCancelClicked = InSession(session, async () => {
									if (!TryDecide("kurir nije potvrdio ostavljanje (Otkazi)")) {
										return;
									}
									try {
										await FireInSession(session, WorkflowTrigger.DepositConfirmationCancelled).ConfigureAwait(false);
									}
									catch (Exception ex) {
										contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToCourierDepositConfirmationPage)} exception: {ex.Message}");
										await ResetInSession(session).ConfigureAwait(false);
									}
								});
			_courierDepositConfirmationViewModel.HandleConfirmClicked = () => ConfirmDeposit("kurir je potvrdio ostavljanje");

			_courierDepositConfirmationViewModel.Activate();
			_currentWorkflowViewModel = _courierDepositConfirmationViewModel;

			this.CurrentPage = _courierDepositConfirmationViewModel;
			// opšti tajmer bi istekom poslao Reset (Start bez potvrde); ovde istek znači potvrdu
			_operationCountdownTimer.Stop();
			_ = ConfirmOnTimeout();
			return ValueTask.CompletedTask;
		}
	}
}
