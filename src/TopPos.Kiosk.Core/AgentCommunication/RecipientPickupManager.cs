using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.Workflow;
using TopPos.Service;

namespace TopPos.Kiosk.AgentCommunication
{
	/// <summary>Veza preuzimanja na zahtev Agenta sa workflow-om (u aplikaciji <see cref="WorkflowRecipientPickupHost"/>).</summary>
	public interface IRecipientPickupHost
	{
		/// <summary>Atomska rezervacija kiosk-a (<see cref="KioskLeaseMode.WorkflowSession"/>, uloga Client); null = zauzet.</summary>
		KioskLease? TryReserve(string source);

		/// <summary>Oslobađa rezervaciju koja nije počela.</summary>
		void Release(KioskLease lease);

		/// <summary>Triger <see cref="WorkflowTrigger.RecipientRemotePickupStarted"/>; true kad je mašina ušla u <see cref="WorkflowState.RecipientRemoteValidating"/>.</summary>
		Task<bool> StartAsync(KioskLease lease, RecipientPickupOperation operation, RecipientRemotePickupRequest request);
	}

	/// <summary>Granice unosa sa ekrana kioska (<c>General.MinimumUnlockCodeLength</c>..<c>MaximumUnlockCodeLength</c>), iste za telefon i šifru.</summary>
	public sealed record RecipientPickupSettings(int MinimumLength, int MaximumLength);

	/// <summary>
	/// Prijem komande <see cref="AgentCommands.RecipientPickup"/>: provera payload-a, uslovi kioska, rezervacija i ulazak u
	/// workflow; ponovljen zahtev iz dnevnika. Tok posle prijema vodi <see cref="RecipientPickupOperation"/> preko
	/// <see cref="WorkflowRecipientPickupHost"/>, a ishod Agent čita kroz <see cref="AgentCommands.GetCommandResult"/>.
	///
	/// Metode komandi se pozivaju pod lock-om dispatcher-a: odluka je sinhrona, a upis i ulazak u workflow idu u odložen
	/// odgovor (<see cref="AgentCommandResult.Pending"/>). Odbijanja ne ostavljaju zapis u dnevniku.
	/// </summary>
	public sealed class RecipientPickupManager
	{
		private readonly AgentOperationJournal _journal;
		private readonly KioskStatusSource _status;
		private readonly IRecipientPickupHost _host;
		private readonly RecipientPickupSettings _settings;
		private readonly ILogger? _logger;

		private readonly Lock _lock = new();
		private (string RequestId, Task<AgentCommandResult> Task)? _starting;

		public RecipientPickupManager(AgentOperationJournal journal, KioskStatusSource status, IRecipientPickupHost host, RecipientPickupSettings settings,
									  ILogger? logger)
		{
			_journal = journal;
			_status = status;
			_host = host;
			_settings = settings;
			_logger = logger;
		}

		/// <summary>Oporavak zapisa posle pada procesa, za <see cref="AgentOperationJournal.Initialize"/>.</summary>
		public static IReadOnlyDictionary<string, Func<AgentOperationRecord, AgentOperationRecord>> Recoverers { get; }
			= new Dictionary<string, Func<AgentOperationRecord, AgentOperationRecord>> {
				[AgentCommands.RecipientPickup] = RecipientPickupOperation.RecoverRecord,
			};

		/// <summary>
		/// Telefon i šifra kao na tastaturi ekrana: samo cifre, zadate dužine; šifra staje u <c>int</c>. Kod prisustva: samo cifre,
		/// <see cref="RecipientPickupLimits.MinPresenceCodeLength"/>..<see cref="RecipientPickupLimits.MaxPresenceCodeLength"/>.
		/// Null kad je ispravno.
		/// </summary>
		public string? ValidatePayload(RecipientPickupPayload payload)
		{
			int min = Math.Max(1, _settings.MinimumLength);
			int max = _settings.MaximumLength;
			if (!IsDigits(payload.Phone, min, max)) {
				return $"Phone mora imati {min}..{max} cifara.";
			}
			if (!IsDigits(payload.Code, min, max)) {
				return $"Code mora imati {min}..{max} cifara.";
			}
			if (!int.TryParse(payload.Code, out _)) {
				return "Code je prevelik broj.";
			}
			if (!IsDigits(payload.PresenceCode, RecipientPickupLimits.MinPresenceCodeLength, RecipientPickupLimits.MaxPresenceCodeLength)) {
				return $"PresenceCode mora imati {RecipientPickupLimits.MinPresenceCodeLength}..{RecipientPickupLimits.MaxPresenceCodeLength} cifara.";
			}
			return null;
		}

