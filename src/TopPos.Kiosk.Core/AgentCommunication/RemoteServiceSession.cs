using System.Text.Json;
using Microsoft.Extensions.Logging;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.Workflow;
using TopPos.LockController;

namespace TopPos.Kiosk.AgentCommunication
{
	/// <summary>Podešavanja jedne servisne sesije; proverene ulaze daje handler <see cref="AgentCommands.BeginRemoteService"/>.</summary>
	public sealed record RemoteServiceSessionOptions
	{
		/// <summary><see cref="AgentRequest.RequestId"/> komande <see cref="AgentCommands.BeginRemoteService"/>.</summary>
		public required string SessionId { get; init; }

		public required string PresenceCode { get; init; }

		/// <summary>Rok neaktivnosti posle prijave servisera.</summary>
		public required TimeSpan IdleTimeout { get; init; }

		public required int LockerBoxCount { get; init; }

		/// <summary>Raspored za crtež, bez vrata kojih na kiosku nema.</summary>
		public IReadOnlyList<LayoutBox> Layout { get; init; } = [];

		public IReadOnlyList<int> IgnoredDoors { get; init; } = [];

		public IReadOnlyList<int> UnavailableModulesAtStart { get; init; } = [];

		/// <summary>Obavezan razmak između dve komande kontroleru: 2,5 s na serijskoj magistrali, nula za NCU (razmak pravi klijent).</summary>
		public TimeSpan CommandGap { get; init; }

		public TimeSpan? ReceiveTimeout { get; init; }

		/// <summary>Otisak zahteva <see cref="AgentCommands.BeginRemoteService"/>, za ponovljen <see cref="AgentRequest.RequestId"/>.</summary>
		public string Fingerprint { get; init; } = "";

		/// <summary>Koliko često sesija sama proverava rokove (prijava, neaktivnost); <c>null</c> = samo kroz <see cref="RemoteServiceSession.CheckDeadlinesAsync"/>.</summary>
		public TimeSpan? DeadlineCheckInterval { get; init; }
	}

	/// <summary>Ishod unosa koda prisustva.</summary>
	public enum PresenceCodeResult
	{
		Accepted,
		/// <summary>Pogrešan kod; ostalo je još pokušaja (<see cref="RemoteServiceSession.RemainingAuthAttempts"/>).</summary>
		Rejected,
		/// <summary>Pogrešan kod iz poslednjeg pokušaja: sesija je završena kao <see cref="RemoteServiceEndModes.AuthFailed"/>.</summary>
		Failed,
		/// <summary>Sesija ne čeka prijavu (serviser je već prijavljen, ili je sesija završena).</summary>
		NotAwaiting,
	}

	/// <summary>
	/// Odgovor na zahtev za otvaranje: <see cref="AgentResultCodes.InProgress"/> (otvaranje teče, <see cref="Completion"/> se
	/// završava kad sva vrata imaju ishod) ili odbijanje (<see cref="AgentResultCodes.Busy"/>,
	/// <see cref="AgentResultCodes.TechnicianNotPresent"/>, <see cref="AgentResultCodes.NoServiceSession"/>,
	/// <see cref="AgentResultCodes.InvalidDoor"/>).
	/// </summary>
	public sealed record OpenDoorsStart(string ResultCode, string? Message, Task? Completion = null);

	/// <summary>
	/// Servisna sesija Agenta (RemoteService): serviser pred paketomatom otvara vrata iz portala ili sa ekrana kioska
	/// (doc/notes/Info/Ugovor pipe komandi/RemoteService.md, „Odgovor kioska").
	///
	/// Faze: <see cref="RemoteServicePhases.AwaitingTechnician"/> (prijava kodom: <see cref="RemoteServiceLimits.MaxAuthAttempts"/>
	/// pokušaja u <see cref="RemoteServiceLimits.AuthTimeoutSeconds"/> s) → <see cref="RemoteServicePhases.Active"/> →
	/// <see cref="RemoteServicePhases.Ended"/>. U sesiji teče najviše jedno otvaranje; vrata idu jedna po jedna, sa najviše
	/// jednim slanjem komande (<see cref="ILockControllerClient.OpenDoorOnce"/>), bez poslovnih potvrda i bez prijave greške
	/// vrata serveru.
	///
	/// Dnevnik: sesija je zapis <see cref="AgentCommands.BeginRemoteService"/> sa <see cref="RemoteServiceResult"/>, a svako
	/// otvaranje iz portala i svoj zapis <see cref="AgentCommands.OpenDoors"/>. Namera se upisuje pre poziva kontrolera
	/// (otvaranje sa ishodom <see cref="DoorOpenOutcomes.OutcomeUnknown"/>, još van <see cref="RemoteServiceResult.LastSequence"/>):
	/// bez upisane namere vrata se ne otvaraju (<see cref="DoorOpenOutcomes.NotStarted"/>), a pad posle slanja ostavlja tačan
	/// ishod (<see cref="RecoverSessionRecord"/>). Svaki upis koji Agent vidi dobija redni broj, a vidljiv upis se više ne menja.
	///
	/// Nema WPF zavisnosti; vreme, kontroler i čekanje razmaka se ubrizgavaju. Bezbedno za pozive iz više niti.
	/// </summary>
	public sealed class RemoteServiceSession : IDisposable
	{
		/// <summary><see cref="KioskStatus.Activity"/> dok sesija traje.</summary>
		public const string Activity = "RemoteService";

