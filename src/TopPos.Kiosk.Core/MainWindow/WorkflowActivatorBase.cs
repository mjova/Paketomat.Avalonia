using Flatsoft.Common;
using Flatsoft.Common.UI;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using TopPos.Kiosk.Workflow;
using TopPos.LockController;
using Stateless;
using TopPos.PenPosService;
using TopPos.Service;

namespace TopPos.Kiosk.ViewModels
{
	/// <summary>
	/// Bazna klasa za per-flow activator klase (Faza 5). Izlaže članove <b>istih imena</b> kao
	/// originalna polja/metode <see cref="MainWindowViewModel"/>-a (servisi, trackeri, <c>this[key]</c>,
	/// <c>DisplayMessage</c>, <c>CurrentPage</c>, <c>_currentWorkflowViewModel</c>, door helpers, …)
	/// tako da tela premeštenih <c>GoTo*</c> metoda ostaju skoro netaknuta. Sve delegira na <see cref="IWorkflowHost"/>.
	/// </summary>
	internal abstract class WorkflowActivatorBase
	{
		protected readonly IWorkflowHost _host;
		protected WorkflowActivatorBase(IWorkflowHost host) => _host = host;

		// ─── Servisi / trackeri (ista imena kao MWVM polja) ─────────────────────
		protected AppConfiguration _configuration => _host.Configuration;
		protected ILockControllerClient _lockControllerClient => _host.LockControllerClient;

		/// <summary>
		/// Otvaranje vrata kroz isti kontroler, tek kad se završi provera DoorWatcher-a koja je bila u toku: DoorWatcher je
		/// pauziran dok kiosk ima vlasnika, ali poll koji je već krenuo bi se umešao između komandi otvaranja (NCU: Unlock pa
		/// GetState nisu nedeljivi).
		/// </summary>
		protected async ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
		{
			await _host.PauseDoorWatcherAsync().ConfigureAwait(false);
			return await _lockControllerClient.OpenDoorSingle(doorNumber, receiveTimeout, cancellationToken).ConfigureAwait(false);
		}
		protected TopPosServiceClient _serviceClient => _host.ServiceClient;
		protected BarcodeScannerClient _barcodeScannerClient => _host.BarcodeScannerClient;
		protected IPosClient? _posClient => _host.PosClient;
		protected PenPosServiceClient? _penPosServiceClient => _host.PenPosServiceClient;
		protected LanguageManager _languageManager => _host.LanguageManager;
		protected IAudioPlayer _audioPlayer => _host.AudioPlayer;
		protected PaymentConfirmationSender _paymentConfirmationSender => _host.PaymentConfirmationSender;
		protected ServerConfirmationSender _serverConfirmationSender => _host.ServerConfirmationSender;
		protected BarcodeScanTracker _barcodeScanTracker => _host.BarcodeScanTracker;
		protected OperationCountdownTimer _operationCountdownTimer => _host.OperationCountdownTimer;
		protected WorkflowContext _workflowContext => _host.WorkflowContext;
		protected WorkflowMachine _workflowMachine => _host.WorkflowMachine;
		protected ILogger? _logger => _host.Logger;

		// ─── Host operacije (ista imena/signature kao MWVM private metode) ──────
		protected string this[string key] => _host.Localize(key);
		protected ValueTask DisplayMessage(string message, MessageType messageType, int? displayTimeInSeconds = null)
			=> _host.DisplayMessage(message, messageType, displayTimeInSeconds);
		protected void RemoveMessage() => _host.RemoveMessage();
		protected void SetCurrentStep(int stepNumber) => _host.SetCurrentStep(stepNumber);
		protected string MakeCurrentLanguagePath(string filename) => _host.MakeCurrentLanguagePath(filename);
		protected ValueTask FireResetAsync() => _host.FireResetAsync();

