using Flatsoft.Common.UI;
using Serilog;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.Workflow;
using TopPos.LockController;
using static TopPos.Kiosk.PosClient;

namespace TopPos.Kiosk.ViewModels
{
	/// <summary>
	/// Recipient (preuzimanje paketa) flow — OnEntry handleri za RecipientCodeInput, RecipientPhoneInput,
	/// RecipientValidating, RecipientPayment, RecipientFiscalReceipt, RecipientPackagePickup. Premešteno iz MainWindowViewModel
	/// (Faza 5). Tela su nepromenjena — deljeni servisi/operacije dolaze iz <see cref="WorkflowActivatorBase"/>.
	/// v4: stranica je ViewModel (bez WPF Page objekata); ekran potvrde i poruke preuzimanja na zahtev Agenta
	/// (RecipientRemoteConfirmation, ShowRemotePickupNotice) dolaze sa Agent kanalom u F2.
	/// </summary>
	internal sealed class RecipientFlowActivator : WorkflowActivatorBase
	{
		private readonly RecipientCodeInputViewModel _recipientCodeInputViewModel;
		private readonly RecipientPhoneInputViewModel _recipientPhoneInputViewModel;
		private readonly RecipientPackagePickupViewModel _recipientPackagePickupViewModel;
		private readonly RecipientPaymentViewModel _recipientPaymentViewModel;
		private readonly RecipientFiscalReceiptViewModel _recipientFiscalReceiptViewModel;
		private readonly ExternalMessageLocalizer _posMessageLocalizer;
		private readonly ExternalMessageLocalizer _serverMessageLocalizer;

		public RecipientFlowActivator(IWorkflowHost host) : base(host)
		{
			_posMessageLocalizer = new ExternalMessageLocalizer(_languageManager, "PosTerminalMessage.", _logger);
			_serverMessageLocalizer = new ExternalMessageLocalizer(_languageManager, "ServerMessage.", _logger);

			_recipientCodeInputViewModel = SetUpRecipientCodeInputViewModel();
			_recipientPhoneInputViewModel = SetUpRecipientPhoneInputViewModel();
			_recipientPackagePickupViewModel = SetUpRecipientPackagePickupViewModel();
			_recipientPaymentViewModel = SetUpRecipientPaymentViewModel();
			_recipientFiscalReceiptViewModel = SetUpRecipientFiscalReceiptViewModel();
		}

        private RecipientCodeInputViewModel SetUpRecipientCodeInputViewModel()
			=> new (_barcodeScannerClient, _configuration.General.MinimumUnlockCodeLength, _configuration.General.MaximumUnlockCodeLength, _languageManager, _logger);
		private RecipientPhoneInputViewModel SetUpRecipientPhoneInputViewModel()
			=> new (_configuration.General.MinimumUnlockCodeLength, _configuration.General.MaximumUnlockCodeLength, _languageManager, _logger);
		private RecipientPackagePickupViewModel SetUpRecipientPackagePickupViewModel()
			=> new (_languageManager, _logger);

		private RecipientPaymentViewModel SetUpRecipientPaymentViewModel()
			=> new (_languageManager,
						"RecipientPaymentPage.ConnectingDevice",
						"RecipientPaymentPage.AmountInfoDetailsTemplate",
						Math.Min(Math.Max(0, _configuration.General.CurrencyDecimalPlaces), 2),
						_configuration.General.CurrencyDisplayName,
						_logger);
		private RecipientFiscalReceiptViewModel SetUpRecipientFiscalReceiptViewModel()
			=> new (TimeSpan.FromMilliseconds(_configuration.General.OperationTimeout),
											TimeSpan.FromMilliseconds(_configuration.General.OperationTimeout),
											_languageManager, _logger);