		private static bool IsDigits(string? value, int min, int max)
			=> value != null && value.Length >= min && value.Length <= max && value.All(char.IsAsciiDigit);

		/// <summary>Otisak za dnevnik: heš, jer sadržaj zahteva nosi telefon, šifru i kod prisustva.</summary>
		public static string HashFingerprint(string fingerprint)
			=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint))).ToLowerInvariant();

		public AgentCommandResult Begin(string requestId, RecipientPickupPayload payload, string fingerprint)
		{
			if (KioskReadiness.Rejection(_status.Current) is { } rejection) {
				return rejection;
			}
			if (_status.Current.Operational != true) {
				return new(AgentResultCodes.NotOperational, "Paketomat nije operativan.");
			}
			var lease = _host.TryReserve($"Agent {AgentCommands.RecipientPickup} ({requestId})");
			if (lease == null) {
				var status = _status.Current;
				return KioskReadiness.Rejection(status)
					?? new(AgentResultCodes.Busy, $"Kiosk je zauzet: {status.WorkflowState}, vlasnik {KioskReadiness.DescribeRole(status.Role)}.");
			}
			var operation = new RecipientPickupOperation(requestId, fingerprint, payload.PresenceCode, _journal, _status, _logger);
			// Zapis je u memoriji dnevnika pre povratka (ponovljen zahtev ga nalazi); upis na disk ide van ove niti.
			var starting = StartAsync(operation, lease, new RecipientRemotePickupRequest(requestId, payload.Phone, payload.Code));
			lock (_lock) {
				_starting = (requestId, starting);
			}
			return AgentCommandResult.Deferred(starting);
		}

		/// <summary>Ponovljen <see cref="AgentCommands.RecipientPickup"/>: tekuće stanje iz dnevnika, bez novog toka.</summary>
		public AgentCommandResult? Repeat(string requestId, string fingerprint)
		{
			if (!_journal.TryGet(requestId, out var record)) {
				return null;
			}
			if (record.Command != AgentCommands.RecipientPickup || record.Fingerprint != fingerprint) {
				return new(AgentResultCodes.DuplicateConflict, "Isti RequestId je već prihvaćen sa drugačijim sadržajem.");
			}
			lock (_lock) {
				if (_starting is { } starting && starting.RequestId == requestId && !starting.Task.IsCompleted) {
					return AgentCommandResult.Deferred(starting.Task);
				}
			}
			return new(record.ResultCode, record.Message ?? "Preuzimanje je već primljeno.") { Result = record.Result };
		}

		private async Task<AgentCommandResult> StartAsync(RecipientPickupOperation operation, KioskLease lease, RecipientRemotePickupRequest request)
		{
			try {
				if (!await operation.StartAsync().ConfigureAwait(false)) {
					await operation.AbortAsync(AgentResultCodes.InternalError, "Dnevnik operacija nije dostupan; preuzimanje nije počelo.").ConfigureAwait(false);
					_host.Release(lease);
					return new(AgentResultCodes.InternalError, "Dnevnik operacija nije dostupan; preuzimanje nije počelo.");
				}
				if (!await _host.StartAsync(lease, operation, request).ConfigureAwait(false)) {
					_logger?.LogWarning($"{nameof(RecipientPickupManager)} {operation.RequestId}: mašina nije ušla u {WorkflowState.RecipientRemoteValidating}.");
					await operation.AbortAsync(AgentResultCodes.Busy, "Kiosk je u međuvremenu zauzet; preuzimanje nije počelo.").ConfigureAwait(false);
					return new(AgentResultCodes.Busy, "Kiosk je u međuvremenu zauzet; preuzimanje nije počelo.");
				}
				return new(AgentResultCodes.InProgress, "Preuzimanje je počelo; provera na serveru.") { Result = AgentJson.ToResult(operation.GetResult()) };
			}
			finally {
				lock (_lock) {
					if (_starting?.RequestId == operation.RequestId) {
						_starting = null;
					}
				}
			}
		}
	}

	/// <summary>
	/// <see cref="AgentCommands.RecipientPickup"/>: podrška preuzima paket umesto primaoca. Odmah
	/// <see cref="AgentResultCodes.InProgress"/> (zapis upisan, mašina u proveri); faze i ishod kroz status i
	/// <see cref="AgentCommands.GetCommandResult"/>. Ponovljen zahtev ne pokreće novi tok.
	/// </summary>
	public sealed class RecipientPickupHandler(RecipientPickupManager manager) : IAgentCommandHandler
	{
		public string Command => AgentCommands.RecipientPickup;

		public bool RemembersRequests => true;

		// Sadržaj ide u log kioska u celini (odluka 25.10.2026); u dnevnik samo kao heš otiska.
		public string? TryReadPayload(AgentRequest request, out string content)
		{
			content = "";
			var error = RemoteServicePayloads.TryRead<RecipientPickupPayload>(request, out var payload);
			if (error != null) {
				return error;
			}
			error = manager.ValidatePayload(payload!);
			if (error != null) {
				return error;
			}
			content = $"telefon={payload!.Phone}, šifra={payload.Code}, kod prisustva={payload.PresenceCode}";
			return null;
		}

		public AgentCommandResult Execute(AgentRequest request, string content)
		{
			RemoteServicePayloads.TryRead<RecipientPickupPayload>(request, out var payload);
			return manager.Begin(request.RequestId, payload!, RecipientPickupManager.HashFingerprint(AgentCommandDispatcher.FingerprintOf(request, content)));
		}

		public AgentCommandResult? Repeat(AgentRequest request, string fingerprint)
			=> manager.Repeat(request.RequestId, RecipientPickupManager.HashFingerprint(fingerprint));
	}

	/// <summary>Poruka na ekranu tokom provere zahteva Agenta (prikazuje je <c>RecipientFlowActivator</c>).</summary>
	public enum RecipientPickupNotice
	{
		/// <summary>„Podrška proverava podatke…" dok traje poziv servera; ostaje do sledeće poruke.</summary>
		Checking,
		/// <summary>Vrata su dodeljena: poruka se skida, sledi stranica potvrde.</summary>
		Cleared,
		/// <summary>„Podrška proverava uplatu" (treba platiti; kartica se ne nudi).</summary>
		PaymentCheck,
		/// <summary>Server je odbio ili nije odgovorio: poruka servera ili opšta poruka.</summary>
		Failed,
	}

	/// <summary>
	/// <see cref="IRecipientPickupHost"/> nad workflow-om aplikacije: rezervacija kroz koordinatora, provera na serveru u
	/// OnEntry stanja <see cref="WorkflowState.RecipientRemoteValidating"/>, a faze i ishodi stranice potvrde iz prelaza mašine.
	/// Ako mašina stigne na Start pre ishoda, ishod je <see cref="RecipientPickupOutcomes.OutcomeUnknown"/> u fazi
	/// <see cref="RecipientPickupPhases.Opening"/>, a inače <see cref="RecipientPickupOutcomes.Interrupted"/>.
	/// </summary>
	public sealed class WorkflowRecipientPickupHost : IRecipientPickupHost
	{
		// Triger čeka u redu iza trigera koji je već bio u toku (npr. Reset posle promene operativnosti); duže od ovoga je kvar.
		private static readonly TimeSpan __startTimeout = TimeSpan.FromSeconds(10);

		private readonly WorkflowMachine _machine;
		private readonly KioskOperationCoordinator _coordinator;
		private readonly WorkflowContext _context;
		private readonly Func<string, int, Task<(RecipientPickupLookupResult, int?, decimal?, string?)>> _lookup;
		private readonly Func<RecipientPickupNotice, string?, ValueTask> _notify;
		private readonly ILogger? _logger;
		private readonly Lock _lock = new();
		private (RecipientPickupOperation Operation, TaskCompletionSource<bool> Entered)? _pending;
		private RecipientPickupOperation? _active;

		/// <param name="lookup">Provera na serveru (<see cref="TopPosServiceClient.RecipientRequestPackagePickupDetailed"/>): telefon, šifra.</param>
		/// <param name="notify">Poruka na ekranu; ne sme da baci izuzetak koji bi prekinuo tok (greška se samo loguje).</param>
		public WorkflowRecipientPickupHost(WorkflowMachine machine, KioskOperationCoordinator coordinator, WorkflowContext context,
										   Func<string, int, Task<(RecipientPickupLookupResult, int?, decimal?, string?)>> lookup,
										   Func<RecipientPickupNotice, string?, ValueTask> notify, ILogger? logger)
		{
			_machine = machine;
			_coordinator = coordinator;
			_context = context;
			_lookup = lookup;
			_notify = notify;
			_logger = logger;
			// Posle upisa zahteva u kontekst (konfiguracija mašine se registruje pre ovoga).
			machine.OnEntryFrom(WorkflowState.RecipientRemoteValidating, machine.RecipientRemotePickupStarted, ValidateAsync);
			machine.OnTransitioned(OnTransitioned);
		}

		public KioskLease? TryReserve(string source)
			=> _coordinator.TryBegin(_machine, KioskLeaseMode.WorkflowSession, WorkflowStatusRules.ResolveRole(WorkflowState.RecipientRemoteValidating), source);

		public void Release(KioskLease lease) => _coordinator.ReleaseIfReserved(lease);

		public async Task<bool> StartAsync(KioskLease lease, RecipientPickupOperation operation, RecipientRemotePickupRequest request)
		{
			var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			lock (_lock) {
				_pending = (operation, entered);
			}
			try {
				// Ne čeka se kraj trigera: OnEntry provere čeka server. Ulazak javlja sam OnEntry, pre poziva servera.
				var firing = _machine.FireGuardedAsync(() => _coordinator.IsReserved(lease), _machine.RecipientRemotePickupStarted, request,
														onRejected: () => {
															_coordinator.ReleaseIfReserved(lease);
															entered.TrySetResult(false);
														});
				_ = firing.ContinueWith(t => {
					_logger?.LogError(t.Exception, $"{nameof(WorkflowRecipientPickupHost)}: triger preuzimanja {operation.RequestId} završen greškom.");
					entered.TrySetResult(false);
				}, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
				return await entered.Task.WaitAsync(__startTimeout).ConfigureAwait(false);
			}
			catch (TimeoutException) {
				_logger?.LogError($"{nameof(WorkflowRecipientPickupHost)}: preuzimanje {operation.RequestId} nije ušlo u " +
								  $"{WorkflowState.RecipientRemoteValidating} za {__startTimeout.TotalSeconds:0} s (stanje {_machine.State}).");
				_coordinator.ReleaseIfReserved(lease);
				return false;
			}
			finally {
				lock (_lock) {
					if (_pending?.Entered == entered) {
						_pending = null;
					}
				}
			}
		}

		private async ValueTask ValidateAsync(RecipientRemotePickupRequest request)
		{
			RecipientPickupOperation? operation;
			lock (_lock) {
				operation = _pending is { } pending && pending.Operation.RequestId == request.RequestId ? pending.Operation : null;
				_active = operation;
				if (operation != null) {
					_pending!.Value.Entered.TrySetResult(true);
				}
			}
			if (operation == null) {
				// Reset se ovde ignoriše: bez izlaza bi kiosk ostao u proveri.
				_logger?.LogError($"{nameof(WorkflowRecipientPickupHost)}: ulazak u {WorkflowState.RecipientRemoteValidating} bez operacije {request.RequestId}.");
				await _machine.FireAsync(WorkflowTrigger.RecipientValidationFailed).ConfigureAwait(false);
				return;
			}

			await Notify(RecipientPickupNotice.Checking, null).ConfigureAwait(false);
			WorkflowTrigger trigger;
			try {
				var (result, doorNumber, amount, serverMessage) = await _lookup(request.Phone, int.Parse(request.Code)).ConfigureAwait(false);
				if (result == RecipientPickupLookupResult.Found && (amount ?? 0m) == 0m && doorNumber > 0) {
					_context.RecipientPackageDoorNumber = doorNumber;
					_context.RemotePickup = operation;	// stranica preuzimanja javlja ishod otvaranja i potvrde
					await operation.AwaitAnswerAsync(doorNumber.Value).ConfigureAwait(false);
					await Notify(RecipientPickupNotice.Cleared, null).ConfigureAwait(false);
					trigger = WorkflowTrigger.RecipientNoPaymentNeeded;
				}
				else if (result == RecipientPickupLookupResult.Found && amount > 0m) {
					await operation.CompleteAsync(RecipientPickupOutcomes.PaymentRequired, amount: amount).ConfigureAwait(false);
					await Notify(RecipientPickupNotice.PaymentCheck, null).ConfigureAwait(false);
					trigger = WorkflowTrigger.RecipientPaymentRequired;
				}
				else {
					// Found bez vrata i bez iznosa ne bi trebalo da stigne; za Agenta je to odbijanje bez poruke.
					var outcome = result == RecipientPickupLookupResult.NoResponse ? RecipientPickupOutcomes.ServerUnavailable : RecipientPickupOutcomes.ServerRejected;
					await operation.CompleteAsync(outcome, serverMessage: serverMessage).ConfigureAwait(false);
					await Notify(RecipientPickupNotice.Failed, serverMessage).ConfigureAwait(false);
					trigger = WorkflowTrigger.RecipientValidationFailed;
				}
			}
			catch (Exception ex) {
				// Izlaz iz provere ne sme da zavisi od Reset-a (ignoriše se) — inače bi kiosk ostao zaglavljen.
				_logger?.LogError(ex, $"{nameof(WorkflowRecipientPickupHost)}: provera preuzimanja {request.RequestId} nije uspela");
				await operation.CompleteAsync(RecipientPickupOutcomes.ServerUnavailable).ConfigureAwait(false);
				trigger = WorkflowTrigger.RecipientValidationFailed;
			}
			await _machine.FireAsync(trigger).ConfigureAwait(false);
		}

		private async ValueTask Notify(RecipientPickupNotice notice, string? serverMessage)
		{
			try {
				await _notify(notice, serverMessage).ConfigureAwait(false);
			}
			catch (Exception ex) {
				_logger?.LogError(ex, $"{nameof(WorkflowRecipientPickupHost)}: poruka {notice} nije prikazana");
			}
		}

		// Pre OnEntry-ja odredišta (Stateless redosled); stanje operacije se menja odmah, upis ide u pozadini. Pri povratku na
		// Start kiosk ostaje zauzet dok operacija ne objavi kraj (Finished): nova operacija bi inače zauzela status pre toga.
		// Važi i kad je ishod već postavljen van reda mašine (treći pogrešan kod), a upis još traje.
		private void OnTransitioned(WorkflowState source, WorkflowTrigger trigger, WorkflowState destination)
		{
			RecipientPickupOperation? operation;
			lock (_lock) {
				operation = _active;
				if (destination == WorkflowState.Start && source != WorkflowState.Start) {
					_active = null;
				}
			}
			if (operation == null) {
				return;
			}
			if (source == WorkflowState.RecipientRemoteConfirmation) {
				if (destination == WorkflowState.RecipientPackagePickup) {
					_ = operation.BeginOpeningAsync();
				}
				else if (destination == WorkflowState.Start && !operation.IsCompleted) {
					// Home (Reset) na stranici potvrde znači „Odustani". WrongCode je već postavljen pri poslednjem pogrešnom unosu.
					_ = operation.CompleteAsync(trigger switch {
						WorkflowTrigger.RecipientRemoteNoAnswer => RecipientPickupOutcomes.NoAnswer,
						WorkflowTrigger.RecipientRemoteCodeFailed => RecipientPickupOutcomes.WrongCode,
						_ => RecipientPickupOutcomes.Declined,
					});
				}
			}
			if (destination == WorkflowState.Start && source != WorkflowState.Start) {
				if (!operation.IsCompleted) {
					bool opening = operation.Phase == RecipientPickupPhases.Opening;
					_logger?.LogWarning($"{nameof(WorkflowRecipientPickupHost)}: preuzimanje {operation.RequestId} stiglo na Start iz {source} ({trigger}) bez ishoda.");
					_ = operation.CompleteAsync(opening ? RecipientPickupOutcomes.OutcomeUnknown : RecipientPickupOutcomes.Interrupted);
				}
				// Sesija je ovde već u Ending (koordinator obrađuje prelaz pre ovog handler-a), a čišćenje iz Start OnEntry-ja još nije predato.
				if (_coordinator.Owner is not { } lease || !_coordinator.TryHoldRelease(lease, operation.Finished)) {
					_logger?.LogWarning($"{nameof(WorkflowRecipientPickupHost)}: kraj preuzimanja {operation.RequestId} ne drži kiosk — kiosk je već slobodan.");
				}
			}
		}
	}
}