		private readonly RemoteServiceSessionOptions _options;
		private readonly ILockControllerClient _controller;
		private readonly AgentOperationJournal _journal;
		private readonly KioskStatusSource? _status;
		private readonly ILogger? _logger;
		private readonly TimeProvider _timeProvider;
		private readonly Func<Task>? _pauseDoorWatcher;
		private readonly Func<TimeSpan, Task> _delay;
		private readonly Action<RemoteServiceSession>? _onEnded;

		private readonly Lock _lock = new();
		// Zapis sesije se pravi i predaje dnevniku pod istim gate-om, pa dnevnik uvek dobije najnovije stanje poslednje.
		private readonly SemaphoreSlim _saveGate = new(1, 1);

		private readonly DateTimeOffset _startedAt;
		private string _phase = RemoteServicePhases.AwaitingTechnician;
		private string? _endMode;
		private DateTimeOffset? _authenticatedAt;
		private DateTimeOffset? _endedAt;
		private int _failedAuthAttempts;
		private List<int>? _doorsNotClosed;
		private readonly List<DoorOpening> _openings = [];
		private readonly List<ModuleError> _moduleErrors = [];
		private readonly Dictionary<int, string> _lastOutcomeByDoor = [];
		// Vrata koja su po poslednjem fizičkom ishodu možda otvorena; ishod bez slanja ne menja stanje vrata.
		private readonly HashSet<int> _possiblyOpen = [];
		private readonly SortedSet<int> _failedModules = [];
		private readonly Dictionary<string, OpenDoorsRun> _runs = new(StringComparer.Ordinal);
		private int _sequence;              // poslednji dodeljen redni broj
		private int _lastVisibleSequence;   // poslednji završen upis (RemoteServiceResult.LastSequence)
		private OpenDoorsRun? _currentRun;
		private int? _doorInProgress;
		private TaskCompletionSource<RemoteServiceResult>? _ended;
		private DateTimeOffset _idleDeadline;
		private long _lastCommandFinished;  // TimeProvider timestamp; 0 = sesija još nije poslala komandu
		private ITimer? _deadlineTimer;

		private sealed class OpenDoorsRun(string? requestId, string source, IReadOnlyList<int> doors)
		{
			public string? RequestId { get; } = requestId;
			public string Source { get; } = source;
			public IReadOnlyList<int> Doors { get; } = doors;
			public List<DoorOpening> Openings { get; } = [];
			public bool Finished { get; set; }
			public DateTimeOffset CreatedUtc { get; set; }
			public string Fingerprint { get; set; } = "";
		}

		/// <param name="pauseDoorWatcher">Pre svake komande: čeka da se završi provera DoorWatcher-a koja je bila u toku.</param>
		/// <param name="delay">Čekanje razmaka magistrale; podrazumevano <see cref="Task.Delay(TimeSpan, TimeProvider)"/>.</param>
		/// <param name="onEnded">Posle kraja sesije (upisan kraj, status bez operacije): vraćanje workflow-a na Start.</param>
		public RemoteServiceSession(RemoteServiceSessionOptions options,
									ILockControllerClient controller,
									AgentOperationJournal journal,
									KioskStatusSource? status,
									ILogger? logger,
									TimeProvider? timeProvider = null,
									Func<Task>? pauseDoorWatcher = null,
									Func<TimeSpan, Task>? delay = null,
									Action<RemoteServiceSession>? onEnded = null)
		{
			_options = options;
			_controller = controller;
			_journal = journal;
			_status = status;
			_logger = logger;
			_timeProvider = timeProvider ?? TimeProvider.System;
			_pauseDoorWatcher = pauseDoorWatcher;
			_delay = delay ?? (d => Task.Delay(d, _timeProvider));
			_onEnded = onEnded;
			_startedAt = _timeProvider.GetUtcNow();
		}

		public string SessionId => _options.SessionId;

		public IReadOnlyList<LayoutBox> Layout => _options.Layout;

		public int LockerBoxCount => _options.LockerBoxCount;

		/// <summary>Promena stanja sesije (faza, otvaranje, ishod, kraj); poziva se van lock-a, sa niti koja je promenu napravila.</summary>
		public event Action? Changed;

		public string Phase {
			get { lock (_lock) { return _phase; } }
		}

		/// <summary>Kraj je zatražen ili završen: nova otvaranja se odbijaju.</summary>
		public bool IsEnding {
			get { lock (_lock) { return _ended != null; } }
		}

		public bool IsOpening {
			get { lock (_lock) { return _currentRun != null; } }
		}

		/// <summary>Vrata kojima je komanda upravo poslata (čeka se ishod), ili null.</summary>
		public int? DoorInProgress {
			get { lock (_lock) { return _doorInProgress; } }
		}

		public int RemainingAuthAttempts {
			get { lock (_lock) { return RemoteServiceLimits.MaxAuthAttempts - _failedAuthAttempts; } }
		}

		public DateTimeOffset AuthDeadline => _startedAt.AddSeconds(RemoteServiceLimits.AuthTimeoutSeconds);