		// ─── Sesija ─────────────────────────────────────────────────────────────
		// OnEntry hvata CurrentSession; callback-ovi koji se izvršavaju posle njega (skener, tracker, dugme, rok ekrana, fetch)
		// rade i okidaju trigere samo kroz nju. Trigeri iz samog OnEntry-ja ostaju FireAsync: dok OnEntry radi i dok red nije
		// prazan, nova sesija ne može da počne (WorkflowMachine.TryReserveWhenIdle), pa takav triger stiže najkasnije u Start.
		protected KioskLease? CurrentSession => _host.CurrentSession;
		protected bool IsSessionActive(KioskLease? session) => _host.IsSessionActive(session);
		protected Task FireInSession(KioskLease? session, WorkflowTrigger trigger) => _host.FireInSession(session, trigger);
		protected Task FireInSession<TArg>(KioskLease? session, StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<TArg> trigger, TArg arg)
			=> _host.FireInSession(session, trigger, arg);
		protected ValueTask ResetInSession(KioskLease? session) => new(FireInSession(session, WorkflowTrigger.Reset));
		protected Task? RunInSession(KioskLease? session, Func<Task> work) => _host.RunInSession(session, work);

		/// <summary>
		/// Handler dugmeta vezan za sesiju. Ekran čita handler pre odziva klika (200 ms), pa klik stare sesije posle odziva ne
		/// radi ništa ako se sesija u međuvremenu završila — ni poruku, ni I/O, ni triger.
		/// </summary>
		protected Func<ValueTask> InSession(KioskLease? session, Func<ValueTask> handler)
			=> () => {
				if (IsSessionActive(session)) {
					return handler();
				}
				_logger?.LogInformation($"{GetType().Name}: radnja posle kraja sesije {session?.ToString() ?? "-"} se ignoriše.");
				return ValueTask.CompletedTask;
			};
		protected Task TrackDoorStatus(int doorNumber, DoorStatusCode targetStatusCode, Func<DoorStatusCode?, ValueTask> notifyComplete)
			=> _host.TrackDoorStatus(doorNumber, targetStatusCode, notifyComplete);
		protected ValueTask HandleDoorActionFailure(int doorNumber, DoorStatusCode? unexpectedStatusCode, int messageDisplayTime,
												Func<ValueTask>? gotoPage, Serilog.ILogger contextLogger, CancellationToken cancellationToken)
			=> _host.HandleDoorActionFailure(doorNumber, unexpectedStatusCode, messageDisplayTime, gotoPage, contextLogger, cancellationToken);

		/// <summary>
		/// Potvrda preuzimanja koja nije prošla zbog prolazne greške ide u trajni red i šalje se dok server ne proradi;
		/// primalac tada ne vidi grešku (paket je preuzet). Vraća <c>false</c> ako red nije upisan na disk.
		/// </summary>
		protected async ValueTask<bool> QueueServerConfirmation(ServerConfirmation confirmation, Serilog.ILogger contextLogger)
		{
			if (await _serverConfirmationSender.Add(confirmation).ConfigureAwait(false)) {
				contextLogger.Warning($"{GetType().Name}: server trenutno ne prima potvrdu; potvrda je u redu za slanje i bice poslata naknadno ({confirmation.Kind}, vrata {confirmation.DoorNumber}).");
				return true;
			}
			contextLogger.Error($"{GetType().Name}: potvrda ({confirmation.Kind}, vrata {confirmation.DoorNumber}) nije upisana u red na disku; salje se samo dok program radi.");
			return false;
		}

		/// <summary>
		/// Šalje potvrdu ostavljanja; ako ne uspe, još jednom (osim kad je server odbije — odgovor bi bio isti). Server poziciju
		/// zauzima tek potvrdom, pa kurir/pošiljalac ne nastavlja bez nje. Vraća <c>null</c> kad je server potvrdio; inače ishod i
		/// poruku servera, koji putuju uz triger do ekrana neuspele potvrde (<see cref="DepositConfirmationFailedActivator"/>).
		/// </summary>
		protected async ValueTask<DepositConfirmFailure?> SendDepositConfirmation(string depositorCode, string barcode, int doorNumber, Serilog.ILogger contextLogger)
		{
			var (result, serverMessage) = await SendDepositConfirmationOnce(depositorCode, barcode, doorNumber, null, contextLogger).ConfigureAwait(false);
			if (result is DepositConfirmResult.NotDelivered or DepositConfirmResult.Unknown) {
				(result, serverMessage) = await SendDepositConfirmationOnce(depositorCode, barcode, doorNumber, result, contextLogger).ConfigureAwait(false);
			}
			return result == DepositConfirmResult.Confirmed ? null : new DepositConfirmFailure(result, serverMessage);
		}

