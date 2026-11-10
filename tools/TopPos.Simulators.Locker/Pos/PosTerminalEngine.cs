using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TopPos.LockerSimulator.Pos
{
	/// <summary>
	/// Simulator POS terminala na nivou protokola. Jedna petlja (<see cref="RunAsync"/>) prima sve što pošalje ECR i
	/// usmerava: ACK/NAK idu sesiji koja ih čeka, nov zahtev prekida tekuću sesiju i pokreće novu.
	/// Sesija je jedna transakcija (async metoda sa sopstvenim CancellationToken-om), pa zakasneli odgovori stare sesije
	/// ne mogu da se pomešaju sa novom (ECR otvara i zatvara port za svaku transakciju).
	/// </summary>
	public sealed class PosTerminalEngine
	{
		private abstract record CustomerAction;
		private sealed record PresentCardAction(SimCard Card) : CustomerAction;
		private sealed record EnterPinAction(string Pin) : CustomerAction;
		private sealed record CancelAction : CustomerAction;

		private sealed class PosSessionAbortedException(string message) : Exception(message);

		private const int MaxPinAttempts = 3;

		private readonly IPosWire _wire;
		private readonly PosSimulatorOptions _options;
		private readonly ILogger? _logger;
		private readonly Channel<CustomerAction> _customerActions = Channel.CreateUnbounded<CustomerAction>();

		private readonly object _ackLock = new();
		private TaskCompletionSource<PosWireEvent>? _pendingAck;

		// Samo petlja RunAsync menja sesiju (v4: i ResetAsync, pod istim _loopLock-om).
		// v4: obrada poruke od ECR-a i ResetAsync se ne preklapaju
		private readonly SemaphoreSlim _loopLock = new(1, 1);
		private CancellationTokenSource? _sessionCts;
		private Task _sessionTask = Task.CompletedTask;
		// Zahtev na koji je poslat pogrešan bajt umesto ACK-a: ECR ga odmah ponavlja, a terminal na ponavljanja ćuti.
		private byte[]? _ignoredRequest;

		// Samo sesija (jedna u jednom trenutku) koristi ove podatke.
		private readonly HashSet<string> _approvedTransactions = new();
		private int _transactionCounter;
		private readonly Random _random = new();

		private volatile PosTerminalStatus _status = PosTerminalStatus.Initial;

		public PosTerminalEngine(IPosWire wire, PosSimulatorOptions options, ILogger? logger = null)
		{
			_wire = wire;
			_options = options;
			_logger = logger;
		}

		public PosFaultSchedule Faults { get; } = new();

		/// <summary>Kupac sam prinese <see cref="AutoCard"/> i unese tačan PIN posle <see cref="PosSimulatorOptions.AutoCustomerDelay"/>.</summary>
		public bool AutoCustomer { get; set; }

		public SimCard AutoCard { get; set; } = SimCard.MastercardContactless;

		public Cancel11Response Cancel11Response { get; set; } = Cancel11Response.Reject;

		public PosSimulatorOptions Options => _options;

		public PosTerminalStatus Status => _status;

		public event Action<PosTerminalStatus>? StatusChanged;

		public event Action<PosLogEntry>? Log;

		public void PresentCard(SimCard card) => _customerActions.Writer.TryWrite(new PresentCardAction(card));

		public void EnterPin(string pin) => _customerActions.Writer.TryWrite(new EnterPinAction(pin));

		/// <summary>Kupac pritisne "odustani" na terminalu.</summary>
		public void CustomerCancel() => _customerActions.Writer.TryWrite(new CancelAction());

		/// <summary>
		/// v4: početno stanje za sledeći E2E scenario (<c>POST /_sim/reset</c>): prekida tekuću sesiju (i zaleđenu posle
		/// FreezeAfterAck), zaboravlja zahtev ignorisan posle WrongAckByte i akcije kupca, briše kvarove i vraća podešavanja.
		/// </summary>
		public async Task ResetAsync()
		{
			await _loopLock.WaitAsync().ConfigureAwait(false);
			try {
				await StopSessionAsync("reset simulatora").ConfigureAwait(false);
				_ignoredRequest = null;
				while (_customerActions.Reader.TryRead(out _)) {
				}
				Faults.Clear();
				AutoCustomer = false;
				AutoCard = SimCard.MastercardContactless;
				Cancel11Response = Cancel11Response.Reject;
				SetStatus(PosTerminalStatus.Initial);
			}
			finally {
				_loopLock.Release();
			}
		}

		public async Task RunAsync(CancellationToken cancellationToken)
		{
			try {
				await foreach (var ev in _wire.Incoming.ReadAllAsync(cancellationToken).ConfigureAwait(false)) {
					try {
						await _loopLock.WaitAsync(cancellationToken).ConfigureAwait(false);
						try {
							await HandleIncomingAsync(ev, cancellationToken).ConfigureAwait(false);
						}
						finally {
							_loopLock.Release();
						}
					}
					catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
						throw;
					}
					catch (Exception ex) {
						Write(PosLogKind.Warning, $"Greška pri obradi poruke od ECR-a: {ex.Message}");
						_logger?.LogError(ex, "POS simulator: greška pri obradi poruke od ECR-a.");
					}
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
			}
			finally {
				await StopSessionAsync(reason: null).ConfigureAwait(false);
			}
		}

		// ==================== Prijem ====================

		private async Task HandleIncomingAsync(PosWireEvent ev, CancellationToken cancellationToken)
		{
			switch (ev.Kind) {
				case PosWireEventKind.Garbage:
					Write(PosLogKind.Warning, $"Odbačen predugačak okvir ({ev.Data.Length} B).");
					return;

				case PosWireEventKind.Control:
					WriteFrame(PosLogKind.EcrToPos, ev.Data);
					if (ev.IsAck || ev.IsNak) {
						TaskCompletionSource<PosWireEvent>? pending;
						lock (_ackLock) {
							pending = _pendingAck;
							_pendingAck = null;
						}
						if (pending == null) {
							Write(PosLogKind.Warning, $"Neočekivan {(ev.IsAck ? "ACK" : "NAK")} — simulator ništa ne čeka.");
						}
						else {
							pending.TrySetResult(ev);
						}
					}
					else {
						Write(PosLogKind.Warning, $"Neočekivan bajt 0x{ev.Data[0]:X2}.");
					}
					return;
			}

			WriteFrame(PosLogKind.EcrToPos, ev.Data);
			var request = PosRequest.Parse(ev.Data);

			if (request.Kind == PosRequestKind.Unknown) {
				Write(PosLogKind.Warning, $"Nepoznat okvir ({request.Error}) — bez odgovora.");
				return;
			}

			if (request.Kind == PosRequestKind.Cancel) {
				// 23 može da stigne u bilo kojoj fazi (i dok terminal šalje 54) — prekidamo sve, ACK, pa 24.
				await StopSessionAsync("ECR otkazuje transakciju (23)").ConfigureAwait(false);
				StartSession(ReplyToCancelAsync, cancellationToken);
				return;
			}

			if (_ignoredRequest != null && ev.Data.AsSpan().SequenceEqual(_ignoredRequest)) {
				Write(PosLogKind.Info, "Ponovljen zahtev posle pogrešnog bajta — terminal ćuti.");
				return;
			}
			_ignoredRequest = null;

			await StopSessionAsync("stigao je nov zahtev").ConfigureAwait(false);

			if (Faults.TryConsume(PosFault.NoResponse)) {
				Write(PosLogKind.Warning, "KVAR: terminal ne odgovara (bez ACK-a).");
				SetStatus(PosTerminalStatus.Initial with { DisplayText = "(terminal ne odgovara)" });
				return;
			}
			if (Faults.TryConsume(PosFault.WrongAckByte)) {
				Write(PosLogKind.Warning, "KVAR: umesto ACK-a šaljem 0x00; na ponovljeni zahtev terminal ćuti.");
				_ignoredRequest = ev.Data;
				await SendRawAsync(new byte[] { 0x00 }, cancellationToken).ConfigureAwait(false);
				return;
			}

			await SendRawAsync(new[] { PosFrame.ACK }, cancellationToken).ConfigureAwait(false);
			StartSession(ct => RunRequestAsync(request, ct), cancellationToken);
		}

		// ==================== Sesija ====================

		private void StartSession(Func<CancellationToken, Task> body, CancellationToken cancellationToken)
		{
			var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			_sessionCts = cts;
			while (_customerActions.Reader.TryRead(out _)) {
				// akcije kupca iz prethodne sesije ne važe
			}
			_sessionTask = Task.Run(() => RunSessionAsync(body, cts.Token));
		}

		private async Task RunSessionAsync(Func<CancellationToken, Task> body, CancellationToken cancellationToken)
		{
			try {
				await body(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
			}
			catch (PosSessionAbortedException ex) {
				Write(PosLogKind.Warning, $"Sesija prekinuta: {ex.Message}");
				SetPhase(PosTerminalPhase.Idle);
			}
			catch (Exception ex) {
				Write(PosLogKind.Warning, $"Greška u sesiji: {ex.Message}");
				_logger?.LogError(ex, "POS simulator: greška u sesiji.");
				SetPhase(PosTerminalPhase.Idle);
			}
		}

		private async Task StopSessionAsync(string? reason)
		{
			var cts = _sessionCts;
			if (cts == null) {
				return;
			}
			_sessionCts = null;
			if (!_sessionTask.IsCompleted) {
				if (reason != null) {
					Write(PosLogKind.Info, $"Prekidam tekuću sesiju: {reason}.");
				}
				cts.Cancel();
			}
			lock (_ackLock) {
				_pendingAck?.TrySetCanceled();
				_pendingAck = null;
			}
			try {
				await _sessionTask.ConfigureAwait(false);
			}
			catch {
				// RunSessionAsync ne baca
			}
			cts.Dispose();
		}

		private async Task RunRequestAsync(PosRequest request, CancellationToken ct)
		{
			SetStatus(new PosTerminalStatus(PosTerminalPhase.Processing, string.Empty, request.Amount, request.SeqNumber, request.TransactionType));

			if (request.Kind == PosRequestKind.Invalid) {
				Write(PosLogKind.Warning, $"Neispravan zahtev: {request.Error}.");
			}
			else {
				Write(PosLogKind.Info, $"Zahtev: {DescribeType(request.TransactionType)}, seq {request.SeqNumber}, iznos {FormatAmount(request.Amount)}.");
			}

			if (Faults.TryConsume(PosFault.FreezeAfterAck)) {
				Write(PosLogKind.Warning, "KVAR: terminal se zamrznuo posle ACK-a (ECR čeka do FunctionTimeout).");
				SetStatus(_status with { Phase = PosTerminalPhase.Frozen, DisplayText = "(zamrznut)" });
				await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
			}

			if (request.Kind == PosRequestKind.Invalid) {
				await PauseAsync(40, ct).ConfigureAwait(false);
				await SendFinalAsync(PosFrame.BuildErrorWithCode("06", "POGRESAN UNOS"), "POGRESAN UNOS", ct).ConfigureAwait(false);
				return;
			}

			switch (request.TransactionType) {
				case PosRequest.TypeReturnTid:
					await PauseAsync(40, ct).ConfigureAwait(false);
					await SendFinalAsync(PosResponses.BuildTidResponse(request.SeqNumber, _options.Tid), $"TID {_options.Tid}", ct).ConfigureAwait(false);
					break;

				case PosRequest.TypeCancelLast:
					await RunCancelLastAsync(request, ct).ConfigureAwait(false);
					break;

				default:
					await RunSaleAsync(request, ct).ConfigureAwait(false);
					break;
			}
		}

		private async Task RunSaleAsync(PosRequest request, CancellationToken ct)
		{
			await PauseAsync(40, ct).ConfigureAwait(false);
			SetPhase(PosTerminalPhase.WaitingForCard);
			await HoldAsync("51", "PROVUCI/UBACI KARTICU", ct).ConfigureAwait(false);

			var action = await WaitForCustomerAsync(PosTerminalPhase.WaitingForCard, () => new PresentCardAction(AutoCard), ct).ConfigureAwait(false);
			if (action is not PresentCardAction { Card: var card }) {
				Write(PosLogKind.Info, action == null ? "Kartica nije prineta na vreme — terminal prekida." : "Kupac je odustao na terminalu.");
				await SendFinalAsync(PosFrame.BuildErrorWithCode("09", "PREKINUTO"), "PREKINUTO", ct).ConfigureAwait(false);
				return;
			}
			Write(PosLogKind.Info, $"Kartica: {card.Name}.");

			SetPhase(PosTerminalPhase.Processing);
			await HoldAsync("54", "OBRADA KARTICE", ct).ConfigureAwait(false);
			await PauseAsync(370, ct).ConfigureAwait(false);

			bool pinUsed = false;
			if (card.RequiresPin) {
				for (int attempt = 1; ; attempt++) {
					SetPhase(PosTerminalPhase.WaitingForPin);
					await HoldAsync("55", "UNOS PIN-a", ct).ConfigureAwait(false);
					var pinAction = await WaitForCustomerAsync(PosTerminalPhase.WaitingForPin, () => new EnterPinAction(_options.Pin), ct).ConfigureAwait(false);
					if (pinAction is not EnterPinAction { Pin: var pin }) {
						// Kartica je već pročitana — terminal tada vraća 10 sa flagom 06 i tekstom PREKINUTO (kao u logovima).
						Write(PosLogKind.Info, pinAction == null ? "PIN nije unet na vreme — terminal prekida." : "Kupac je odustao na terminalu.");
						await SendSaleResultAsync(request, PosResponses.FlagCommError, card, "PREKINUTO", "", pinUsed: false, ct).ConfigureAwait(false);
						return;
					}
					SetPhase(PosTerminalPhase.Processing);
					if (pin == _options.Pin) {
						pinUsed = true;
						await HoldAsync("56", "PIN OK", ct).ConfigureAwait(false);
						break;
					}
					if (attempt >= MaxPinAttempts) {
						await HoldAsync("61", "PIN BLOKIRAN", ct).ConfigureAwait(false);
						await PauseAsync(400, ct).ConfigureAwait(false);
						await HoldAsync("50", "MOLIM SACEKAJ", ct).ConfigureAwait(false);
						await HoldAsync("52", "IZVADI KARTICU", ct).ConfigureAwait(false);
						await SendSaleResultAsync(request, PosResponses.FlagRefused, card, "POGRESAN PIN", "55", pinUsed: true, ct).ConfigureAwait(false);
						return;
					}
					await HoldAsync("61", "POGRESAN PIN", ct).ConfigureAwait(false);
				}
			}

			await PauseAsync(400, ct).ConfigureAwait(false);
			await HoldAsync("57", "MOLIM SACEKAJ", ct).ConfigureAwait(false);
			await PauseAsync(2500, ct).ConfigureAwait(false);
			await HoldAsync("50", "MOLIM SACEKAJ", ct).ConfigureAwait(false);
			await PauseAsync(400, ct).ConfigureAwait(false);
			if (card.IsChip) {
				await HoldAsync("52", "IZVADI KARTICU", ct).ConfigureAwait(false);
			}

			if (Faults.TryConsume(PosFault.NoBankConnection)) {
				Write(PosLogKind.Warning, "KVAR: nema veze sa bankom.");
				await SendSaleResultAsync(request, PosResponses.FlagCommError, card, "KOMUNIKACIONA GRESKA", "", pinUsed, ct).ConfigureAwait(false);
				return;
			}

			var (flag, text, responseCode) = card.Outcome switch {
				SimCardOutcome.Approve => (PosResponses.FlagApprovedWithAuthorization, "ODOBRENO", "00"),
				SimCardOutcome.Expired => (PosResponses.FlagRefused, "KARTICA ISTEKLA", "54"),
				_ => (PosResponses.FlagRefused, "ODBIJENO", "05"),
			};
			await SendSaleResultAsync(request, flag, card, text, responseCode, pinUsed, ct).ConfigureAwait(false);
		}

		private async Task RunCancelLastAsync(PosRequest request, CancellationToken ct)
		{
			// Ponašanje pravog terminala na tip 11 nije snimljeno — bira se u UI-u.
			await PauseAsync(40, ct).ConfigureAwait(false);
			if (Cancel11Response == Cancel11Response.Accept) {
				Write(PosLogKind.Warning, "Zahtev 11 (otkazivanje): odgovaram 10/01 (nepotvrđeno). Kiosk ovo NE sme da protumači kao naplatu.");
				var frame = PosResponses.BuildSaleResponse(new PosResponses.SaleResult(
					request.SeqNumber, PosResponses.FlagApprovedWithoutAuthorization, request.Amount, DateTime.Now, Card: null,
					TransactionNumber: "000000", AuthorizationCode: "", _options.Tid, _options.Mid, "OTKAZANO", "00", PinUsed: false,
					TransactionType: PosRequest.TypeCancelLast));
				await SendFinalAsync(frame, "OTKAZANO", ct).ConfigureAwait(false);
			}
			else {
				Write(PosLogKind.Info, "Zahtev 11 (otkazivanje): odgovaram 26/06 POGRESAN UNOS.");
				await SendFinalAsync(PosFrame.BuildErrorWithCode("06", "POGRESAN UNOS"), "POGRESAN UNOS", ct).ConfigureAwait(false);
			}
		}

		private async Task ReplyToCancelAsync(CancellationToken ct)
		{
			SetPhase(PosTerminalPhase.Processing);
			await SendRawAsync(new[] { PosFrame.ACK }, ct).ConfigureAwait(false);
			await PauseAsync(40, ct).ConfigureAwait(false);
			Write(PosLogKind.Info, "Odgovor 24 na otkazivanje (format nije potvrđen na pravom terminalu).");
			await SendFinalAsync(PosFrame.BuildCancelConfirmation("PREKINUTO"), "PREKINUTO", ct).ConfigureAwait(false);
		}

		private async Task SendSaleResultAsync(PosRequest request, string flag, SimCard card, string text, string fullResponseCode, bool pinUsed, CancellationToken ct)
		{
			bool approved = flag is PosResponses.FlagApprovedWithAuthorization or PosResponses.FlagApprovedWithoutAuthorization;
			string transactionNumber = approved ? (++_transactionCounter % 1_000_000).ToString("000000") : "000000";
			string authorizationCode = approved ? _random.Next(0, 1_000_000).ToString("000000") : "000000";

			var frame = PosResponses.BuildSaleResponse(new PosResponses.SaleResult(
				request.SeqNumber, flag, request.Amount, DateTime.Now, card, transactionNumber, authorizationCode,
				_options.Tid, _options.Mid, text, fullResponseCode, pinUsed));

			if (approved) {
				if (_approvedTransactions.Add($"{request.SeqNumber}|{request.Amount}")) {
					Write(PosLogKind.Info, $"NAPLAĆENO {FormatAmount(request.Amount)} (seq {request.SeqNumber}, transakcija {transactionNumber}).");
				}
				else {
					Write(PosLogKind.Warning, $"DVOSTRUKA NAPLATA: seq {request.SeqNumber} sa iznosom {FormatAmount(request.Amount)} je već naplaćen (transakcija {transactionNumber}).");
				}
				if (Faults.TryConsume(PosFault.SwallowFinalResponse)) {
					Write(PosLogKind.Warning, "KVAR: naplata je odobrena, ali završni odgovor 10 se ne šalje.");
					SetStatus(_status with { Phase = PosTerminalPhase.Idle, DisplayText = text });
					return;
				}
			}
			await SendFinalAsync(frame, text, ct).ConfigureAwait(false);
		}

		// ==================== Kupac ====================

		/// <summary>Čeka akciju kupca koja ima smisla u datoj fazi. Vraća null ako istekne <see cref="PosSimulatorOptions.CardWaitTimeout"/>.</summary>
		private async Task<CustomerAction?> WaitForCustomerAsync(PosTerminalPhase phase, Func<CustomerAction> autoAction, CancellationToken ct)
		{
			using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeoutCts.CancelAfter(_options.CardWaitTimeout);
			try {
				while (true) {
					CustomerAction action;
					if (AutoCustomer) {
						using var autoCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
						autoCts.CancelAfter(_options.AutoCustomerDelay);
						try {
							action = await _customerActions.Reader.ReadAsync(autoCts.Token).ConfigureAwait(false);
						}
						catch (OperationCanceledException) when (!timeoutCts.IsCancellationRequested) {
							action = autoAction();
							Write(PosLogKind.Info, $"Auto kupac: {Describe(action)}.");
						}
					}
					else {
						action = await _customerActions.Reader.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
					}

					bool fits = action is CancelAction
								|| (phase == PosTerminalPhase.WaitingForCard && action is PresentCardAction)
								|| (phase == PosTerminalPhase.WaitingForPin && action is EnterPinAction);
					if (fits) {
						return action;
					}
					Write(PosLogKind.Info, $"Akcija \"{Describe(action)}\" se ne očekuje u ovoj fazi — zanemarena.");
				}
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
				return null;
			}
		}

		private static string Describe(CustomerAction action) => action switch {
			PresentCardAction p => $"kartica {p.Card.Name}",
			EnterPinAction => "PIN",
			_ => "odustajanje",
		};

		// ==================== Slanje ====================

		private async Task HoldAsync(string code, string text, CancellationToken ct)
		{
			SetStatus(_status with { DisplayText = text });
			await SendFrameAsync(PosFrame.BuildHoldWithCode(code, text), ct).ConfigureAwait(false);
		}

		private async Task SendFinalAsync(byte[] frame, string displayText, CancellationToken ct)
		{
			SetStatus(_status with { DisplayText = displayText });
			await SendFrameAsync(frame, ct).ConfigureAwait(false);
			SetPhase(PosTerminalPhase.Idle);
		}

		/// <summary>Šalje okvir i čeka ACK od ECR-a. Na NAK ponavlja (najviše 3 puta); bez ACK-a prekida sesiju.</summary>
		private async Task SendFrameAsync(byte[] frame, CancellationToken ct)
		{
			for (int attempt = 1; ; attempt++) {
				var tcs = new TaskCompletionSource<PosWireEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
				lock (_ackLock) {
					_pendingAck = tcs;
				}
				await SendRawAsync(frame, ct).ConfigureAwait(false);

				PosWireEvent reply;
				try {
					reply = await tcs.Task.WaitAsync(_options.AckTimeout, ct).ConfigureAwait(false);
				}
				catch (TimeoutException) {
					lock (_ackLock) {
						if (_pendingAck == tcs) {
							_pendingAck = null;
						}
					}
					throw new PosSessionAbortedException("ECR nije potvrdio okvir (nema ACK-a).");
				}

				if (reply.IsAck) {
					return;
				}
				if (attempt >= 3) {
					throw new PosSessionAbortedException("ECR je tri puta odgovorio NAK.");
				}
				Write(PosLogKind.Warning, "ECR je odgovorio NAK — ponavljam okvir.");
			}
		}

		private async Task SendRawAsync(byte[] data, CancellationToken ct)
		{
			await _wire.SendAsync(data, ct).ConfigureAwait(false);
			WriteFrame(PosLogKind.PosToEcr, data);
		}

		private Task PauseAsync(int milliseconds, CancellationToken ct)
		{
			ct.ThrowIfCancellationRequested();
			double ms = milliseconds * _options.SpeedFactor;
			return ms < 1 ? Task.CompletedTask : Task.Delay(TimeSpan.FromMilliseconds(ms), ct);
		}

		// ==================== Stanje i log ====================

		private void SetPhase(PosTerminalPhase phase) => SetStatus(_status with { Phase = phase });

		private void SetStatus(PosTerminalStatus status)
		{
			_status = status;
			StatusChanged?.Invoke(status);
		}

		private void WriteFrame(PosLogKind kind, byte[] data)
			=> Write(kind, PosFrame.ToDisplay(data), PosFrame.ToHex(data));

		private void Write(PosLogKind kind, string text, string? hex = null)
		{
			var entry = new PosLogEntry(DateTime.Now, kind, text, hex);
			_logger?.LogInformation("PosSimulator {Kind}: {Text}", kind, text);
			Log?.Invoke(entry);
		}

		private static string DescribeType(string type) => type switch {
			PosRequest.TypeSale => "prodaja (01)",
			PosRequest.TypeReturnTid => "TID (96)",
			PosRequest.TypeCancelLast => "otkazivanje (11)",
			_ => type,
		};

		public static string FormatAmount(long amount) => $"{amount / 100m:N2} RSD";
	}
}