		// ──────────────────────────────────────────────────────────────────────
		// OnEntry handler za WorkflowState.RecipientCodeInput.
		// Triger: WorkflowTrigger.RecipientPickupRequested (sa Start stranice).
		// Korisnik unosi/skenira unlock kod. Posle validnog unosa (kod putuje uz triger, kontekst upisuje OnEntryFrom):
		//   - ako je u konfiguraciji default phone → fire-uje
		//     RecipientCodeAcceptedWithDefaultPhone (preskače UI za telefon)
		//   - inače fire-uje RecipientCodeAccepted (ide na phone input UI)
		// ──────────────────────────────────────────────────────────────────────
		public ValueTask GoToRecipientCodeInputPage()
		{
			var session = CurrentSession;
			_currentWorkflowViewModel?.Deactivate();

			async ValueTask<bool> UnlockCodeHandler(string unlockCode)
				{
					var contextLogger = Serilog.Log.ForContext("UnlockCode", unlockCode);
					if (!IsSessionActive(session)) {
						return false;	// ekran stare sesije: bez poruke i trigera
					}
					try
					{
						if (unlockCode.Length < _configuration.General.MinimumUnlockCodeLength || !int.TryParse(unlockCode, out var _))
						{
							await DisplayMessage(this["RecipientCodeInputPage.InvalidUnlockCode"], MessageType.Error).ConfigureAwait(false);
							return false;
						}

						if (!string.IsNullOrWhiteSpace(_configuration.General.DefaultRecipientPhoneNumber))
						{
							await FireInSession(session, _workflowMachine.RecipientCodeAcceptedWithDefaultPhone, unlockCode).ConfigureAwait(false);
						}
						else
						{
							await FireInSession(session, _workflowMachine.RecipientCodeAccepted, unlockCode).ConfigureAwait(false);
						}
						return true;
					}
					catch (Exception ex)
					{
						contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToRecipientCodeInputPage)} exception: {ex.Message}");
						await ResetInSession(session).ConfigureAwait(false);
						return false;
					}
				}
			_recipientCodeInputViewModel.HandleScannedBarcode = UnlockCodeHandler;
			_recipientCodeInputViewModel.HandleCodeConfirmed = UnlockCodeHandler;
			_recipientCodeInputViewModel.HandleKeyPressed = key => {
						_operationCountdownTimer.ResetCounter();
						return ValueTask.CompletedTask;
					};
			_recipientCodeInputViewModel.Activate();
			_currentWorkflowViewModel = _recipientCodeInputViewModel;

			this.CurrentPage = _recipientCodeInputViewModel;
			SetCurrentStep(1);
			_operationCountdownTimer.Start();
			_audioPlayer.Play(MakeCurrentLanguagePath("scan-or-enter-unlock-code.mp3"));

