using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.Workflow;
using TopPos.LockController;

namespace TopPos.Kiosk.AgentCommunication
{
	/// <summary>Veza servisne sesije sa workflow-om i hardverom kioska (u aplikaciji <see cref="WorkflowRemoteServiceHost"/>).</summary>
	public interface IRemoteServiceHost
	{
		/// <summary>Atomska rezervacija kiosk-a za sesiju (<see cref="KioskLeaseMode.WorkflowSession"/>, uloga Agent); null = zauzet.</summary>
		KioskLease? TryReserve(string source);

		/// <summary>Oslobađa rezervaciju koja nije počela.</summary>
		void Release(KioskLease lease);

		/// <summary>Triger <see cref="WorkflowTrigger.RemoteServiceStarted"/>; true kad je mašina ušla u <see cref="WorkflowState.RemoteService"/>.</summary>
		Task<bool> StartAsync(KioskLease lease, RemoteServiceSession session);

		/// <summary>Triger <see cref="WorkflowTrigger.RemoteServiceEnded"/> za sesiju te rezervacije; ne čeka.</summary>
		void End(KioskLease lease);

		/// <summary>Moduli koje DoorWatcher vidi kao nedostupne.</summary>
		IReadOnlyList<int> GetUnavailableModules();

		/// <summary>Čeka da se završi provera DoorWatcher-a koja je bila u toku.</summary>
		Task PauseDoorWatcherAsync();
	}

	/// <summary>Podešavanja kioska za servisne sesije.</summary>
	public sealed record RemoteServiceSettings
	{
		public required int LockerBoxCount { get; init; }

		/// <summary>2,5 s na serijskom kontroleru (kao <c>TopPos.Technician</c>), nula za NCU.</summary>
		public TimeSpan CommandGap { get; init; }

		public TimeSpan? ReceiveTimeout { get; init; }

		public TimeSpan? DeadlineCheckInterval { get; init; } = TimeSpan.FromSeconds(1);

		/// <summary>Razmak magistrale po tipu kontrolera.</summary>
		public static TimeSpan CommandGapFor(string controllerType)
			=> controllerType is LockControllerTypes.Serial or LockControllerTypes.SerialDailyExpress ? TimeSpan.FromSeconds(2.5) : TimeSpan.Zero;
	}

	/// <summary>
	/// Servisne sesije kiosk-a (najviše jedna u isto vreme): početak, otvaranje i kraj iz portala, ponovljeni zahtevi iz
	/// dnevnika i rezultati za <see cref="AgentCommands.GetCommandResult"/>. Handleri komandi su tanki adapteri oko ove klase;
	/// stranica sesije radi direktno sa <see cref="RemoteServiceSession"/>.
	///
	/// Metode komandi se pozivaju pod lock-om dispatcher-a: odluka (provere, rezervacija, upis u memoriju dnevnika) je
	/// sinhrona, a posao koji čeka disk, workflow ili kontroler ide u odložen odgovor (<see cref="AgentCommandResult.Pending"/>).
	/// </summary>
	public sealed class RemoteServiceManager
	{
		private readonly AgentOperationJournal _journal;
		private readonly KioskStatusSource _status;
		private readonly ILockControllerClient _controller;
		private readonly IRemoteServiceHost _host;
		private readonly RemoteServiceSettings _settings;
		private readonly ILogger? _logger;
		private readonly TimeProvider _timeProvider;
		private readonly Func<TimeSpan, Task>? _delay;

		private readonly Lock _lock = new();
		private RemoteServiceSession? _current;
		private Task<AgentCommandResult>? _starting;

		/// <param name="delay">Čekanje razmaka magistrale (testovi); podrazumevano stvarno čekanje.</param>
		public RemoteServiceManager(AgentOperationJournal journal, KioskStatusSource status, ILockControllerClient controller, IRemoteServiceHost host,
									RemoteServiceSettings settings, ILogger? logger, TimeProvider? timeProvider = null, Func<TimeSpan, Task>? delay = null)
		{
			_journal = journal;
			_status = status;
			_controller = controller;
			_host = host;
			_settings = settings;
			_logger = logger;
			_timeProvider = timeProvider ?? TimeProvider.System;
			_delay = delay;
		}

