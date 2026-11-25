using System.Linq;
using Flatsoft.Common;
using Serilog;
using TopPos.Kiosk.Workflow;

namespace TopPos.Kiosk.ViewModels
{
	/// <summary>
	/// Courier login routing flow — OnEntry handleri za CourierManualLogin, CourierManualAuthenticating, CourierAuthenticating, CourierLockerBoxAvailability,
	/// CourierPickupConfirmation i CourierFetchingExpiredPackages. Premešteno iz MainWindowViewModel (Faza 5).
	/// (Ulazna tačka <c>TryHandleCourierOrServiceBarcode</c> + <c>GoToStartPage</c> ostaju u MWVM.)
	/// v4: stranica je ViewModel (bez WPF Page objekata).
	/// </summary>
	internal sealed class CourierLoginActivator : WorkflowActivatorBase
	{
		private readonly CourierManualLoginViewModel _courierManualLoginViewModel;
		private readonly CourierLockerBoxAvailabilityViewModel _courierLockerBoxAvailabilityViewModel;
		private readonly CourierPickupConfirmationViewModel _courierPickupConfirmationViewModel;

		public CourierLoginActivator(IWorkflowHost host) : base(host)
		{
			_courierManualLoginViewModel = SetUpCourierManualLoginPageViewModel();
			_courierLockerBoxAvailabilityViewModel = SetUpCourierLockerBoxAvailabilityViewModel();
			_courierPickupConfirmationViewModel = SetUpCourierPickupConfirmationViewModel();
		}

		private CourierManualLoginViewModel SetUpCourierManualLoginPageViewModel()
			=> new(2/*_configuration.General.MinimumUnlockCodeLength*/, _configuration.General.MaximumUnlockCodeLength, _languageManager, _logger);
		private CourierLockerBoxAvailabilityViewModel SetUpCourierLockerBoxAvailabilityViewModel()
			=> new (TimeSpan.FromMilliseconds(_configuration.General.OperationTimeout), _languageManager, _logger);
		private CourierPickupConfirmationViewModel SetUpCourierPickupConfirmationViewModel()
			=> new (_languageManager, _logger);

		// OnEntry za CourierManualLogin — ulaz sa Start-a (barkod upisan u kontekst u OnEntryFrom) i povratak posle
		// neuspele provere koda. Uneti kod putuje uz trigger CourierManualCodeEntered; provera je u CourierManualAuthenticating.
		public ValueTask GoToCourierManualLoginPage()
        {
            var session = CurrentSession;
            _currentWorkflowViewModel?.Deactivate();

            // Ovaj poziv prazni red trigera, pa do njega stiže i izuzetak iz OnEntry-ja kasnijih stanja; pozivalac je
            // async void handler tastature — neuhvaćen izuzetak bi oborio proces.
            async ValueTask<bool> ManualCodeHandler(string manualCode)
				{
					try {
						await FireInSession(session, _workflowMachine.CourierManualCodeEntered, manualCode).ConfigureAwait(false);
						return true;
					}
					catch (Exception ex) {
						_workflowContext.Logger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToCourierManualLoginPage)} exception: {ex.Message}");
						await ResetInSession(session).ConfigureAwait(false);
						return false;
					}
				}
            _courierManualLoginViewModel.HandleScannedBarcode = ManualCodeHandler;
            _courierManualLoginViewModel.HandleCodeConfirmed = ManualCodeHandler;
            _courierManualLoginViewModel.HandleKeyPressed = key => {
					_operationCountdownTimer.ResetCounter();
					return ValueTask.CompletedTask;
				};
            _courierManualLoginViewModel.Activate();
            _currentWorkflowViewModel = _courierManualLoginViewModel;

            this.CurrentPage = _courierManualLoginViewModel;
            SetCurrentStep(1);
            _operationCountdownTimer.Start();
            //_audioPlayer.Play(MakeCurrentLanguagePath("scan-or-enter-unlock-code.mp3"));