		public DateTimeOffset IdleDeadline {
			get { lock (_lock) { return _idleDeadline; } }
		}

		/// <summary>Moduli koji tokom sesije nisu odgovorili, uz one nedostupne pri početku.</summary>
		public IReadOnlyList<int> FailedModules {
			get {
				lock (_lock) {
					return _failedModules.Union(_options.UnavailableModulesAtStart).Order().ToList();
				}
			}
		}

		/// <summary>Poslednji ishod otvaranja po vratima u ovoj sesiji.</summary>
		public IReadOnlyDictionary<int, string> LastOutcomeByDoor {
			get { lock (_lock) { return new Dictionary<int, string>(_lastOutcomeByDoor); } }
		}

		/// <summary>
		/// Vrata koja su po poslednjem poznatom stanju možda otvorena (<see cref="DoorOpenOutcomes.OpenConfirmed"/> ili
		/// <see cref="DoorOpenOutcomes.OutcomeUnknown"/>); prvi dodir na „Zatvorio sam sva vrata" ih pokazuje.
		/// </summary>
		public IReadOnlyList<int> DoorsPossiblyOpen()
		{
			lock (_lock) {
				return _possiblyOpen.Order().ToList();
			}
		}

		/// <summary>
		/// Početak sesije: status dobija operaciju, a dnevnik zapis sesije. Vraća da li je zapis upisan na disk; bez upisa
		/// handler ne pokreće sesiju.
		/// </summary>
		public async Task<bool> StartAsync()
		{
			_status?.SetOperation(SessionId, Activity);
			bool saved = await SaveSessionAsync().ConfigureAwait(false);
			_logger?.LogInformation($"{nameof(RemoteServiceSession)} {SessionId}: početak, čeka se prijava servisera " +
									$"(kod {_options.PresenceCode}, rok {RemoteServiceLimits.AuthTimeoutSeconds} s); " +
									$"rok neaktivnosti {_options.IdleTimeout.TotalSeconds:0} s, vrata {LockerBoxCount}, " +
									$"izbačena iz rasporeda [{string.Join(", ", _options.IgnoredDoors)}], " +
									$"nedostupni moduli [{string.Join(", ", _options.UnavailableModulesAtStart)}]" +
									(saved ? "." : "; zapis sesije NIJE upisan u dnevnik."));
			if (saved && _options.DeadlineCheckInterval is { } interval) {
				lock (_lock) {
					_deadlineTimer = _timeProvider.CreateTimer(_ => _ = CheckDeadlinesSafeAsync(), null, interval, interval);
				}
			}
			return saved;
		}

		/// <summary>Kod sa numeričke tastature ili skenera (QR nosi isti broj). Unos se loguje u celini.</summary>
		public async Task<PresenceCodeResult> SubmitPresenceCodeAsync(string code)
		{
			await CheckDeadlinesAsync().ConfigureAwait(false);
			var entered = code.Trim();
			PresenceCodeResult result;
			int attempts;
			lock (_lock) {
				// Iscrpljeni pokušaji zatvaraju prijavu odmah, ne tek kad kraj (AuthFailed) počne posle upisa u dnevnik.
				if (_phase != RemoteServicePhases.AwaitingTechnician || _ended != null || _failedAuthAttempts >= RemoteServiceLimits.MaxAuthAttempts) {
					result = PresenceCodeResult.NotAwaiting;
					attempts = _failedAuthAttempts;
				}
				else {
					_sequence++;
					_lastVisibleSequence = _sequence;
					if (string.Equals(entered, _options.PresenceCode, StringComparison.Ordinal)) {
						_phase = RemoteServicePhases.Active;
						_authenticatedAt = _timeProvider.GetUtcNow();
						_idleDeadline = _authenticatedAt.Value + _options.IdleTimeout;
						result = PresenceCodeResult.Accepted;
					}
					else {
						_failedAuthAttempts++;
						result = _failedAuthAttempts >= RemoteServiceLimits.MaxAuthAttempts ? PresenceCodeResult.Failed : PresenceCodeResult.Rejected;
					}
					attempts = _failedAuthAttempts;
				}
			}
			switch (result) {
				case PresenceCodeResult.NotAwaiting:
					_logger?.LogWarning($"{nameof(RemoteServiceSession)} {SessionId}: kod {entered} unet kad sesija ne čeka prijavu; zanemaren.");
					return result;
				case PresenceCodeResult.Accepted:
					_logger?.LogInformation($"{nameof(RemoteServiceSession)} {SessionId}: serviser prijavljen kodom {entered}.");
					break;
				default:
					_logger?.LogWarning($"{nameof(RemoteServiceSession)} {SessionId}: pogrešan kod {entered}, pokušaj {attempts} od {RemoteServiceLimits.MaxAuthAttempts}.");
					break;
			}
			await SaveAndPublishAsync().ConfigureAwait(false);
			if (result == PresenceCodeResult.Failed) {
				await EndAsync(RemoteServiceEndModes.AuthFailed).ConfigureAwait(false);
			}
			return result;
		}