		/// <summary>Oporavak zapisa posle pada procesa, za <see cref="AgentOperationJournal.Initialize"/>.</summary>
		public static IReadOnlyDictionary<string, Func<AgentOperationRecord, AgentOperationRecord>> Recoverers { get; }
			= new Dictionary<string, Func<AgentOperationRecord, AgentOperationRecord>> {
				[AgentCommands.BeginRemoteService] = RemoteServiceSession.RecoverSessionRecord,
			};

		/// <summary>Sesija koja traje (i dok se završava), ili null.</summary>
		public RemoteServiceSession? Current {
			get { lock (_lock) { return _current; } }
		}

		/// <summary>Sesija sa zadatim ID-om dok nije završena; null za nepoznatu ili završenu.</summary>
		public RemoteServiceSession? Find(string sessionId)
		{
			lock (_lock) {
				return _current is { } session && session.SessionId == sessionId && session.Phase != RemoteServicePhases.Ended ? session : null;
			}
		}

		// ─── BeginRemoteService ────────────────────────────────────────────────

		/// <summary>
		/// Proverava raspored: null kad je ispravan, inače poruka za <see cref="AgentResultCodes.InvalidDoor"/>. Vrata iznad
		/// <paramref name="lockerBoxCount"/> se izbacuju (<paramref name="ignoredDoors"/>).
		/// </summary>
		public static string? ValidateLayout(IReadOnlyList<LayoutBox> layout, int lockerBoxCount, out List<LayoutBox> kept, out List<int> ignoredDoors)
		{
			kept = [];
			ignoredDoors = [];
			if (layout.Count == 0) {
				return "Raspored je prazan.";
			}
			// JSON null kao stavka niza: deserijalizacija ga propušta.
			if (layout.Any(b => b is null)) {
				return "Raspored ima praznu stavku (null).";
			}
			var bad = layout.Where(b => b.DoorNumber <= 0 || b.Column <= 0 || b.Row <= 0).Select(b => b.DoorNumber).ToList();
			if (bad.Count > 0) {
				return $"Raspored ima vrata, vertikalu ili red manji od 1 (vrata {string.Join(", ", bad)}).";
			}
			var sizes = layout.Where(b => !BoxSizes.All.Contains(b.Size, StringComparer.Ordinal)).Select(b => b.DoorNumber).ToList();
			if (sizes.Count > 0) {
				return $"Veličina mora biti {string.Join("/", BoxSizes.All)} (vrata {string.Join(", ", sizes)}).";
			}
			var repeated = layout.GroupBy(b => b.DoorNumber).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
			if (repeated.Count > 0) {
				return $"Vrata se ponavljaju u rasporedu: {string.Join(", ", repeated)}.";
			}
			kept = layout.Where(b => b.DoorNumber <= lockerBoxCount).ToList();
			ignoredDoors = layout.Where(b => b.DoorNumber > lockerBoxCount).Select(b => b.DoorNumber).Order().ToList();
			return null;
		}

		/// <summary>Otisak sadržaja za log i dnevnik: raspored i kod samo kao heš (dnevnik ne čuva kod).</summary>
		public static string DescribeBegin(BeginRemoteServicePayload payload)
			=> $"idle={payload.IdleTimeoutSeconds} s, raspored={payload.Layout?.Count ?? 0} poz. ({Hash(JsonSerializer.Serialize(payload.Layout, AgentJson.Options))}), " +
			   $"kod={Hash(payload.PresenceCode)}";