            return ValueTask.CompletedTask;
        }

		/// <summary>
		/// OnEntry za <see cref="WorkflowState.CourierManualAuthenticating"/> — provera ručno unetog koda uz skenirani barkod
		/// (iz konteksta). Uspeh → <see cref="WorkflowTrigger.CourierLoginSuccessful"/>; neuspeh → poruka pa
		/// <see cref="WorkflowTrigger.CourierLoginFailed"/> (nazad na unos koda). Ekran za unos ostaje prikazan, ali neaktivan.
		/// </summary>
		public async ValueTask AuthenticateManualCourierAndRoute(string manualCode)
		{
			_currentWorkflowViewModel?.Deactivate();
			_currentWorkflowViewModel = null;
			// Pun rok od slanja; CourierLogin je ograničen RequestTimeout-om (kraći od OperationTimeout).
			_operationCountdownTimer.ResetCounter();
			try {
				var finalLoginCode = $"{_workflowContext.CourierLoginBarcode};{manualCode}";
				var (courierCode, errorMessage) = await _serviceClient.CourierLogin(finalLoginCode, CancellationToken.None).ConfigureAwait(false);
				if (courierCode == null) {
					await DisplayMessage(errorMessage ?? this["StartPage.CourierAuthenticationFailed.Default"],
										MessageType.Error, _configuration.General.CourierMessageDisplayTimeInSeconds).ConfigureAwait(false);
					await _workflowMachine.FireAsync(WorkflowTrigger.CourierLoginFailed).ConfigureAwait(false);
					return;
				}
				_workflowContext.CourierCode = courierCode;
				_workflowContext.Logger = Serilog.Log.ForContext("CourierId", LogMask.Credential(manualCode));	// v4: maskiran
				await _workflowMachine.FireAsync(WorkflowTrigger.CourierLoginSuccessful).ConfigureAwait(false);
			}
			catch (Exception ex) {
				// Greška (ne odbijen kod): prekid prijave, na Start. Reset je u ovom virtuelnom stanju dozvoljen.
				_workflowContext.Logger.Error($"{nameof(MainWindowViewModel)}: {nameof(AuthenticateManualCourierAndRoute)} exception: {ex.Message}");
				await FireResetAsync().ConfigureAwait(false);
			}
		}

		/// <summary>
		/// OnEntry za <see cref="WorkflowState.CourierAuthenticating"/> — automatska prijava kurira skeniranim barkodom.
		/// Uspeh → <see cref="WorkflowTrigger.CourierLoginSuccessful"/> (routing je u machine PermitIf granama);
		/// neuspeh → poruka pa <see cref="WorkflowTrigger.CourierLoginFailed"/>. Barkod stiže uz parametrizovan trigger.
		/// </summary>
		public async ValueTask AuthenticateCourierAndRoute(string barcode)
		{
			try {
				var (courierCode, errorMessage) = await _serviceClient.CourierLogin(barcode, CancellationToken.None).ConfigureAwait(false);
				if (courierCode == null) {
					await DisplayMessage(errorMessage ?? this["StartPage.CourierAuthenticationFailed.Default"],
										MessageType.Error, _configuration.General.CourierMessageDisplayTimeInSeconds).ConfigureAwait(false);
					await _workflowMachine.FireAsync(WorkflowTrigger.CourierLoginFailed).ConfigureAwait(false);
					return;
				}
				_workflowContext.CourierCode = courierCode;
				_workflowContext.Logger = Serilog.Log.ForContext("CourierId", LogMask.Credential(barcode));	// v4: maskiran
				await _workflowMachine.FireAsync(WorkflowTrigger.CourierLoginSuccessful).ConfigureAwait(false);
			}
			catch (Exception ex) {
				// Bez izlaznog trigera kiosk bi ostao zaglavljen u zauzetom stanju.
				_workflowContext.Logger.Error($"{nameof(MainWindowViewModel)}: {nameof(AuthenticateCourierAndRoute)} exception: {ex.Message}");
				await _workflowMachine.FireAsync(WorkflowTrigger.CourierLoginFailed).ConfigureAwait(false);
			}
		}

		public async ValueTask GoToLockerBoxAvailabilityPage()
        {
            var contextLogger = _workflowContext.Logger;
            var session = CurrentSession;
            _currentWorkflowViewModel?.Deactivate();
            try
            {
                _courierLockerBoxAvailabilityViewModel.FuncFetchLockerBoxAvailabilityInfo = async (ct) =>
									{
										try {
											contextLogger.Information($"{nameof(MainWindowViewModel)}: {nameof(GoToLockerBoxAvailabilityPage)}: dobijanje informacija o dostupnosti kutija.");
											var (statusPaketomata, error) = await _serviceClient.GetLockerBoxesAvailability(ct).ConfigureAwait(false);
											if (statusPaketomata == null || statusPaketomata.StatusiPoVelicinamaPozicija?.Any() != true) {	// S/M/L
												contextLogger.Error($"{nameof(GoToLockerBoxAvailabilityPage)}: odgovor na zahtev za dobijanje dostupnosti kutija nije dobijen.");
                                                return null;
                                            }
											return statusPaketomata;
                                        }
										catch (Exception ex) {
											contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToLockerBoxAvailabilityPage)} exception: {ex.Message}");
                                            return null;
                                        }
                                    };
				_courierLockerBoxAvailabilityViewModel.HandleCompleteClicked = async () =>
										{
											await FireInSession(session, WorkflowTrigger.BoxAvailabilityCompleted).ConfigureAwait(false);
											return;
										};

                _courierLockerBoxAvailabilityViewModel.Activate();
                _currentWorkflowViewModel = _courierLockerBoxAvailabilityViewModel;
                this.CurrentPage = _courierLockerBoxAvailabilityViewModel;
                // Tek posle promene stranice: OnDeactivatingPage zaustavlja tajmer. Ranije je Start bio pre nje, pa ekran
                // posle učitavanja dostupnosti nije imao rok — kurir ode, kiosk ostaje zauzet.
                _operationCountdownTimer.Start();
            }
            catch (Exception ex)
            {
                contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToLockerBoxAvailabilityPage)} exception: {ex.Message}");
                await FireResetAsync().ConfigureAwait(false);
            }
        }

		// OnEntry za CourierPickupConfirmation. "Da li želite da preuzmete istekle pakete?"
		public ValueTask GoToCourierPickupConfirmationPage()
		{
			var contextLogger = _workflowContext.Logger;
			var session = CurrentSession;
			_currentWorkflowViewModel?.Deactivate();
			_courierPickupConfirmationViewModel.HandleCancelClicked = async () => {
									try {
										await FireInSession(session, WorkflowTrigger.PickupConfirmationCancelled).ConfigureAwait(false);
									}
									catch (Exception ex) {
										contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToCourierPickupConfirmationPage)} exception: {ex.Message}");
										await ResetInSession(session).ConfigureAwait(false);
									}
								};
			_courierPickupConfirmationViewModel.HandleConfirmClicked = async () => {
									try {
										await FireInSession(session, WorkflowTrigger.PickupConfirmed).ConfigureAwait(false);
									}
									catch (Exception ex) {
										contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToCourierPickupConfirmationPage)} exception: {ex.Message}");
										await ResetInSession(session).ConfigureAwait(false);
									}
								};

			_courierPickupConfirmationViewModel.Activate();
			_currentWorkflowViewModel = _courierPickupConfirmationViewModel;

			this.CurrentPage = _courierPickupConfirmationViewModel;
			_operationCountdownTimer.Start();
			return ValueTask.CompletedTask;
		}

		/// <summary>
		/// OnEntry za <see cref="WorkflowState.CourierFetchingExpiredPackages"/> — fetch isteklih paketa.
		/// Ima paketa → <see cref="WorkflowTrigger.HasExpiredPackages"/>; nema → <see cref="WorkflowTrigger.NoExpiredPackages"/>.
		/// </summary>
		public async ValueTask FetchExpiredPackagesAndRoute()
		{
			var courierCode = _workflowContext.CourierCode!;
			var contextLogger = _workflowContext.Logger;
			// Pun rok od slanja: stranica se ovde ne menja, pa bi tajmer prethodnog ekrana (dostupnost sanduka, pitanje o
			// preuzimanju) nastavio da teče i Reset-om prekinuo kurira posle uspešnog odgovora. Zahtev je ograničen
			// RequestTimeout-om (kraći od OperationTimeout).
			_operationCountdownTimer.ResetCounter();
			contextLogger.Information($"Trazenje liste isteklih paketa...");
			var (expiredPackageInfos, errorMessage2) = await _serviceClient.GetExpiredPackagesForPickup(courierCode, CancellationToken.None).ConfigureAwait(false);
			if (expiredPackageInfos == null) {
				await DisplayMessage(errorMessage2 ?? this["StartPage.ExpiredPackagesListRetrievalFailure.Default"],
									MessageType.Error, _configuration.General.CourierMessageDisplayTimeInSeconds).ConfigureAwait(false);
				await FireResetAsync().ConfigureAwait(false);
				return;
			}
			contextLogger.Information($"Ukupan broj isteklih paketa: {expiredPackageInfos!.Count()}");
			if (expiredPackageInfos?.Any() == true) {
				_workflowContext.RemainingExpiredPackages = [..expiredPackageInfos];
				await _workflowMachine.FireAsync(WorkflowTrigger.HasExpiredPackages).ConfigureAwait(false);
			}
			else {
				await _workflowMachine.FireAsync(WorkflowTrigger.NoExpiredPackages).ConfigureAwait(false);
			}
		}
	}
}