		/// <summary>Dodir na ekranu u fazi <see cref="RemoteServicePhases.Active"/> produžava rok neaktivnosti.</summary>
		public void Touch()
		{
			lock (_lock) {
				if (_phase == RemoteServicePhases.Active && _ended == null) {
					var deadline = _timeProvider.GetUtcNow() + _options.IdleTimeout;
					if (deadline > _idleDeadline) {
						_idleDeadline = deadline;
					}
				}
			}
		}

		/// <summary>
		/// Pokreće otvaranje: zadata vrata redom, ili sva (1..<see cref="LockerBoxCount"/>, uključujući servisna). Ponovljena
		/// vrata u spisku se otvaraju jednom. Za otvaranje iz portala <paramref name="requestId"/> je ID komande
		/// <see cref="AgentCommands.OpenDoors"/>, koja dobija svoj zapis u dnevniku.
		/// </summary>
		public async Task<OpenDoorsStart> StartOpenDoorsAsync(IReadOnlyList<int>? doorNumbers, bool all, string source,
															   string? requestId = null, string fingerprint = "")
		{
			IReadOnlyList<int> doors = all
				? Enumerable.Range(1, LockerBoxCount).ToList()
				: (doorNumbers ?? []).Distinct().ToList();
			OpenDoorsRun run;
			lock (_lock) {
				if (_phase == RemoteServicePhases.Ended || _ended != null) {
					return new(AgentResultCodes.NoServiceSession, "Servisna sesija je završena.");
				}
				if (_phase == RemoteServicePhases.AwaitingTechnician) {
					return new(AgentResultCodes.TechnicianNotPresent, "Serviser se još nije prijavio na ekranu kioska.");
				}
				if (_currentRun != null) {
					return new(AgentResultCodes.Busy, "Otvaranje je u toku.");
				}
				if (doors.Count == 0) {
					return new(AgentResultCodes.InvalidDoor, "Nije zadato nijedno vrata.");
				}
				var invalid = doors.Where(d => d < 1 || d > LockerBoxCount).ToList();
				if (invalid.Count > 0) {
					return new(AgentResultCodes.InvalidDoor, $"Vrata van 1..{LockerBoxCount}: {string.Join(", ", invalid)}.");
				}
				run = new OpenDoorsRun(requestId, source, doors) { CreatedUtc = _timeProvider.GetUtcNow(), Fingerprint = fingerprint };
				_currentRun = run;
				if (requestId != null) {
					_runs[requestId] = run;
				}
			}
			_logger?.LogInformation($"{nameof(RemoteServiceSession)} {SessionId}: otvaranje ({source}{(requestId != null ? $", {requestId}" : "")}) " +
									$"vrata [{string.Join(", ", doors)}].");
			Changed?.Invoke();
			if (requestId != null && !await SaveRunAsync(run).ConfigureAwait(false)) {
				_logger?.LogError($"{nameof(RemoteServiceSession)} {SessionId}: zapis otvaranja {requestId} nije upisan u dnevnik; " +
								  "namera za svaka vrata se i dalje upisuje pre slanja.");
			}
			var completion = Task.Run(() => RunOpenDoorsAsync(run));
			return new(AgentResultCodes.InProgress, null, completion);
		}

		/// <summary>Rezultat otvaranja iz portala dok sesija postoji u memoriji; <c>null</c> za nepoznat ID.</summary>
		public OpenDoorsResult? GetOpenDoorsResult(string requestId)
		{
			lock (_lock) {
				return _runs.TryGetValue(requestId, out var run) ? BuildRunResult(run) : null;
			}
		}

		/// <summary>Rezultat sesije za Agenta: upisi posle <paramref name="afterSequence"/>, najviše <see cref="RemoteServiceLimits.MaxEntriesPerResult"/>.</summary>
		public RemoteServiceResult GetResult(int? afterSequence = null)
		{
			lock (_lock) {
				return Page(BuildFullResult(), afterSequence);
			}
		}

		/// <summary>Serviser je na ekranu potvrdio da je zatvorio sva vrata.</summary>
		public Task<(string ResultCode, RemoteServiceResult? Result)> EndFromScreenAsync() => EndAsync(RemoteServiceEndModes.ScreenConfirmed);

		/// <summary><see cref="AgentCommands.EndRemoteService"/>: pre prijave servisera sesija se završava kao <see cref="RemoteServiceEndModes.PortalCancelled"/>.</summary>
		public Task<(string ResultCode, RemoteServiceResult? Result)> EndFromPortalAsync() => EndAsync(RemoteServiceEndModes.PortalConfirmed);

		/// <summary>Proverava rok prijave i rok neaktivnosti (ne teče dok otvaranje traje); po isteku završava sesiju.</summary>
		public async Task CheckDeadlinesAsync()
		{
			string? mode = null;
			lock (_lock) {
				if (_ended != null) {
					return;
				}
				var now = _timeProvider.GetUtcNow();
				if (_phase == RemoteServicePhases.AwaitingTechnician && now >= AuthDeadline) {
					mode = RemoteServiceEndModes.AuthTimeout;
				}
				else if (_phase == RemoteServicePhases.Active && _currentRun == null && now >= _idleDeadline) {
					mode = RemoteServiceEndModes.IdleTimeout;
				}
			}
			if (mode != null) {
				await EndAsync(mode).ConfigureAwait(false);
			}
		}