		public AgentCommandResult Begin(string sessionId, BeginRemoteServicePayload payload, string fingerprint)
		{
			if (KioskReadiness.Rejection(_status.Current) is { } rejection) {
				return rejection;
			}
			if (ValidateLayout(payload.Layout!, _settings.LockerBoxCount, out var layout, out var ignoredDoors) is { } layoutError) {
				return new(AgentResultCodes.InvalidDoor, layoutError);
			}
			var lease = _host.TryReserve($"Agent {AgentCommands.BeginRemoteService} ({sessionId})");
			if (lease == null) {
				var status = _status.Current;
				return KioskReadiness.Rejection(status)
					?? new(AgentResultCodes.Busy, $"Kiosk je zauzet: {status.WorkflowState}, vlasnik {KioskReadiness.DescribeRole(status.Role)}.");
			}
			var options = new RemoteServiceSessionOptions {
				SessionId = sessionId,
				PresenceCode = payload.PresenceCode,
				IdleTimeout = TimeSpan.FromSeconds(payload.IdleTimeoutSeconds),
				LockerBoxCount = _settings.LockerBoxCount,
				Layout = layout,
				IgnoredDoors = ignoredDoors,
				UnavailableModulesAtStart = _host.GetUnavailableModules(),
				CommandGap = _settings.CommandGap,
				ReceiveTimeout = _settings.ReceiveTimeout,
				Fingerprint = fingerprint,
				DeadlineCheckInterval = _settings.DeadlineCheckInterval,
			};
			var session = new RemoteServiceSession(options, _controller, _journal, _status, _logger, _timeProvider,
													_host.PauseDoorWatcherAsync, _delay, onEnded: ended => OnEnded(ended, lease));
			lock (_lock) {
				_current = session;
			}
			// Zapis sesije je u memoriji dnevnika pre povratka (ponovljen zahtev ga nalazi); upis na disk ide van ove niti.
			var starting = StartAsync(session, lease);
			lock (_lock) {
				if (_current == session) {
					_starting = starting;
				}
			}
			return AgentCommandResult.Deferred(starting);
		}

		/// <summary>Ponovljen <see cref="AgentCommands.BeginRemoteService"/>: tekuće stanje sesije iz memorije ili dnevnika.</summary>
		public AgentCommandResult? RepeatBegin(string sessionId, string fingerprint)
		{
			if (!_journal.TryGet(sessionId, out var record)) {
				return null;
			}
			if (record.Command != AgentCommands.BeginRemoteService || record.Fingerprint != fingerprint) {
				return new(AgentResultCodes.DuplicateConflict, "Isti RequestId je već prihvaćen sa drugačijim sadržajem.");
			}
			lock (_lock) {
				if (_current?.SessionId == sessionId && _starting is { IsCompleted: false } starting) {
					return AgentCommandResult.Deferred(starting);
				}
			}
			if (Find(sessionId) is { } live) {
				return Completed(live.GetResult(), "Servisna sesija je već počela.");
			}
			return record.ResultCode is AgentResultCodes.InProgress or AgentResultCodes.Completed
				? Completed(ReadSessionResult(record, afterSequence: null), "Servisna sesija je već počela.")
				: new(record.ResultCode, record.Message);
		}

		private async Task<AgentCommandResult> StartAsync(RemoteServiceSession session, KioskLease lease)
		{
			if (!await session.StartAsync().ConfigureAwait(false)) {
				_status.ClearOperation(session.SessionId);
				if (_journal.TryGet(session.SessionId, out var record)) {
					await _journal.SaveAsync(record with { ResultCode = AgentResultCodes.InternalError, Message = "Dnevnik operacija nije dostupan; sesija nije počela." })
								  .ConfigureAwait(false);
				}
				session.Dispose();
				_host.Release(lease);
				ClearCurrent(session);
				return new(AgentResultCodes.InternalError, "Dnevnik operacija nije dostupan; servisna sesija nije počela.");
			}
			if (!await _host.StartAsync(lease, session).ConfigureAwait(false)) {
				_logger?.LogWarning($"{nameof(RemoteServiceManager)}: sesija {session.SessionId} nije ušla u workflow; završava se kao {RemoteServiceEndModes.PortalCancelled}.");
				await session.EndFromPortalAsync().ConfigureAwait(false);
				return new(AgentResultCodes.Busy, "Kiosk je u međuvremenu zauzet; servisna sesija nije počela.");
			}
			var result = session.GetResult();
			var notes = new List<string>();
			if (result.UnavailableModulesAtStart.Count > 0) {
				notes.Add($"nedostupni moduli: {string.Join(", ", result.UnavailableModulesAtStart)}");
			}
			if (result.IgnoredDoors.Count > 0) {
				notes.Add($"vrata kojih na kiosku nema (izbačena iz rasporeda): {string.Join(", ", result.IgnoredDoors)}");
			}
			return Completed(result, notes.Count == 0 ? "Servisna sesija je počela; čeka se prijava servisera." :
														$"Servisna sesija je počela; čeka se prijava servisera ({string.Join("; ", notes)}).");
		}