		/// <summary>
		/// Jedno slanje potvrde ostavljanja. Poruka „Slanje potvrde serveru…" se prikazuje tek kad slanje traje duže od 1 s (ceo
		/// poziv sa Polly ponavljanjima traje do RequestTimeout-a). „Ne zna se" iz ranijeg pokušaja (<paramref name="previous"/>)
		/// ostaje dok server ne odgovori konačno (potvrda ili odbijanje): kasnije „nije upisano" ne važi za raniji pokušaj.
		/// </summary>
		protected async ValueTask<(DepositConfirmResult, string?)> SendDepositConfirmationOnce(string depositorCode, string barcode, int doorNumber,
																								DepositConfirmResult? previous, Serilog.ILogger contextLogger)
		{
			DepositConfirmResult result;
			string? serverMessage;
			var ctsMessage = new CancellationTokenSource();
			var messageShown = ShowDepositConfirmationSendingMessage(contextLogger, ctsMessage.Token);
			try {
				(result, serverMessage) = await _serviceClient.CourierPackageDepositConfirm(depositorCode, barcode, doorNumber, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception ex) {
				// servisni poziv ne baca; ako ipak baci, ne zna se da li je zahtev stigao do servera
				contextLogger.Error($"{GetType().Name}: {nameof(SendDepositConfirmationOnce)} exception: {ex.Message}");
				(result, serverMessage) = (DepositConfirmResult.Unknown, null);
			}
			finally {
				ctsMessage.Cancel();
				if (await messageShown.ConfigureAwait(false)) {
					RemoveMessage();
				}
				ctsMessage.Dispose();
			}
			if (previous == DepositConfirmResult.Unknown && result == DepositConfirmResult.NotDelivered) {
				result = DepositConfirmResult.Unknown;
			}
			if (result == DepositConfirmResult.Confirmed) {
				contextLogger.Information($"{GetType().Name}: server je potvrdio ostavljanje (sifra {depositorCode}, barkod {barcode}, vrata {doorNumber}).");
			}
			else {
				contextLogger.Warning($"{GetType().Name}: potvrda ostavljanja nije uspela: {result} (sifra {depositorCode}, barkod {barcode}, vrata {doorNumber}, poruka servera: {serverMessage ?? "-"}).");
			}
			return (result, serverMessage);
		}

		private async Task<bool> ShowDepositConfirmationSendingMessage(Serilog.ILogger contextLogger, CancellationToken cancellationToken)
		{
			try {
				await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) {
				return false;
			}
			await DisplayMessage(this["General.DepositConfirmationSending"], MessageType.Information, 0).ConfigureAwait(false);
			contextLogger.Information($"{GetType().Name}: slanje potvrde ostavljanja traje duze od 1 s; prikazana poruka o slanju.");
			return true;
		}

		/// <summary>Setter-only — premeštene metode samo postavljaju tekuću stranicu, ne čitaju je.</summary>
		protected IWorkflowViewModel CurrentPage { set => _host.SetCurrentPage(value); }

		/// <summary>Ime sa underscore namerno: tela premeštenih metoda koriste <c>_currentWorkflowViewModel</c> nepromenjeno.</summary>
		protected IWorkflowViewModel? _currentWorkflowViewModel
		{
			get => _host.CurrentWorkflowViewModel;
			set => _host.CurrentWorkflowViewModel = value;
		}
	}
}