		public void Dispose()
		{
			lock (_lock) {
				_deadlineTimer?.Dispose();
				_deadlineTimer = null;
			}
		}

		private async Task CheckDeadlinesSafeAsync()
		{
			try {
				await CheckDeadlinesAsync().ConfigureAwait(false);
			}
			catch (Exception ex) {
				_logger?.LogError(ex, $"{nameof(RemoteServiceSession)} {SessionId}: provera rokova nije uspela");
			}
		}

		/// <summary>
		/// Kraj sesije. Dok otvaranje traje → <see cref="AgentResultCodes.Busy"/>. Prvi zahtev određuje način kraja; svi zahtevi
		/// dobijaju isti rezultat, posle upisa kraja.
		/// </summary>
		private async Task<(string ResultCode, RemoteServiceResult? Result)> EndAsync(string requestedMode)
		{
			TaskCompletionSource<RemoteServiceResult> ended;
			string mode = requestedMode;
			bool runEnd = false;
			bool readDoors = false;
			lock (_lock) {
				if (_ended != null) {
					ended = _ended;
				}
				else {
					if (_currentRun != null) {
						return (AgentResultCodes.Busy, null);
					}
					runEnd = true;
					mode = requestedMode == RemoteServiceEndModes.PortalConfirmed && _phase == RemoteServicePhases.AwaitingTechnician
						? RemoteServiceEndModes.PortalCancelled
						: requestedMode;
					// Pre prijave ništa nije otvarano, pa se stanje vrata ne čita.
					readDoors = _phase == RemoteServicePhases.Active;
					_endMode = mode;
					_ended = new TaskCompletionSource<RemoteServiceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
					ended = _ended;
					_deadlineTimer?.Dispose();
					_deadlineTimer = null;
				}
			}
			if (runEnd) {
				Changed?.Invoke();
				// Čitanje vrata i upis kraja nikad na niti pozivaoca (UI, lock dispatcher-a): NCU nema razmak, pa bi prvo
				// čitanje modula počelo sinhrono.
				await Task.Run(() => RunEndAsync(mode, readDoors, ended)).ConfigureAwait(false);
			}
			return (AgentResultCodes.Completed, await ended.Task.ConfigureAwait(false));
		}

		private async Task RunEndAsync(string mode, bool readDoors, TaskCompletionSource<RemoteServiceResult> ended)
		{
			_logger?.LogInformation($"{nameof(RemoteServiceSession)} {SessionId}: kraj ({mode})" + (readDoors ? ", čitanje stanja vrata." : "."));
			List<int> doorsNotClosed = [];
			try {
				if (readDoors) {
					doorsNotClosed = await ReadDoorsNotClosedAsync().ConfigureAwait(false);
				}
			}
			catch (Exception ex) {
				// ReadDoorsNotClosedAsync hvata greške po modulu; ovo je samo zaštita da sesija ipak završi.
				_logger?.LogError(ex, $"{nameof(RemoteServiceSession)} {SessionId}: čitanje stanja vrata na kraju nije uspelo");
			}
			RemoteServiceResult result;
			lock (_lock) {
				_phase = RemoteServicePhases.Ended;
				_endedAt = _timeProvider.GetUtcNow();
				_doorsNotClosed = doorsNotClosed;
				_sequence++;
				_lastVisibleSequence = _sequence;
				result = Page(BuildFullResult(), afterSequence: null);
			}
			if (!await SaveSessionAsync().ConfigureAwait(false)) {
				_logger?.LogError($"{nameof(RemoteServiceSession)} {SessionId}: kraj sesije nije upisan u dnevnik; posle restarta bi bio zaključen kao {RemoteServiceEndModes.ProcessRestart}.");
			}
			_status?.SetOperationSequence(SessionId, result.LastSequence);
			_status?.ClearOperation(SessionId);
			_logger?.LogInformation($"{nameof(RemoteServiceSession)} {SessionId}: završena ({mode}); otvaranja {CountOpenings()}, " +
									$"vrata bez „zatvoreno\" [{string.Join(", ", doorsNotClosed)}].");
			ended.TrySetResult(result);
			Changed?.Invoke();
			try {
				_onEnded?.Invoke(this);
			}
			catch (Exception ex) {
				_logger?.LogError(ex, $"{nameof(RemoteServiceSession)} {SessionId}: obrada kraja sesije nije uspela");
			}
		}

		private int CountOpenings()
		{
			lock (_lock) {
				return _openings.Count;
			}
		}

		// Jedno čitanje po modulu, bez ponavljanja; modul bez odgovora → sva njegova vrata ulaze u spisak i greška modula.
		private async Task<List<int>> ReadDoorsNotClosedAsync()
		{
			var notClosed = new List<int>();
			int perModule = _controller.NumberOfDoorsPerController;
			int modules = DoorWatcher.GetModuleCount(LockerBoxCount, perModule);
			for (int module = 1; module <= modules; module++) {
				var (first, last) = DoorWatcher.GetModuleDoorRange(module, LockerBoxCount, perModule);
				await WaitForControllerAsync().ConfigureAwait(false);
				DoorStatusCode[]? statuses;
				try {
					statuses = await _controller.CheckDoorMultiple(module, _options.ReceiveTimeout, CancellationToken.None).ConfigureAwait(false);
				}
				catch (Exception ex) {
					_logger?.LogError(ex, $"{nameof(RemoteServiceSession)} {SessionId}: čitanje modula {module} na kraju nije uspelo");
					statuses = null;
				}
				finally {
					MarkCommandFinished();
				}
				if (statuses == null) {
					_logger?.LogWarning($"{nameof(RemoteServiceSession)} {SessionId}: modul {module} nije odgovorio na čitanje na kraju.");
					notClosed.AddRange(Enumerable.Range(first, last - first + 1));
					lock (_lock) {
						AddModuleError(module);
					}
					continue;
				}
				for (int door = first; door <= last && door - first < statuses.Length; door++) {
					if (statuses[door - first].IsOpen) {
						notClosed.Add(door);
					}
				}
			}
			return notClosed;
		}