		private void OnEnded(RemoteServiceSession session, KioskLease lease)
		{
			session.Dispose();
			_host.End(lease);
			ClearCurrent(session);
		}

		private void ClearCurrent(RemoteServiceSession session)
		{
			lock (_lock) {
				if (_current == session) {
					_current = null;
					_starting = null;
				}
			}
		}

		// ─── OpenDoors ─────────────────────────────────────────────────────────

		public AgentCommandResult OpenDoors(string requestId, OpenDoorsPayload payload, string fingerprint)
		{
			if (_status.Current.Lifecycle == KioskLifecycles.ShuttingDown) {
				return new(AgentResultCodes.ShuttingDown, "Kiosk se gasi.");
			}
			if (Find(payload.SessionId) is not { } session) {
				return new(AgentResultCodes.NoServiceSession, "Servisna sesija ne postoji ili je završena.");
			}
			var start = session.StartOpenDoorsAsync(payload.DoorNumbers, payload.All, DoorOpenSources.Portal, requestId, fingerprint);
			return AgentCommandResult.Deferred(MapOpenDoorsStart(session, requestId, start));
		}

		private static async Task<AgentCommandResult> MapOpenDoorsStart(RemoteServiceSession session, string requestId, Task<OpenDoorsStart> start)
		{
			var started = await start.ConfigureAwait(false);
			if (started.ResultCode != AgentResultCodes.InProgress) {
				return new(started.ResultCode, started.Message);
			}
			return new(AgentResultCodes.InProgress, "Otvaranje je počelo.") { Result = ToResult(session.GetOpenDoorsResult(requestId)) };
		}

		/// <summary>Ponovljen <see cref="AgentCommands.OpenDoors"/>: ishod iz dnevnika, bez novog otvaranja.</summary>
		public AgentCommandResult? RepeatOpenDoors(string requestId, string fingerprint)
		{
			if (!_journal.TryGet(requestId, out var record)) {
				return null;
			}
			if (record.Command != AgentCommands.OpenDoors || record.Fingerprint != fingerprint) {
				return new(AgentResultCodes.DuplicateConflict, "Isti RequestId je već prihvaćen sa drugačijim sadržajem.");
			}
			return OpenDoorsResultOf(record, "Otvaranje je već primljeno.");
		}

		// ─── EndRemoteService ──────────────────────────────────────────────────

		/// <summary>
		/// Kraj iz portala. Za sesiju koja je već završena (bilo kojim načinom) vraća njen konačni rezultat, pa je ponovljen
		/// zahtev bezbedan; nepoznat ID → <see cref="AgentResultCodes.NoServiceSession"/>.
		/// </summary>
		public AgentCommandResult End(string sessionId)
		{
			// Sesija koja još počinje (upis zapisa, ulazak u workflow) završava se tek kad početak završi: inače bi rezervacija
			// ostala neaktivna, triger kraja bi se odbacio, a zakasneli početak bi uveo mašinu u RemoteService bez sesije.
			Task<AgentCommandResult>? starting;
			lock (_lock) {
				starting = _current?.SessionId == sessionId && _starting is { IsCompleted: false } pending ? pending : null;
			}
			if (starting != null) {
				return AgentCommandResult.Deferred(EndAfterStartAsync(sessionId, starting));
			}
			if (Find(sessionId) is { } session) {
				return AgentCommandResult.Deferred(MapEnd(session.EndFromPortalAsync()));
			}
			if (_journal.TryGet(sessionId, out var record) && record.Command == AgentCommands.BeginRemoteService && record.IsFinished
				&& record.ResultCode == AgentResultCodes.Completed) {
				return Completed(ReadSessionResult(record, afterSequence: null), "Servisna sesija je već završena.");
			}
			return new(AgentResultCodes.NoServiceSession, "Servisna sesija ne postoji ili je završena.");
		}

