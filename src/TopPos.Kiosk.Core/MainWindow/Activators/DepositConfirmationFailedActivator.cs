using TopPos.Kiosk.Workflow;
using TopPos.LockController;
using TopPos.Service;

namespace TopPos.Kiosk.ViewModels
{
	/// <summary>
	/// OnEntry za <see cref="WorkflowState.CourierDepositConfirmationFailed"/> i <see cref="WorkflowState.SenderDepositConfirmationFailed"/>:
	/// server nije potvrdio ostavljanje ni posle drugog pokušaja, a paket je u zatvorenim vratima. Server poziciju zauzima tek
	/// potvrdom (de-test S3), pa kurir/pošiljalac ne nastavlja dok ne izabere:
	/// <list type="bullet">
	/// <item>„Ponovi slanje" — kad server nije odbio potvrdu; neuspeh ostavlja isti ekran.</item>
	/// <item>„Prekini i preuzmi paket" — samo kad server sigurno nije upisao ostavljanje; ista vrata se otvaraju, serveru se ništa ne šalje.</item>
	/// <item>„Ostavi paket i nastavi" — kad se ne zna da li je server upisao ostavljanje: paket ostaje (bolje da ga nađe sledeći kurir
	/// nego da kupac otvori prazna vrata).</item>
	/// </list>
	/// Istek roka (<see cref="GeneralConfiguration.OperationTimeout"/>): paket ostaje, ništa se ne šalje, Start. Home je ugašen.
	/// Ishod slanja stiže uz triger (<see cref="DepositConfirmFailure"/>). Rok, dugmad i povrat paketa važe samo dok je sesija
	/// aktivna; povrat paketa i ponovno slanje su praćeni posao sesije (nova sesija ne počinje dok traju).
	/// v4: stranica je ViewModel (bez WPF Page objekata).
	/// </summary>
	internal sealed class DepositConfirmationFailedActivator : WorkflowActivatorBase
	{
		private readonly DepositConfirmationFailedViewModel _depositConfirmationFailedViewModel;

		public DepositConfirmationFailedActivator(IWorkflowHost host) : base(host)
		{
			_depositConfirmationFailedViewModel = new DepositConfirmationFailedViewModel(_languageManager, _logger);
		}

		public ValueTask GoToCourierDepositConfirmationFailedPage(DepositConfirmFailure failure)
			=> GoToDepositConfirmationFailedPage(failure, WorkflowState.CourierDepositConfirmationFailed, _workflowContext.CourierCode!,
												WorkflowTrigger.DepositConfirmationRetrySucceeded,
												WorkflowTrigger.DepositPackageReturned,
												WorkflowTrigger.DepositLeftUnconfirmed,
												WorkflowTrigger.DepositConfirmationFailedTimeout);

		public ValueTask GoToSenderDepositConfirmationFailedPage(DepositConfirmFailure failure)
			=> GoToDepositConfirmationFailedPage(failure, WorkflowState.SenderDepositConfirmationFailed, _configuration.General.SenderDepositCourierCode,
												WorkflowTrigger.SenderConfirmationRetrySucceeded,
												WorkflowTrigger.SenderPackageReturned,
												WorkflowTrigger.SenderLeftUnconfirmed,
												WorkflowTrigger.SenderConfirmationFailedTimeout);