		private async Task RunOpenDoorsAsync(OpenDoorsRun run)
		{
			var failedModules = new HashSet<int>();
			try {
				foreach (var door in run.Doors) {
					int module = ModuleOf(door);
					if (failedModules.Contains(module)) {
						// Modul nije odgovorio na prethodna vrata ovog otvaranja: komanda se ne šalje.
						lock (_lock) {
							CompleteOpening(run, NewOpening(run, door), DoorOpenOutcomes.ModuleUnavailable);
						}
						await SaveAndPublishAsync().ConfigureAwait(false);
						continue;
					}

					await WaitForControllerAsync().ConfigureAwait(false);
					DoorOpening opening;
					lock (_lock) {
						opening = NewOpening(run, door);
						opening.Outcome = DoorOpenOutcomes.OutcomeUnknown;
						_openings.Add(opening);    // namera: van LastSequence dok ishod ne stigne
						_doorInProgress = door;
					}
					Changed?.Invoke();
					if (!await SaveSessionAsync().ConfigureAwait(false)) {
						_logger?.LogError($"{nameof(RemoteServiceSession)} {SessionId}: namera za vrata {door} nije upisana u dnevnik; vrata se ne otvaraju.");
						lock (_lock) {
							_openings.Remove(opening);
							_doorInProgress = null;
							CompleteOpening(run, opening, DoorOpenOutcomes.NotStarted);
						}
						await SaveAndPublishAsync().ConfigureAwait(false);
						continue;
					}

					DoorOpenAttempt attempt;
					try {
						attempt = await _controller.OpenDoorOnce(door, _options.ReceiveTimeout, CancellationToken.None).ConfigureAwait(false);
					}
					catch (Exception ex) {
						// Ne zna se da li je komanda otišla.
						_logger?.LogError(ex, $"{nameof(RemoteServiceSession)} {SessionId}: otvaranje vrata {door} nije uspelo");
						attempt = new DoorOpenAttempt(true, null);
					}
					finally {
						MarkCommandFinished();
					}

					var outcome = !attempt.Sent ? DoorOpenOutcomes.ModuleUnavailable
								: attempt.Status is not { } status ? DoorOpenOutcomes.OutcomeUnknown
								: status.IsOpen ? DoorOpenOutcomes.OpenConfirmed
								: DoorOpenOutcomes.OpenNotConfirmed;
					bool moduleFailed = attempt.Status == null;
					lock (_lock) {
						_openings.Remove(opening);
						_doorInProgress = null;
						CompleteOpening(run, opening, outcome);
						if (moduleFailed) {
							failedModules.Add(module);
							AddModuleError(module);
						}
					}
					_logger?.LogInformation($"{nameof(RemoteServiceSession)} {SessionId}: vrata {door} ({run.Source}) → {outcome}" +
											(moduleFailed ? $"; modul {module} ne odgovara, ostala njegova vrata u ovom otvaranju se preskaču." : "."));
					await SaveAndPublishAsync().ConfigureAwait(false);
				}
			}
			catch (Exception ex) {
				_logger?.LogError(ex, $"{nameof(RemoteServiceSession)} {SessionId}: otvaranje je prekinuto greškom");
			}
			finally {
				lock (_lock) {
					run.Finished = true;
					_currentRun = null;
					_doorInProgress = null;
					var deadline = _timeProvider.GetUtcNow() + _options.IdleTimeout;
					if (deadline > _idleDeadline) {
						_idleDeadline = deadline;
					}
				}
				if (run.RequestId != null && !await SaveRunAsync(run).ConfigureAwait(false)) {
					_logger?.LogError($"{nameof(RemoteServiceSession)} {SessionId}: kraj otvaranja {run.RequestId} nije upisan u dnevnik.");
				}
				Changed?.Invoke();
			}
		}

		// Pod _lock-om. Redni broj se dodeljuje u trenutku slanja (namera), pa redosled upisa prati redosled komandi.
		private DoorOpening NewOpening(OpenDoorsRun run, int door)
		{
			_sequence++;
			return new DoorOpening {
				Sequence = _sequence,
				DoorNumber = door,
				Source = run.Source,
				RequestId = run.RequestId,
				AtUtc = _timeProvider.GetUtcNow(),
			};
		}