		private async Task<AgentCommandResult> EndAfterStartAsync(string sessionId, Task<AgentCommandResult> starting)
		{
			try {
				await starting.ConfigureAwait(false);
			}
			catch (Exception ex) {
				_logger?.LogError(ex, $"{nameof(RemoteServiceManager)}: početak sesije {sessionId} nije završen; kraj se odlučuje po zapisu.");
			}
			var end = End(sessionId);
			return end.Pending is { } pending ? await pending.ConfigureAwait(false) : end;
		}

		private static async Task<AgentCommandResult> MapEnd(Task<(string ResultCode, RemoteServiceResult? Result)> end)
		{
			var (code, result) = await end.ConfigureAwait(false);
			return code == AgentResultCodes.Busy
				? new(AgentResultCodes.Busy, "Otvaranje je u toku; sesija se završava posle njega.")
				: Completed(result!, $"Servisna sesija je završena ({result!.EndMode}).");
		}

		// ─── GetCommandResult ──────────────────────────────────────────────────

		/// <summary>Ishod ranije komande iz dnevnika; za sesiju i otvaranje koji traju — iz memorije, uz dosadašnji deo.</summary>
		public AgentCommandResult GetCommandResult(string requestId, int? afterSequence)
		{
			if (!_journal.TryGet(requestId, out var record)) {
				return new(AgentResultCodes.ResultNotFound, "Kiosk nema zapis o ovom zahtevu (zapisi se čuvaju 7 dana).");
			}
			switch (record.Command) {
				case AgentCommands.BeginRemoteService:
					if (Find(requestId) is { } live) {
						return new(AgentResultCodes.InProgress, null) { Result = ToResult(live.GetResult(afterSequence)) };
					}
					return new(record.ResultCode, record.Message) { Result = ToResult(ReadSessionResult(record, afterSequence)) };
				case AgentCommands.OpenDoors:
					return OpenDoorsResultOf(record, record.Message);
				default:
					return new(record.ResultCode, record.Message) { Result = record.Result };
			}
		}

		private AgentCommandResult OpenDoorsResultOf(AgentOperationRecord record, string? message)
		{
			var stored = record.Result is { ValueKind: JsonValueKind.Object } json ? json.Deserialize<OpenDoorsResult>(AgentJson.Options) : null;
			if (stored != null && Find(stored.SessionId) is { } live && live.GetOpenDoorsResult(record.RequestId) is { } current) {
				return new(current.Finished ? AgentResultCodes.Completed : AgentResultCodes.InProgress, message) { Result = ToResult(current) };
			}
			// Prekinuto restartom: zapis otvaranja nema upise, a sesija ih ima (namera se upisuje u zapis sesije).
			if (stored != null && !stored.Finished && _journal.TryGet(stored.SessionId, out var sessionRecord)) {
				var fromSession = ReadSessionResult(sessionRecord, afterSequence: null, page: false);
				stored.Openings = fromSession?.Openings.Where(o => o.RequestId == record.RequestId).ToList() ?? stored.Openings;
			}
			return new(record.ResultCode, message ?? record.Message) { Result = ToResult(stored) };
		}

		// ─── pomoćno ───────────────────────────────────────────────────────────

		private static RemoteServiceResult? ReadSessionResult(AgentOperationRecord record, int? afterSequence, bool page = true)
		{
			var full = record.Result is { ValueKind: JsonValueKind.Object } json ? json.Deserialize<RemoteServiceResult>(AgentJson.Options) : null;
			return full == null || !page ? full : RemoteServiceSession.Page(full, afterSequence);
		}

		private static AgentCommandResult Completed(RemoteServiceResult? result, string message)
			=> new(AgentResultCodes.Completed, message) { Result = ToResult(result) };

		private static JsonElement? ToResult<T>(T? result) where T : class => result == null ? null : AgentJson.ToResult(result);