			return ValueTask.CompletedTask;
		}

		// ──────────────────────────────────────────────────────────────────────
		// OnEntryFrom handler-i za RecipientCodeAccepted (→ RecipientPhoneInput) i RecipientCodeAcceptedWithDefaultPhone
		// (→ RecipientValidating). Kod putuje uz triger i u kontekst se upisuje tek ovde, u izvršavanju mašine: handler
		// skenera/tastature stare sesije (pozadinska nit) tako ne može da upiše kod u kontekst sledeće sesije.
		// ──────────────────────────────────────────────────────────────────────
		public ValueTask AcceptUnlockCode(string unlockCode)
		{
			_workflowContext.UnlockCode = unlockCode;
			_workflowContext.Logger = Serilog.Log.ForContext("UnlockCode", unlockCode);
			return ValueTask.CompletedTask;
		}

		public ValueTask AcceptUnlockCodeWithDefaultPhone(string unlockCode)
		{
			_workflowContext.RecipientPhoneNumber = _configuration.General.DefaultRecipientPhoneNumber?.Trim();
			return AcceptUnlockCode(unlockCode);
		}

		// ──────────────────────────────────────────────────────────────────────
		// OnEntry handler za WorkflowState.RecipientValidating (virtuelno stanje).
		// Triger: RecipientPhoneEntered (telefon upisan u kontekst u OnEntryFrom) ili
		// RecipientCodeAcceptedWithDefaultPhone (kod i telefon iz konfiguracije upisani u OnEntryFrom).
		// Šalje kod + telefon serveru (RRPP), setuje preostala context polja i fire-uje ishod.
		// Prethodni ekran ostaje prikazan, ali neaktivan (tastatura, skener) dok se čeka server.
		// ──────────────────────────────────────────────────────────────────────
		public async ValueTask ValidateRecipientAndRoute()
		{
			_currentWorkflowViewModel?.Deactivate();
			_currentWorkflowViewModel = null;
			// Pun rok od slanja: skeniran kod (default phone) ne resetuje tajmer kao taster. RRPP je ograničen
			// RequestTimeout-om (kraći od OperationTimeout), pa tajmer ne ističe dok kupac čeka odgovor.
			_operationCountdownTimer.ResetCounter();

			var contextLogger = _workflowContext.Logger;
			var unlockCode = _workflowContext.UnlockCode!;
			var phoneNumber = _workflowContext.RecipientPhoneNumber!;
			try {
				(int? doorNumber, decimal? amount, string? errorMessage) = await _serviceClient.RecipientRequestPackagePickup(phoneNumber,
																												int.Parse(unlockCode),
																												CancellationToken.None)
																										.ConfigureAwait(false);
				if (doorNumber == null) {
					await DisplayMessage(_serverMessageLocalizer.Localize(errorMessage) ?? this["RecipientPhoneInputPage.AuthorizationFailed.Default"], MessageType.Error).ConfigureAwait(false);
					await _workflowMachine.FireAsync(WorkflowTrigger.RecipientValidationFailed).ConfigureAwait(false);
					return;
				}
				if (!amount.HasValue || amount == 0m) {
					_workflowContext.RecipientPackageDoorNumber = doorNumber.Value;
					await _workflowMachine.FireAsync(WorkflowTrigger.RecipientNoPaymentNeeded).ConfigureAwait(false);
					return;
				}
				if (!_configuration.PosTerminal.TerminalInstalled)
				{
					contextLogger.Error($"{nameof(ValidateRecipientAndRoute)}: paket sa kodom {unlockCode} zahteva plaćanje na eksternom terminalu.");
					var abortMessageId = !string.IsNullOrEmpty(_configuration.General.ExternalTerminalPaymentMessageId) ?
																_configuration.General.ExternalTerminalPaymentMessageId :
																"RecipientPhoneInputPage.PayAtExternalTerminal.Default";
					await DisplayMessage(this[abortMessageId], MessageType.Information).ConfigureAwait(false);
					await _workflowMachine.FireAsync(WorkflowTrigger.RecipientPaymentNotPossible).ConfigureAwait(false);
					return;
				}
				_workflowContext.PaymentAmount = amount.Value;
				await _workflowMachine.FireAsync(WorkflowTrigger.RecipientPaymentRequired).ConfigureAwait(false);
			}
			catch (Exception ex) {
				// Izlaz iz virtuelnog stanja ne sme da zavisi od Reset-a — inače bi kiosk ostao zaglavljen u proveri.
				contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(ValidateRecipientAndRoute)} exception: {ex.Message}");
				await _workflowMachine.FireAsync(WorkflowTrigger.RecipientValidationFailed).ConfigureAwait(false);
			}
		}

		// ──────────────────────────────────────────────────────────────────────
		// OnEntry handler za WorkflowState.RecipientPhoneInput.
		// Triger: RecipientCodeAccepted (default phone path ide pravo u RecipientValidating).
		// Potvrđen telefon putuje uz trigger RecipientPhoneEntered — u kontekst se upisuje tek pri
		// ulasku u RecipientValidating, pa triger odbačen u drugom stanju ne menja kontekst.
		// ──────────────────────────────────────────────────────────────────────
		public ValueTask GoToRecipientPhoneInputPage()
		{
			var session = CurrentSession;
			_currentWorkflowViewModel?.Deactivate();

			// Ovaj poziv prazni red trigera, pa do njega stiže i izuzetak iz OnEntry-ja kasnijih stanja (provera, plaćanje);
			// pozivalac je async void handler tastature — neuhvaćen izuzetak bi oborio proces.
			_recipientPhoneInputViewModel.HandlePhoneConfirmed = async phoneNumber => {
						try {
							await FireInSession(session, _workflowMachine.RecipientPhoneEntered, phoneNumber).ConfigureAwait(false);
							return true;
						}
						catch (Exception ex) {
							_workflowContext.Logger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToRecipientPhoneInputPage)} exception: {ex.Message}");
							await ResetInSession(session).ConfigureAwait(false);
							return false;
						}
					};
			_recipientPhoneInputViewModel.HandleKeyPressed = key => {
						_operationCountdownTimer.ResetCounter();
						return ValueTask.CompletedTask;
					};
			_recipientPhoneInputViewModel.Activate();
			_currentWorkflowViewModel = _recipientPhoneInputViewModel;

			this.CurrentPage = _recipientPhoneInputViewModel;
			SetCurrentStep(2);
			_operationCountdownTimer.Start();
			_audioPlayer.Play(MakeCurrentLanguagePath("enter-phone-number.mp3"));

			return ValueTask.CompletedTask;
		}

		// ──────────────────────────────────────────────────────────────────────
		// OnEntry handler za WorkflowState.RecipientPayment.
		// Triger: RecipientPaymentRequired (iz ValidateRecipientAndRoute kad amount > 0
		// i terminal je dostupan). Radi POS sale, payment confirmation i (na uspehu) RRPP-2
		// da dobije door number za pickup.
		// ──────────────────────────────────────────────────────────────────────
		public async ValueTask GoToRecipientPaymentPage()
		{
			var contextLogger = _workflowContext.Logger;
			// Dokle je tok stigao — određuje šta catch radi sa uplatom.
			PaymentConfirmation? unconfirmedPayment = null;	// kartica naplaćena, a ishod potvrde serveru još nije obrađen
			bool paymentConfirmed = false;					// server je evidentirao uplatu
			try
			{
				_currentWorkflowViewModel?.Deactivate();
				var amount = _workflowContext.PaymentAmount!.Value;
				var unlockCode = _workflowContext.UnlockCode!;
				var telephoneNumber = _workflowContext.RecipientPhoneNumber!;

				_recipientPaymentViewModel.Amount = amount;
				_recipientPaymentViewModel.Activate();
				_currentWorkflowViewModel = _recipientPaymentViewModel;

				if (!_posClient!.IsConnected)
				{
					var ctsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
					bool connected = false;
					try {
						connected = await _posClient.CheckPresence(ctsTimeout.Token).ConfigureAwait(false);
					}
					catch (Exception ex) {
						contextLogger.Error($"{nameof(GoToRecipientPaymentPage)}: paket sa kodom {unlockCode} zahteva plaćanje, ali POS terminal nije dostupan: {ex.Message}");
					}
					if (!connected) {
						contextLogger.Error($"{nameof(GoToRecipientPaymentPage)}: POS terminal nije u funkciji.");
						await DisplayMessage(this["RecipientPaymentPage.POSTerminalError"], MessageType.Error).ConfigureAwait(false);
						await _workflowMachine.FireAsync(WorkflowTrigger.RecipientPaymentDeclined).ConfigureAwait(false);
						return;
					}
				}

				// Bez opšteg tajmera: Reset je u RecipientPayment ignorisan (Home race), a zaglavljenu prodaju prekida POS
				// (FunctionTimeout u ECR-u). Ranije pozvan Start(PaymentTimeout) ionako je odmah zaustavljala promena stranice.
				_operationCountdownTimer.Stop();
				this.CurrentPage = _recipientPaymentViewModel;

                // Ovaj poziv može trajati do 150 sekundi - možda treba ponuditi "otkaži uplatu karticom"?
                var (status, transactionFlag, transactionNumber, tidNumber) = await _posClient!.Sale(amount,
																processingMessage => { _recipientPaymentViewModel.InfoMessage = _posMessageLocalizer.Localize(processingMessage); return ValueTask.CompletedTask; },
																						CancellationToken.None).ConfigureAwait(false);
				if(status != PosClient.ResponseCodes.Success)
				{
					if(status == PosClient.ResponseCodes.TransactionCanceled) {
						// korisnik je prekinuo plaćanje na samom POS terminalu
						contextLogger.Information($"{nameof(GoToRecipientPaymentPage)}: {unlockCode}: korisnik je prekinuo plaćanje na POS terminalu.");
						await DisplayMessage(_posMessageLocalizer.Localize(PosClient.CanceledByUserMessage), MessageType.Information).ConfigureAwait(false);
					}
					else {
						// neki hw problem, ne može da pristupi pos terminalu
						contextLogger.Error($"{nameof(GoToRecipientPaymentPage)}: {unlockCode}: greška u radu POS terminala: status={status}");
						await DisplayMessage(this["RecipientPaymentPage.POSTerminalError"], MessageType.Error).ConfigureAwait(false);
					}
					await _workflowMachine.FireAsync(WorkflowTrigger.RecipientPaymentDeclined).ConfigureAwait(false);
					return;
				}

				if(transactionFlag == TransactionFlagCodes.TransactionAcceptedWithAuthorization || transactionFlag == TransactionFlagCodes.TransactionAcceptedWithoutAuthorizationCode)
				{
					transactionNumber ??= string.Empty;
					tidNumber ??= string.Empty;
					unconfirmedPayment = new PaymentConfirmation(unlockCode, transactionNumber, tidNumber, amount);
					// Šaljemo potvrdu serveru da je izvršena uplata
					var (paymentId, pcErrorMessage, recoverableError) = await _serviceClient.PaymentConfirmation(unlockCode, transactionNumber, amount, tidNumber, CancellationToken.None).ConfigureAwait(false);
					// Polly ponavlja POST posle izgubljenog odgovora (5xx, prekinuta veza), a server je uplatu možda već evidentirao:
					// ponovljeni zahtev tada dobija 400 „već evidentirana uplata“. Uplata je na serveru, pa se nastavlja kao posle
					// uspešne potvrde (isto prepoznaje i pozadinski red, PaymentConfirmationSender.Classify).
					// Poruka je vezana za paket, ne za transakciju: ako je server evidentirao raniju uplatu iz reda (kupac platio
					// ponovo pre nego što je red stigao da je pošalje), ova naplata je dupla — Error sa svim podacima za povraćaj.
					bool alreadyRecorded = !paymentId.HasValue &&
											PaymentConfirmationSender.Classify(paymentId, pcErrorMessage, recoverableError) == ConfirmationSendResult.AlreadyDelivered;
					if (alreadyRecorded) {
						contextLogger.Error($"{nameof(GoToRecipientPaymentPage)}: {unlockCode}: server je uplatu već evidentirao ({pcErrorMessage}); nastavlja se kao posle " +
											$"uspešne potvrde. Ako server ima drugu transakciju, ova naplata je dupla — proveriti i vratiti novac " +
											$"(UnlockCode={unconfirmedPayment.UnlockCode}, TransactionNumber={unconfirmedPayment.TransactionNumber}, " +
											$"Tid={unconfirmedPayment.TidNumber}, Amount={unconfirmedPayment.Amount}).");
					}
					if (!paymentId.HasValue && !alreadyRecorded) {
						contextLogger.Error($"{nameof(GoToRecipientPaymentPage)}: {unlockCode}: potvrda uplate nije uspela: {pcErrorMessage}");
						bool persisted = false;
						if (recoverableError) {
							contextLogger.Information($"{nameof(GoToRecipientPaymentPage)}: {unlockCode}: slanje potvrde će biti pokušavano dok ne uspe.");
							persisted = await _paymentConfirmationSender.AddPaymentConfirmation(unconfirmedPayment).ConfigureAwait(false);
							if (!persisted) {
								LogUnsavedPaymentConfirmation(contextLogger, unconfirmedPayment);
							}
						}
						unconfirmedPayment = null;	// ishod potvrde obrađen: u listi za slanje, ili ju je server odbio
						// Recoverable („pokušajte ponovo za 2 minuta“) samo ako potvrda preživljava restart; inače „pozovite podršku“.
						var messageId = persisted ? "RecipientPaymentPage.PaymentConfirmationError.Recoverable" :
													"RecipientPaymentPage.PaymentConfirmationError.Unrecoverable";
						await DisplayMessage(this[messageId], MessageType.Error).ConfigureAwait(false);
						await _workflowMachine.FireAsync(WorkflowTrigger.RecipientPaymentDeclined).ConfigureAwait(false);
						return;
					}
					unconfirmedPayment = null;
					paymentConfirmed = true;

					// uspešno prijavljena uplata
					contextLogger.Information($"{nameof(GoToRecipientPaymentPage)}: {unlockCode}: potvrda uplate je uspela. Returned PaymentID: {paymentId}");

					// get box number:
					(int? doorNumber, decimal? amountNew, string? rrppErrorMessage) = await _serviceClient.RecipientRequestPackagePickup(telephoneNumber,
																																		int.Parse(unlockCode),
																																		CancellationToken.None)
																							.ConfigureAwait(false);
					if (doorNumber == null) {
						contextLogger.Error($"{nameof(GoToRecipientPaymentPage)}: {unlockCode}: zahtev za preuzimanjem paketa nije uspeo: {rrppErrorMessage}");
						await DisplayMessage(_serverMessageLocalizer.Localize(rrppErrorMessage) ?? this["RecipientPaymentPage.RecipientRequestPackagePickupFailed.Default"], MessageType.Error).ConfigureAwait(false);
						await _workflowMachine.FireAsync(WorkflowTrigger.RecipientPaymentDeclined).ConfigureAwait(false);
						return;
					}
					_workflowContext.RecipientPackageDoorNumber = (int)doorNumber;
					await _workflowMachine.FireAsync(WorkflowTrigger.RecipientPaymentApproved).ConfigureAwait(false);
					return;
				}
				else if(transactionFlag == TransactionFlagCodes.TransactionRefused)
				{
					// odbijena naplata
					contextLogger.Error($"{nameof(GoToRecipientPaymentPage)}: {unlockCode}: greška u toku POS transakcije ({transactionFlag})");
					await DisplayMessage(this["RecipientPaymentPage.PaymentRefused"], MessageType.Error).ConfigureAwait(false);
				}
				else {
					// neuspešna transakcija - greška u komunikaciji s bankom ili nešto drugo
					contextLogger.Error($"{nameof(GoToRecipientPaymentPage)}: {unlockCode}: greška u toku POS transakcije ({transactionFlag})");
					await DisplayMessage(this["RecipientPaymentPage.PaymentFailed"], MessageType.Error).ConfigureAwait(false);
				}
				await _workflowMachine.FireAsync(WorkflowTrigger.RecipientPaymentDeclined).ConfigureAwait(false);
			}
			catch (Exception ex) {
				// Ne Reset: RecipientPayment ignoriše Reset (Home ne sme da prekine plaćanje), pa bi kiosk ostao na ekranu
				// plaćanja do restarta. Izlaz je RecipientPaymentDeclined; poruka i sudbina uplate zavise od faze toka.
				contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToRecipientPaymentPage)} exception: {ex.Message}");
				string messageId;
				if (unconfirmedPayment != null) {
					// Kartica naplaćena, a server ne zna: potvrda ide u listu za slanje, kao kod prolazne greške potvrde.
					if (await _paymentConfirmationSender.AddPaymentConfirmation(unconfirmedPayment).ConfigureAwait(false)) {
						contextLogger.Error($"{nameof(GoToRecipientPaymentPage)}: {unconfirmedPayment.UnlockCode}: naplaćeno, a potvrda nije poslata — dodata u listu za slanje.");
						messageId = "RecipientPaymentPage.PaymentConfirmationError.Recoverable";
					}
					else {
						LogUnsavedPaymentConfirmation(contextLogger, unconfirmedPayment);
						messageId = "RecipientPaymentPage.PaymentConfirmationError.Unrecoverable";	// „Pozovite tehničku podršku“
					}
				}
				else if (paymentConfirmed) {
					// Server je evidentirao uplatu: pri ponovnom unosu koda kupac ne plaća i dobija vrata (kao kad RRPP-2 ne uspe).
					messageId = "RecipientPaymentPage.RecipientRequestPackagePickupFailed.Default";
				}
				else {
					messageId = "RecipientPaymentPage.POSTerminalError";
				}
				try {
					await DisplayMessage(this[messageId], MessageType.Error).ConfigureAwait(false);
				}
				catch (Exception exMessage) {
					contextLogger.Error($"{nameof(GoToRecipientPaymentPage)}: prikaz poruke nije uspeo: {exMessage.Message}");
				}
				await _workflowMachine.FireAsync(WorkflowTrigger.RecipientPaymentDeclined).ConfigureAwait(false);
			}
		}

		// Kartica naplaćena, a potvrda nije upisana na disk: postoji samo u memoriji (šalje se dok program radi, restart je
		// gubi). Log sadrži sve što podrška treba da uplatu prijavi ručno.
		private static void LogUnsavedPaymentConfirmation(Serilog.ILogger contextLogger, PaymentConfirmation payment)
			=> contextLogger.Error($"{nameof(GoToRecipientPaymentPage)}: {payment.UnlockCode}: naplaćeno, a potvrda NIJE upisana na disk — " +
									$"prijaviti ručno ako ne bude poslata (UnlockCode={payment.UnlockCode}, TransactionNumber={payment.TransactionNumber}, " +
									$"Tid={payment.TidNumber}, Amount={payment.Amount}).");

		// ──────────────────────────────────────────────────────────────────────
		// OnEntry handler za WorkflowState.RecipientFiscalReceipt.
		// Triger: RecipientPaymentApproved (kad je IsPenPOSDeviceAvailable=true).
		// Prikazuje fiscal receipt QR kod, čeka korisnika da klikne Završi.
		// ──────────────────────────────────────────────────────────────────────
		public async ValueTask GoToRecipientFiscalReceiptPage()
        {
            _currentWorkflowViewModel?.Deactivate();
            var amount = _workflowContext.PaymentAmount!.Value;
            var contextLogger = _workflowContext.Logger;
            var session = CurrentSession;
            try
            {
                _recipientFiscalReceiptViewModel.FuncFetchFiscalReceiptImage = async (ct) =>
									{
										try {
											var invoiceResult = await _penPosServiceClient!.ProcessInvoice(amount, ct).ConfigureAwait(false);
											if (string.IsNullOrEmpty(invoiceResult?.VerificationUrl)) {
												contextLogger.Error($"{nameof(GoToRecipientFiscalReceiptPage)}: odgovor na zahtev za dobijanje fiskalnog racuna nije dobijen.");
                                                return null;
                                            }
											contextLogger.Information($"{nameof(GoToRecipientFiscalReceiptPage)}: generisanje QR koda za fiskalni racun {invoiceResult?.InvoiceNumber}");
                                            var qrCodeImage = QrCodeService.GenerateQRCode(invoiceResult!.VerificationUrl);	// v4: PNG bajtovi
											return qrCodeImage;
                                        }
										catch (Exception ex) {
											contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToRecipientFiscalReceiptPage)} exception: {ex.Message}");
                                            return null;
                                        }
                                    };
				_recipientFiscalReceiptViewModel.HandleCompleteClicked = async () =>
										{
											await FireInSession(session, WorkflowTrigger.RecipientFiscalReceiptCompleted).ConfigureAwait(false);
										};

                _recipientFiscalReceiptViewModel.Activate();
                _currentWorkflowViewModel = _recipientFiscalReceiptViewModel;
                // Bez opšteg tajmera: ekran ima svoje rokove (dohvatanje računa, 10 s kad račun ne stigne, skeniranje QR-a) i
                // svi završavaju otvaranjem vrata. Opšti tajmer bi slao Reset → Start, pa bi plaćen kupac ostao bez paketa.
                // (Ranije pozvan Start pre promene stranice odmah je zaustavljao OnDeactivatingPage.)
                _operationCountdownTimer.Stop();
                this.CurrentPage = _recipientFiscalReceiptViewModel;
            }
            catch (Exception ex)
            {
                contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToRecipientFiscalReceiptPage)} exception: {ex.Message}");
                await FireResetAsync().ConfigureAwait(false);
            }
        }

		// ──────────────────────────────────────────────────────────────────────
		// OnEntry handler za WorkflowState.RecipientPackagePickup.
		// Triger: RecipientNoPaymentNeeded, RecipientPaymentApproved (no PenPOS), ili RecipientFiscalReceiptCompleted.
		// Otvara vrata, šalje pickup confirmation serveru, čeka da korisnik zatvori vrata.
		// Kad zatvori → RecipientPickupComplete. Kad ne otvori (ili vrata ne javljaju zatvaranje) → RecipientPickupDoorActionFailed nakon error-a.
		// ──────────────────────────────────────────────────────────────────────
        public async ValueTask GoToRecipientPackagePickupPage()
		{
			_currentWorkflowViewModel?.Deactivate();
			var unlockCode = _workflowContext.UnlockCode!;
			var telephoneNumber = _workflowContext.RecipientPhoneNumber!;
			var packageDoorNumber = _workflowContext.RecipientPackageDoorNumber!.Value;
			var contextLogger = _workflowContext.Logger;
			var session = CurrentSession;
			var remotePickup = _workflowContext.RemotePickup;	// null u lokalnom toku
			bool trackingStarted = false;
			try
			{
				_operationCountdownTimer.Stop();	// No plain timeouts here; the timeout will eventually come from a door not being locked back.

				// Preuzimanje na zahtev Agenta: faza otvaranja se upisuje pre otvaranja (restart posle otvaranja tako ne
				// prijavljuje da ništa nije otvoreno). Vrata se otvaraju i kad upis ne uspe: korisnik je pred paketomatom
				// i uneo je kod prisustva (odluka korisnika 28.10.2026).
				if (remotePickup != null && !await remotePickup.OpeningRecordedAsync().ConfigureAwait(false)) {
					contextLogger.Error($"{nameof(GoToRecipientPackagePickupPage)}: {unlockCode}: faza otvaranja preuzimanja na zahtev Agenta nije upisana u dnevnik; vrata se ipak otvaraju.");
				}

				DoorStatusCode? doorStatusCode = null;
				int retries = 0;
				do {
					if (retries > 0) {
						await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
					}
					doorStatusCode = await OpenDoorSingle(packageDoorNumber,
																				TimeSpan.FromMilliseconds(_configuration.General.DoorStatusResponseTimeout),
																				CancellationToken.None).ConfigureAwait(false);
				} while (doorStatusCode?.IsOpen != true && ++retries < 2);

				if (doorStatusCode?.IsOpen != true)
				{
					if (remotePickup != null) {
						await remotePickup.CompleteOpeningAsync(RemotePickupReport.OutcomeOf(doorStatusCode), confirmation: null).ConfigureAwait(false);
					}
					await DisplayMessage(this["General.DoorOpeningFailed.User.Template"].Replace("{doorNumber}", packageDoorNumber.ToString()), MessageType.Error).ConfigureAwait(false);
					await HandleDoorActionFailure(packageDoorNumber,
													doorStatusCode,
													_configuration.General.UserMessageDisplayTimeInSeconds,
													() => new ValueTask(_workflowMachine.FireAsync(WorkflowTrigger.RecipientPickupDoorActionFailed)),
													contextLogger,
													CancellationToken.None)
							.ConfigureAwait(false);
					return;
				}

				// Door is open: we consider the package delivered.
				bool confirmed = false;
				bool queued = false;
				bool recoverable = false;
				string? errorMessage = null;
				try {
					(confirmed, errorMessage, recoverable) = await _serviceClient.RecipientPackagePickupConfirm(int.Parse(unlockCode), telephoneNumber, packageDoorNumber, CancellationToken.None)
																	.ConfigureAwait(false);
					// prolazna greška: potvrda ide u red (šalje se kad server proradi); odbijanje (400): samo zapis u logu, kao ranije
					queued = !confirmed && recoverable && await QueueServerConfirmation(ServerConfirmation.ForRecipientPickup(int.Parse(unlockCode), telephoneNumber, packageDoorNumber), contextLogger).ConfigureAwait(false);
					if (!confirmed && !queued) {
						contextLogger.Error($"{nameof(GoToRecipientPackagePickupPage)}: {unlockCode}: slanje potvrde o preuzimanju paketa nije uspelo. Detalji: {errorMessage}");
					}
				}
				catch (Exception ex) {
					contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToRecipientPackagePickupPage)} exception: {ex.Message}");
				}
				// Ishod za Agenta je konačan posle potvrde serveru; ne čeka zatvaranje vrata.
				if (remotePickup != null) {
					await remotePickup.CompleteOpeningAsync(RecipientPickupOutcomes.OpenConfirmed, RemotePickupReport.ConfirmationOf(confirmed, queued)).ConfigureAwait(false);
				}

				_ = TrackDoorStatus(packageDoorNumber, DoorStatusCode.Closed,
											async statusCode =>
											{
												if (statusCode?.IsClosed != true) {
													await HandleDoorActionFailure(packageDoorNumber,
																					statusCode,
																					_configuration.General.UserMessageDisplayTimeInSeconds,
																					() => new ValueTask(FireInSession(session, WorkflowTrigger.RecipientPickupDoorActionFailed)),
																					contextLogger,
																					CancellationToken.None)
															.ConfigureAwait(false);
												}
												else {
													await FireInSession(session, WorkflowTrigger.RecipientPickupComplete).ConfigureAwait(false);
												}
											});
				trackingStarted = true;

				_recipientPackagePickupViewModel.PickupInstructions = this["RecipientPackagePickupPage.PickupInstructions"]
							.Replace("{doorNumber}", packageDoorNumber.ToString());
				_recipientPackagePickupViewModel.Activate();
				_currentWorkflowViewModel = _recipientPackagePickupViewModel;
				this.CurrentPage = _recipientPackagePickupViewModel;
				SetCurrentStep(3);
				_audioPlayer.Play(MakeCurrentLanguagePath("recipient-pick-up-package-close-door.mp3"));
			}
			catch (Exception ex) {
				// Pre praćenja vrata nema izlaznog trigera (Home ugašen, opšti tajmer zaustavljen) — kiosk bi ostao zaglavljen.
				// Posle pokretanja izlaz daje praćenje (zatvaranje ili istek); Reset bi tada prekinuo započet tok.
				contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(GoToRecipientPackagePickupPage)} exception: {ex.Message}");
				if (!trackingStarted) {
					await FireResetAsync().ConfigureAwait(false);
				}
			}
		}
	}
}