		// Pod _lock-om: ishod je konačan i vidljiv Agentu.
		private void CompleteOpening(OpenDoorsRun run, DoorOpening opening, string outcome)
		{
			opening.Outcome = outcome;
			_openings.Add(opening);
			run.Openings.Add(opening);
			_lastOutcomeByDoor[opening.DoorNumber] = outcome;
			TrackPossiblyOpen(_possiblyOpen, opening.DoorNumber, outcome);
			_lastVisibleSequence = Math.Max(_lastVisibleSequence, opening.Sequence);
			var deadline = _timeProvider.GetUtcNow() + _options.IdleTimeout;
			if (deadline > _idleDeadline) {
				_idleDeadline = deadline;
			}
		}

		// Pod _lock-om.
		private void AddModuleError(int module)
		{
			_sequence++;
			_lastVisibleSequence = _sequence;
			_moduleErrors.Add(new ModuleError { Sequence = _sequence, ModuleNumber = module, AtUtc = _timeProvider.GetUtcNow() });
			_failedModules.Add(module);
		}

		private int ModuleOf(int door) => (door - 1) / _controller.NumberOfDoorsPerController + 1;

		/// <summary>
		/// Pre svake komande: DoorWatcher ne sme biti usred provere, a serijska magistrala traži razmak od kraja prethodne
		/// komande. Prva komanda sesije čeka ceo razmak, jer ne zna kad je DoorWatcher poslao svoju poslednju.
		/// </summary>
		private async Task WaitForControllerAsync()
		{
			if (_pauseDoorWatcher != null) {
				await _pauseDoorWatcher().ConfigureAwait(false);
			}
			if (_options.CommandGap <= TimeSpan.Zero) {
				return;
			}
			long last = Interlocked.Read(ref _lastCommandFinished);
			var remaining = last == 0 ? _options.CommandGap : _options.CommandGap - _timeProvider.GetElapsedTime(last);
			if (remaining > TimeSpan.Zero) {
				await _delay(remaining).ConfigureAwait(false);
			}
		}

		private void MarkCommandFinished() => Interlocked.Exchange(ref _lastCommandFinished, Math.Max(1, _timeProvider.GetTimestamp()));

		private async Task SaveAndPublishAsync()
		{
			await SaveSessionAsync().ConfigureAwait(false);
			int sequence;
			lock (_lock) {
				sequence = _lastVisibleSequence;
			}
			_status?.SetOperationSequence(SessionId, sequence);
			Changed?.Invoke();
		}

		private async Task<bool> SaveSessionAsync()
		{
			await _saveGate.WaitAsync().ConfigureAwait(false);
			try {
				AgentOperationRecord record;
				lock (_lock) {
					var ended = _phase == RemoteServicePhases.Ended;
					record = new AgentOperationRecord {
						RequestId = SessionId,
						Command = AgentCommands.BeginRemoteService,
						Fingerprint = _options.Fingerprint,
						ResultCode = ended ? AgentResultCodes.Completed : AgentResultCodes.InProgress,
						Message = ended ? $"Servisna sesija je završena ({_endMode})." : null,
						Result = AgentJson.ToResult(BuildFullResult(includePending: true)),
						CreatedUtc = _startedAt,
					};
				}
				return await _journal.SaveAsync(record).ConfigureAwait(false);
			}
			finally {
				_saveGate.Release();
			}
		}

		private async Task<bool> SaveRunAsync(OpenDoorsRun run)
		{
			AgentOperationRecord record;
			lock (_lock) {
				record = new AgentOperationRecord {
					RequestId = run.RequestId!,
					Command = AgentCommands.OpenDoors,
					Fingerprint = run.Fingerprint,
					ResultCode = run.Finished ? AgentResultCodes.Completed : AgentResultCodes.InProgress,
					Result = AgentJson.ToResult(BuildRunResult(run)),
					CreatedUtc = run.CreatedUtc,
				};
			}
			return await _journal.SaveAsync(record).ConfigureAwait(false);
		}

		// Pod _lock-om. Kopije upisa: serijalizacija ide van lock-a. Namera (includePending) ide samo u dnevnik.
		private RemoteServiceResult BuildFullResult(bool includePending = false) => new() {
			SessionId = SessionId,
			Phase = _phase,
			EndMode = _phase == RemoteServicePhases.Ended ? _endMode : null,
			StartedAtUtc = _startedAt,
			AuthenticatedAtUtc = _authenticatedAt,
			EndedAtUtc = _endedAt,
			FailedAuthAttempts = _failedAuthAttempts,
			IgnoredDoors = [.. _options.IgnoredDoors],
			UnavailableModulesAtStart = [.. _options.UnavailableModulesAtStart],
			DoorsNotClosed = _doorsNotClosed == null ? null : [.. _doorsNotClosed],
			Openings = _openings.Where(o => includePending || o.Sequence <= _lastVisibleSequence).OrderBy(o => o.Sequence).Select(Copy).ToList(),
			ModuleErrors = _moduleErrors.Select(e => new ModuleError { Sequence = e.Sequence, ModuleNumber = e.ModuleNumber, AtUtc = e.AtUtc }).ToList(),
			LastSequence = _lastVisibleSequence,
		};

		// Pod _lock-om.
		private OpenDoorsResult BuildRunResult(OpenDoorsRun run) => new() {
			SessionId = SessionId,
			Finished = run.Finished,
			Openings = run.Openings.OrderBy(o => o.Sequence).Select(Copy).ToList(),
		};