		private static string Hash(string? text)
			=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? "")), 0, 8).ToLowerInvariant();
	}

	/// <summary>
	/// <see cref="IRemoteServiceHost"/> nad workflow-om aplikacije: rezervacija kroz koordinatora, ulazak i izlazak iz
	/// <see cref="WorkflowState.RemoteService"/> kroz red trigera.
	/// </summary>
	public sealed class WorkflowRemoteServiceHost : IRemoteServiceHost
	{
		// Triger čeka u redu iza trigera koji je već bio u toku (npr. Reset posle promene operativnosti); duže od ovoga je kvar.
		private static readonly TimeSpan __startTimeout = TimeSpan.FromSeconds(10);

		private readonly WorkflowMachine _machine;
		private readonly KioskOperationCoordinator _coordinator;
		private readonly Func<IReadOnlyList<int>> _unavailableModules;
		private readonly Func<Task> _pauseDoorWatcher;
		private readonly ILogger? _logger;
		private readonly Lock _lock = new();
		private (RemoteServiceSession Session, TaskCompletionSource<bool> Entered)? _pending;

		/// <param name="showSession">OnEntry stanja <see cref="WorkflowState.RemoteService"/>: prikaz stranice sesije.</param>
		public WorkflowRemoteServiceHost(WorkflowMachine machine, KioskOperationCoordinator coordinator, Func<IReadOnlyList<int>> unavailableModules,
										 Func<Task> pauseDoorWatcher, ILogger? logger, Func<RemoteServiceSession, ValueTask>? showSession = null)
		{
			_machine = machine;
			_coordinator = coordinator;
			_unavailableModules = unavailableModules;
			_pauseDoorWatcher = pauseDoorWatcher;
			_logger = logger;
			machine.OnEntryFrom(WorkflowState.RemoteService, machine.RemoteServiceStarted, async sessionId => {
				(RemoteServiceSession Session, TaskCompletionSource<bool> Entered)? entering;
				lock (_lock) {
					entering = _pending is { } pending && pending.Session.SessionId == sessionId ? pending : null;
				}
				if (entering is not { } current) {
					_logger?.LogError($"{nameof(WorkflowRemoteServiceHost)}: ulazak u {WorkflowState.RemoteService} bez sesije {sessionId}.");
					return;
				}
				try {
					if (showSession != null) {
						await showSession(current.Session).ConfigureAwait(false);
					}
				}
				finally {
					current.Entered.TrySetResult(true);
				}
			});
		}

		public KioskLease? TryReserve(string source)
			=> _coordinator.TryBegin(_machine, KioskLeaseMode.WorkflowSession, WorkflowStatusRules.AgentRole, source);

		public void Release(KioskLease lease) => _coordinator.ReleaseIfReserved(lease);

		public async Task<bool> StartAsync(KioskLease lease, RemoteServiceSession session)
		{
			var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			lock (_lock) {
				_pending = (session, entered);
			}
			try {
				await _machine.FireGuardedAsync(() => _coordinator.IsReserved(lease), _machine.RemoteServiceStarted, session.SessionId,
												onRejected: () => {
													_coordinator.ReleaseIfReserved(lease);
													entered.TrySetResult(false);
												}).ConfigureAwait(false);
				return await entered.Task.WaitAsync(__startTimeout).ConfigureAwait(false);
			}
			catch (TimeoutException) {
				bool inSession = _machine.State == WorkflowState.RemoteService && _coordinator.IsActive(lease);
				_logger?.LogError($"{nameof(WorkflowRemoteServiceHost)}: sesija {session.SessionId} nije potvrdila ulazak u " +
								  $"{WorkflowState.RemoteService} za {__startTimeout.TotalSeconds:0} s (stanje {_machine.State}).");
				if (!inSession) {
					_coordinator.ReleaseIfReserved(lease);
				}
				return inSession;
			}
			finally {
				lock (_lock) {
					if (_pending?.Entered == entered) {
						_pending = null;
					}
				}
			}
		}

		public void End(KioskLease lease)
			=> _ = _machine.FireGuardedAsync(() => _coordinator.IsActive(lease), WorkflowTrigger.RemoteServiceEnded);

		public IReadOnlyList<int> GetUnavailableModules() => _unavailableModules();

		public Task PauseDoorWatcherAsync() => _pauseDoorWatcher();
	}
}