		private ValueTask GoToDepositConfirmationFailedPage(DepositConfirmFailure failure, WorkflowState state, string depositorCode, WorkflowTrigger retrySucceeded,
															WorkflowTrigger packageReturned, WorkflowTrigger leftUnconfirmed, WorkflowTrigger timedOut)
		{
			var barcode = _workflowContext.DepositBarcode!;
			var doorNumber = _workflowContext.DepositDoorNumber!.Value;
			var result = failure.Result;
			var contextLogger = _workflowContext.Logger;
			var session = CurrentSession;
			var details = $"sifra {depositorCode}, barkod {barcode}, vrata {doorNumber}";
			_currentWorkflowViewModel?.Deactivate();

			// Jedna radnja odjednom (dugme ili istek roka); posle odluke koja napušta ekran ništa se više ne prihvata.
			var busy = new SemaphoreSlim(1, 1);
			var left = false;
			CancellationTokenSource? ctsTimeout = null;

			void StartTimeout()
			{
				var cts = new CancellationTokenSource();
				ctsTimeout = cts;
				_ = OnTimeout(cts.Token);
			}

			async Task OnTimeout(CancellationToken cancellationToken)
			{
				try {
					await Task.Delay(TimeSpan.FromMilliseconds(_configuration.General.OperationTimeout), cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException) {
					return;
				}
				// Istek čeka semafor (klik koji je odbačen ne sme da ga potroši); pod semaforom važi samo ako ga radnja u
				// međuvremenu nije otkazala — „Ponovi" tada pokreće nov tajmer.
				await Act("isteklo vreme na ekranu neuspele potvrde ostavljanja", async () => {
					left = true;
					contextLogger.Error($"{nameof(DepositConfirmationFailedActivator)}: niko nije izabrao radnju posle neuspele potvrde ostavljanja ({result}); paket ostaje u vratima, potvrda se ne salje ({details}).");
					await FireInSession(session, timedOut).ConfigureAwait(false);
				}, () => !cancellationToken.IsCancellationRequested, waitForBusy: true).ConfigureAwait(false);
			}

			// allowed: da li dugme važi za trenutni ishod. Komanda kreće posle animacije klika (200 ms), pa klik može da stigne
			// pošto je „Ponovi" promenio ishod i sakrio dugme (npr. „Preuzmi" posle prelaza u „ne zna se").
			async ValueTask Act(string decision, Func<Task> action, Func<bool>? allowed = null, bool waitForBusy = false)
			{
				if (waitForBusy) {
					await busy.WaitAsync().ConfigureAwait(false);
				}
				else if (!await busy.WaitAsync(0).ConfigureAwait(false)) {
					contextLogger.Information($"{nameof(DepositConfirmationFailedActivator)}: {decision} ignorisano — radnja je u toku.");
					return;
				}
				try {
					// odluka je doneta, ili je kiosk napustio ekran spolja (sesija završena) — rok i dugmad tada ne važe, ni u sledećoj
					// sesiji koja je možda baš na istom ekranu
					if (left || !IsSessionActive(session) || _workflowMachine.State != state) {
						contextLogger.Information($"{nameof(DepositConfirmationFailedActivator)}: {decision} ignorisano — odluka je vec doneta ili ekran vise nije aktivan.");
						return;
					}
					if (allowed?.Invoke() == false) {
						contextLogger.Information($"{nameof(DepositConfirmationFailedActivator)}: {decision} ignorisano — ne vazi za ishod {result}.");
						return;
					}
					ctsTimeout?.Cancel();
					contextLogger.Information($"{nameof(DepositConfirmationFailedActivator)}: {decision}.");
					await action().ConfigureAwait(false);
				}
				catch (Exception ex) {
					contextLogger.Error($"{nameof(DepositConfirmationFailedActivator)}: {decision} exception: {ex.Message}");
					left = true;
					await ResetInSession(session).ConfigureAwait(false);
				}
				finally {
					busy.Release();
				}
			}

			_depositConfirmationFailedViewModel.HandleRetryClicked = () => Act("ponovno slanje potvrde ostavljanja", async () => {
				string? serverMessage = null;
				var sending = RunInSession(session, async () => {
									(result, serverMessage) = await SendDepositConfirmationOnce(depositorCode, barcode, doorNumber, result, contextLogger).ConfigureAwait(false);
								});
				if (sending == null) {
					return;		// sesija se završava
				}
				await sending.ConfigureAwait(false);
				if (result == DepositConfirmResult.Confirmed) {
					left = true;
					await FireInSession(session, retrySucceeded).ConfigureAwait(false);
					return;
				}
				_depositConfirmationFailedViewModel.SetResult(result, serverMessage);
				StartTimeout();
			}, () => _depositConfirmationFailedViewModel.CanRetry);
			_depositConfirmationFailedViewModel.HandleReturnPackageClicked = () => Act("kurir/posiljalac uzima paket nazad", async () => {
				left = true;
				contextLogger.Warning($"{nameof(DepositConfirmationFailedActivator)}: ostavljanje nije potvrdjeno ({result}); paket se vraca, serveru se nista ne salje ({details}).");
				// Od otvaranja vrata do zatvaranja (ili zaustavljenog praćenja) povrat je praćen posao sesije: nova sesija ne
				// počinje pre njegovog kraja, ni kad se ova sesija u međuvremenu završi.
				bool? returned = null;
				var returning = RunInSession(session, async () => {
									returned = await ReturnDepositPackage(doorNumber, session, contextLogger).ConfigureAwait(false);
								});
				if (returning == null) {
					return;		// sesija se završava
				}
				await returning.ConfigureAwait(false);
				switch (returned) {
					case true:
						await FireInSession(session, packageReturned).ConfigureAwait(false);
						break;
					case false:
						await ResetInSession(session).ConfigureAwait(false);
						break;
					case null:
						break;	// kiosk je već napustio ekran (sesija završena) — novi Reset bi prekinuo sledeću sesiju
				}
			}, () => _depositConfirmationFailedViewModel.CanReturnPackage);	// nikad kad se ne zna da li je server upisao ostavljanje
			_depositConfirmationFailedViewModel.HandleLeavePackageClicked = () => Act("paket ostaje u vratima bez potvrde", async () => {
				left = true;
				contextLogger.Error($"{nameof(DepositConfirmationFailedActivator)}: ne zna se da li je server upisao ostavljanje; paket ostaje u vratima, potvrda se ne salje ({details}).");
				await FireInSession(session, leftUnconfirmed).ConfigureAwait(false);
			}, () => _depositConfirmationFailedViewModel.CanLeavePackage);

			_depositConfirmationFailedViewModel.SetResult(result, failure.ServerMessage);
			_depositConfirmationFailedViewModel.Activate();
			_currentWorkflowViewModel = _depositConfirmationFailedViewModel;
			this.CurrentPage = _depositConfirmationFailedViewModel;
			// opšti tajmer bi istekom poslao Reset; ovde istek ima svoje značenje (paket ostaje, bez potvrde)
			_operationCountdownTimer.Stop();
			StartTimeout();
			return ValueTask.CompletedTask;
		}

		/// <summary>
		/// Ponovo otvara ista vrata da kurir/pošiljalac uzme paket i čeka da ih zatvori; serveru se ništa ne šalje (ostavljanje
		/// nije upisano). Ako se vrata ne otvore, prijavljuju se kao neispravna i tok se nastavlja. Vraća <c>false</c> ako
		/// vrata nisu zatvorena u roku — pozivalac tada ide na Start, kao kad vrata ostanu otvorena posle ostavljanja — a
		/// <c>null</c> ako je kiosk napustio ekran (sesija završena, praćenje zaustavljeno spolja): tada se ne radi ništa više.
		/// </summary>
		private async ValueTask<bool?> ReturnDepositPackage(int doorNumber, KioskLease? session, Serilog.ILogger contextLogger)
		{
			var messageDisplayTime = _configuration.General.CourierMessageDisplayTimeInSeconds;

			DoorStatusCode? doorStatusCode = null;
			int retries = 0;
			do {
				if (retries > 0) {
					await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
				}
				if (!IsSessionActive(session)) {
					contextLogger.Warning($"{nameof(DepositConfirmationFailedActivator)}: vrata {doorNumber} se ne otvaraju — kiosk je napustio ekran.");
					return null;
				}
				doorStatusCode = await OpenDoorSingle(doorNumber,
																			TimeSpan.FromMilliseconds(_configuration.General.DoorStatusResponseTimeout),
																			CancellationToken.None).ConfigureAwait(false);
			} while (doorStatusCode?.IsOpen != true && ++retries < 2);

			if (doorStatusCode?.IsOpen != true) {
				await DisplayMessage(this["General.DoorOpeningFailed.Courier.Template"].Replace("{doorNumber}", doorNumber.ToString()),
									MessageType.Error, messageDisplayTime).ConfigureAwait(false);
				await HandleDoorActionFailure(doorNumber, doorStatusCode, messageDisplayTime, null, contextLogger, CancellationToken.None).ConfigureAwait(false);
				return true;
			}

			contextLogger.Information($"{nameof(DepositConfirmationFailedActivator)}: vrata {doorNumber} ponovo otvorena; ceka se da se paket preuzme i vrata zatvore.");
			// poruka (i overlay preko dugmadi) stoji dok se vrata ne zatvore
			await DisplayMessage(this["CourierPackagePickupPage.Instructions"], MessageType.Information, 0).ConfigureAwait(false);
			_audioPlayer.Play(MakeCurrentLanguagePath("recipient-pick-up-package-close-door.mp3"));
			DoorStatusCode? statusCode = null;
			var notified = false;
			// Praćenje se završava i kad ga zaustavi povratak na Start (tada bez callback-a) — ranije je ovde čekanje visilo zauvek.
			await TrackDoorStatus(doorNumber, DoorStatusCode.Closed, status => { statusCode = status; notified = true; return ValueTask.CompletedTask; }).ConfigureAwait(false);
			RemoveMessage();
			if (!notified) {
				contextLogger.Warning($"{nameof(DepositConfirmationFailedActivator)}: pracenje vrata {doorNumber} je zaustavljeno pre zatvaranja (kiosk je napustio ekran).");
				return null;
			}

			if (statusCode?.IsClosed != true) {
				await HandleDoorActionFailure(doorNumber, statusCode, messageDisplayTime, null, contextLogger, CancellationToken.None).ConfigureAwait(false);
				return false;
			}
			contextLogger.Information($"{nameof(DepositConfirmationFailedActivator)}: vrata {doorNumber} zatvorena posle preuzimanja paketa.");
			return true;
		}
	}
}