		private static DoorOpening Copy(DoorOpening o) => new() {
			Sequence = o.Sequence, DoorNumber = o.DoorNumber, Source = o.Source, RequestId = o.RequestId, Outcome = o.Outcome, AtUtc = o.AtUtc,
		};

		/// <summary>
		/// Vrata su možda otvorena posle <see cref="DoorOpenOutcomes.OpenConfirmed"/> ili <see cref="DoorOpenOutcomes.OutcomeUnknown"/>, a
		/// zatvorena tek kad kontroler javi zatvoreno. <see cref="DoorOpenOutcomes.ModuleUnavailable"/> i
		/// <see cref="DoorOpenOutcomes.NotStarted"/> nisu poslati, pa ne menjaju ranije stanje.
		/// </summary>
		private static void TrackPossiblyOpen(HashSet<int> possiblyOpen, int door, string outcome)
		{
			if (outcome is DoorOpenOutcomes.OpenConfirmed or DoorOpenOutcomes.OutcomeUnknown) {
				possiblyOpen.Add(door);
			}
			else if (outcome == DoorOpenOutcomes.OpenNotConfirmed) {
				possiblyOpen.Remove(door);
			}
		}

		/// <summary>
		/// Deo punog rezultata za jedan odgovor: upisi (otvaranja i greške modula, zajedno po rednom broju) posle
		/// <paramref name="afterSequence"/>, najviše <see cref="RemoteServiceLimits.MaxEntriesPerResult"/>; zbirna polja cela.
		/// Upis van <see cref="RemoteServiceResult.LastSequence"/> (namera bez ishoda) se ne vraća.
		/// </summary>
		public static RemoteServiceResult Page(RemoteServiceResult full, int? afterSequence)
		{
			int after = afterSequence ?? 0;
			var entries = full.Openings.Select(o => (o.Sequence, Opening: (DoorOpening?)o, Error: (ModuleError?)null))
				.Concat(full.ModuleErrors.Select(e => (e.Sequence, Opening: (DoorOpening?)null, Error: (ModuleError?)e)))
				.Where(x => x.Sequence > after && x.Sequence <= full.LastSequence)
				.OrderBy(x => x.Sequence)
				.ToList();
			var page = entries.Take(RemoteServiceLimits.MaxEntriesPerResult).ToList();
			return new RemoteServiceResult {
				SessionId = full.SessionId,
				Phase = full.Phase,
				EndMode = full.EndMode,
				StartedAtUtc = full.StartedAtUtc,
				AuthenticatedAtUtc = full.AuthenticatedAtUtc,
				EndedAtUtc = full.EndedAtUtc,
				FailedAuthAttempts = full.FailedAuthAttempts,
				IgnoredDoors = full.IgnoredDoors,
				UnavailableModulesAtStart = full.UnavailableModulesAtStart,
				DoorsNotClosed = full.DoorsNotClosed,
				Openings = page.Where(x => x.Opening != null).Select(x => x.Opening!).ToList(),
				ModuleErrors = page.Where(x => x.Error != null).Select(x => x.Error!).ToList(),
				LastSequence = full.LastSequence,
				HasMore = entries.Count > page.Count,
			};
		}

		/// <summary>
		/// Oporavak zapisa sesije posle pada procesa (<see cref="AgentOperationJournal.Initialize"/>): sesija se završava kao
		/// <see cref="RemoteServiceEndModes.ProcessRestart"/>; namera bez ishoda postaje vidljiv upis
		/// <see cref="DoorOpenOutcomes.OutcomeUnknown"/>, a vrata otvorena ili sa nepoznatim ishodom idu u
		/// <see cref="RemoteServiceResult.DoorsNotClosed"/> (stanje se ne čita). Ništa se ne ponavlja.
		/// </summary>
		public static AgentOperationRecord RecoverSessionRecord(AgentOperationRecord record)
		{
			var result = record.Result is { ValueKind: JsonValueKind.Object } json ? json.Deserialize<RemoteServiceResult>(AgentJson.Options) : null;
			if (result == null) {
				return record with { ResultCode = AgentResultCodes.OutcomeUnknown, Message = "Program kioska je prekinut usred servisne sesije; zapis sesije nema rezultat." };
			}
			int last = result.Openings.Select(o => o.Sequence).Concat(result.ModuleErrors.Select(e => e.Sequence)).Append(result.LastSequence).Max();
			var possiblyOpen = new HashSet<int>();
			foreach (var opening in result.Openings.OrderBy(o => o.Sequence)) {
				TrackPossiblyOpen(possiblyOpen, opening.DoorNumber, opening.Outcome);
			}
			result.Phase = RemoteServicePhases.Ended;
			result.EndMode = RemoteServiceEndModes.ProcessRestart;
			result.EndedAtUtc = record.UpdatedUtc;
			result.DoorsNotClosed = possiblyOpen.Order().ToList();
			result.LastSequence = last + 1;
			result.HasMore = false;
			return record with {
				ResultCode = AgentResultCodes.Completed,
				Message = "Program kioska je prekinut usred servisne sesije; sesija je zaključena pri sledećem startu.",
				Result = AgentJson.ToResult(result),
			};
		}
	}
}
