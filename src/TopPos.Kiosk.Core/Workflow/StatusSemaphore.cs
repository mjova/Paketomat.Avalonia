using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TopPos.Kiosk.Workflow
{
	/// <summary>
	/// Semafor kojim spoljni servis (kontrola paketomata) zna da li aplikaciju trenutno
	/// neko koristi (klijent, kurir ili tehničar). Realizovan kao fajl <c>status.lock</c> pored .exe
	/// koji uvek postoji dok aplikacija radi; JSON sadržaj nosi status:
	/// <list type="bullet">
	/// <item><c>Busy=true</c>  → aplikacija je ZAUZETA (neko je u nekom toku)</item>
	/// <item><c>Busy=false</c> → aplikacija je SLOBODNA (StartPage / idle)</item>
	/// <item><c>Operational</c> → operativnost paketomata (kontroler/hardver): <c>true</c> ispravan,
	///       <c>false</c> nije u funkciji, <c>null</c> još nije utvrđeno (npr. na startu). Nezavisno
	///       od <c>Busy</c> — kvar ne znači zauzeto.</item>
	/// <item><c>CleanShutdown</c> → <c>false</c> dok sesija traje, <c>true</c> tek posle završnog upisa
	///       urednog gašenja.</item>
	/// </list>
	/// Uz status ide uloga, trenutno stanje i timestamp poslednjeg upisa — pa servis može da
	/// prepozna i zaglavljenu sesiju (Busy=true sa timestampom starijim od praga) i koliko dugo
	/// je kiosk slobodan (Busy=false).
	///
	/// Izvedeni pisac: u aplikaciji ga hrani <see cref="KioskStatusSource"/> (<see cref="Update"/> na svaku novu reviziju),
	/// pa „slobodan/zauzet“ i uloga dolaze iz <see cref="WorkflowStatusRules"/>, kao i status za Agenta preko pipe-a.
	/// Zadržava se poslednje stanje i svaki upis izliva kompletan snapshot. Time je vreme upisa, ne heartbeat.
	///
	/// Redovni upisi idu van niti koja je javila promenu (prelazi mašine su i na UI niti), serijalizovano, a uzastopne
	/// promene se spajaju u jedan upis poslednjeg stanja. Upis je ojačan: <c>Flush(true)</c> pa <c>File.Replace</c>
	/// (čitalac sa <c>FileShare.Delete</c> ne smeta), uz ograničeno ponavljanje. Početni i završni upis su sinhroni.
	///
	/// Evidencija gašenja: pri startu aplikacije (<see cref="InitializeSession"/>) se zatečeni fajl prvo pročita i u log
	/// upiše ishod prethodne sesije (uredno isključena, ili bez potvrde urednog gašenja posle poslednjeg <c>Time</c>), pa
	/// se tek onda prepiše početnim statusom nove sesije (Idle, slobodno, CleanShutdown=false). Uredno gašenje
	/// (<see cref="MarkCleanShutdown"/>) upisuje CleanShutdown=true; pad, kill ili nestanak struje ostave false.
	///
	/// Svaki upis nosi i izjavu procesa (<see cref="StatusDeclaration"/>): <c>ProcessId</c>, <c>ProcessStartUtc</c> i
	/// <c>AgentChannel</c> (<c>null</c> = kanal ka Agentu isključen).
	/// </summary>
	public class StatusSemaphore
	{
		private const string FileName = "status.lock";
		private const int ReplaceAttempts = 4;
		private static readonly TimeSpan __replaceRetryDelay = TimeSpan.FromMilliseconds(50);

		private readonly string _filePath;
		private readonly string _tempPath;
		private readonly ILogger? _logger;
		// Izjava procesa je ista za celu sesiju; vreme starta se formatira jednom.
		private readonly int _processId;
		private readonly string? _processStartUtc;     // null = vreme starta nije pročitano
		private readonly AgentChannelPayload? _agentChannel;
		// Zadržano stanje i red upisa. Nikad se ne drži dok se čeka _fileLock.
		private readonly object _lock = new();
		// Serijalizuje upise fajla (zajednički .tmp); spolja se uzima pre _lock.
		private readonly object _fileLock = new();

		// Zadržano stanje — svaki upis izliva kompletan snapshot.
		private bool _busy;
		private WorkflowState _state = WorkflowState.Idle;
		private string? _role;        // uloga iz izvora statusa; null = izvesti iz stanja (SetBusy/SetFree)
		private bool? _operational;   // null = još nije utvrđeno
		private bool _cleanShutdown;
		// Posle završnog upisa sesija je zatvorena: zakasneli SetFree/SetBusy/SetOperational ne smeju prepisati završni snapshot.
		private bool _sessionClosed;

		// Verzija zadržanog stanja i poslednja upisana; pisac radi dok se ne izjednače.
		private long _version;
		private long _writtenVersion;
		private bool _writerQueued;

		/// <summary>Izjava tekućeg procesa, bez kanala ka Agentu.</summary>
		public StatusSemaphore(string baseDirectory, ILogger? logger)
			: this(baseDirectory, logger, StatusDeclaration.ForCurrentProcess(agentPipeName: null, logger))
		{
		}

		public StatusSemaphore(string baseDirectory, ILogger? logger, StatusDeclaration declaration)
		{
			_filePath = Path.Combine(baseDirectory, FileName);
			_tempPath = _filePath + ".tmp";
			_logger = logger;
			_processId = declaration.ProcessId;
			// "O" iz UTC DateTime-a: uvek sedam decimala i Z (podrazumevani JSON formatter skida završne nule).
			_processStartUtc = declaration.ProcessStartUtc is { } start
								? DateTime.SpecifyKind(start, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)
								: null;
			_agentChannel = declaration.AgentPipeName is { } pipeName ? new AgentChannelPayload { PipeName = pipeName } : null;
		}

		/// <summary>
		/// Početak sesije — jednom po prihvaćenoj instanci aplikacije, pre uređaja. Prvo u log upiše ishod prethodne sesije iz
		/// zatečenog fajla, pa ga prepiše početnim statusom: Busy=false, State=Idle, Operational=null, CleanShutdown=false.
		/// Greške čitanja i pisanja idu u log i ne prekidaju pokretanje.
		/// </summary>
		public void InitializeSession()
		{
			StatusPayload payload;
			lock (_fileLock)
			{
				lock (_lock)
				{
					LogPreviousSession();
					_busy = false;
					_state = WorkflowState.Idle;
					_operational = null;
					_cleanShutdown = false;
					_writtenVersion = _version;
					payload = BuildPayload();
				}
				WriteSnapshot(payload);
			}
		}

		/// <summary>
		/// Završni upis urednog gašenja: zadržava poslednji status, postavlja CleanShutdown=true i svež timestamp. Čeka redovni
		/// upis koji je u toku; posle završnog nijedan statusni upis više ne menja fajl, a ponovljen poziv ne radi ništa. Ako
		/// upis ne uspe, u fajlu ostaje CleanShutdown=false, pa sledeći start nema potvrdu urednog završetka.
		/// </summary>
		public void MarkCleanShutdown()
		{
			lock (_fileLock)
			{
				StatusPayload payload;
				lock (_lock)
				{
					if (_sessionClosed) {
						return;
					}
					_sessionClosed = true;
					_cleanShutdown = true;
					payload = BuildPayload();
				}
				if (WriteSnapshot(payload)) {
					_logger?.LogInformation("StatusSemaphore: uredno gašenje zabeleženo u {File}.", _filePath);
				}
				else {
					_logger?.LogWarning("StatusSemaphore: uredno gašenje nije zabeleženo; sledeći start neće imati potvrdu urednog gašenja.");
				}
			}
		}

		/// <summary>
		/// Novo stanje iz <see cref="KioskStatusSource"/>: zakazuje upis kompletnog snapshot-a sa svežim timestampom.
		/// <paramref name="role"/> je gotova uloga izvora (vlasnik kioska, npr. Service na Start-u); <c>null</c> = iz stanja.
		/// </summary>
		public void Update(bool busy, WorkflowState state, bool? operational, string? role = null)
		{
			lock (_lock) { _busy = busy; _state = state; _operational = operational; _role = role; ScheduleWrite(); }
		}

		/// <summary>Aplikacija je slobodna (Start/Idle) — zakazuje upis sa Busy=false i svežim timestampom.</summary>
		public void SetFree(WorkflowState state)
		{
			lock (_lock) { _busy = false; _state = state; _role = null; ScheduleWrite(); }
		}

		/// <summary>Aplikacija je zauzeta u datom stanju — zakazuje upis sa Busy=true i svežim timestampom.</summary>
		public void SetBusy(WorkflowState state)
		{
			lock (_lock) { _busy = true; _state = state; _role = null; ScheduleWrite(); }
		}

		/// <summary>
		/// Operativnost paketomata (kontroler/hardver): <c>true</c> ispravan, <c>false</c> nije u
		/// funkciji, <c>null</c> nepoznato. Nezavisno od Busy — zakazuje upis sa svežim timestampom.
		/// </summary>
		public void SetOperational(bool? operational)
		{
			lock (_lock) { _operational = operational; ScheduleWrite(); }
		}

		/// <summary>
		/// Čeka da zakazani redovni upisi završe (testovi; gašenje ne mora — <see cref="MarkCleanShutdown"/> sam čeka upis u
		/// toku). Vraća false ako rok istekne.
		/// </summary>
		public bool WaitForPendingWrites(TimeSpan timeout)
		{
			var deadline = DateTime.UtcNow + timeout;
			lock (_lock)
			{
				while (_writerQueued) {
					var remaining = deadline - DateTime.UtcNow;
					if (remaining <= TimeSpan.Zero || !Monitor.Wait(_lock, remaining)) {
						return !_writerQueued;
					}
				}
				return true;
			}
		}

		// Zakazuje redovni upis van niti pozivaoca; posle završnog upisa se preskače. MORA se pozivati dok se drži _lock.
		private void ScheduleWrite()
		{
			if (_sessionClosed) {
				return;
			}
			_version++;
			if (!_writerQueued) {
				_writerQueued = true;
				ThreadPool.UnsafeQueueUserWorkItem(_ => WriterLoop(), null);
			}
		}

		// Upisuje poslednje zadržano stanje dok ima neupisanih promena; uzastopne promene se spajaju u jedan upis.
		private void WriterLoop()
		{
			while (true) {
				lock (_fileLock) {
					StatusPayload payload;
					long version;
					lock (_lock) {
						if (_sessionClosed || _writtenVersion == _version) {
							_writerQueued = false;
							Monitor.PulseAll(_lock);
							return;
						}
						version = _version;
						payload = BuildPayload();
					}
					// Neuspeh je u logu; sledeća promena pokušava ponovo, bez vrtenja u petlji.
					WriteSnapshot(payload);
					lock (_lock) {
						_writtenVersion = version;
					}
				}
			}
		}

		// MORA se pozivati dok se drži _lock.
		private StatusPayload BuildPayload() => new()
		{
			Busy = _busy,
			Operational = _operational,
			Role = _busy ? _role ?? WorkflowStatusRules.ResolveRole(_state) : "",
			State = _state.ToString(),
			Time = DateTimeOffset.Now,
			CleanShutdown = _cleanShutdown,
			ProcessId = _processId,
			ProcessStartUtc = _processStartUtc,
			AgentChannel = _agentChannel
		};

		// Upisuje snapshot; vraća da li je upis uspeo. MORA se pozivati dok se drži _fileLock (zajednički .tmp), a ne _lock.
		private bool WriteSnapshot(StatusPayload payload)
		{
			try
			{
				var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
				// Atomično pisanje: prvo temp (na disk, da nestanak struje posle zamene ne ostavi prazan fajl), pa zamena,
				// da servis ne pročita polupisan fajl.
				using (var stream = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
					stream.Write(json);
					stream.Flush(flushToDisk: true);
				}
				ReplaceStatusFile();
				return true;
			}
			catch (Exception ex)
			{
				_logger?.LogWarning(ex, "StatusSemaphore: neuspešno pisanje {File} za stanje {State}.", _filePath, payload.State);
				return false;
			}
		}

		// File.Replace (čitalac sa FileShare.Delete ne smeta); Replace traži da cilj postoji, pa prvi upis ide sa Move.
		// Isti obrazac kao LockerStatusUpdater.TryReplaceStatusFile. Posle poslednjeg neuspeha baca izuzetak.
		private void ReplaceStatusFile()
		{
			for (int attempt = 1; ; attempt++) {
				try {
					if (File.Exists(_filePath)) {
						File.Replace(_tempPath, _filePath, destinationBackupFileName: null);
					}
					else {
						File.Move(_tempPath, _filePath);
					}
					return;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
					if (attempt >= ReplaceAttempts) {
						// Replace može da obriše cilj, a da ne preimenuje .tmp (ERROR_UNABLE_TO_MOVE_REPLACEMENT) - ne ostavljati fajl bez cilja.
						if (!File.Exists(_filePath) && File.Exists(_tempPath)) {
							File.Move(_tempPath, _filePath);
							return;
						}
						throw;
					}
					Thread.Sleep(__replaceRetryDelay);
				}
			}
		}

		// Ishod prethodne sesije iz zatečenog fajla. Uzrok se ne utvrđuje (kill, pad i nestanak struje izgledaju isto), a Time je
		// vreme poslednjeg uspešnog upisa, ne trenutak prekida. MORA se pozivati dok se drži _lock, pre prvog upisa sesije.
		private void LogPreviousSession()
		{
			PreviousSession previous;
			try
			{
				previous = ReadPreviousSession();
			}
			catch (FileNotFoundException)
			{
				_logger?.LogInformation("StatusSemaphore: status.lock ne postoji; nema podatka o prethodnom gašenju. Nastavlja se pokretanje.");
				return;
			}
			catch (Exception ex)
			{
				_logger?.LogWarning(ex, "StatusSemaphore: prethodni status.lock nije moguće pouzdano pročitati; ishod prethodnog gašenja nije moguće utvrditi. Nastavlja se pokretanje.");
				return;
			}

			if (previous.CleanShutdown) {
				if (previous.Time is { } lastWrite) {
					_logger?.LogInformation("StatusSemaphore: prethodna sesija je uredno isključena. Poslednji upis: {PreviousTime:o}.", lastWrite);
				}
				else {
					_logger?.LogWarning("StatusSemaphore: prethodna sesija je uredno isključena, ali Time u status.lock nije ispravan; vreme poslednjeg upisa nije poznato.");
				}
			}
			else if (previous.Time is { } lastWrite) {
				_logger?.LogWarning("StatusSemaphore: program je nasilno isključen posle {PreviousTime:o}; u status.lock nema potvrde urednog gašenja. Prethodno stanje: {PreviousState}, Busy={PreviousBusy}.",
					lastWrite, previous.State, previous.Busy);
			}
			else {
				_logger?.LogWarning("StatusSemaphore: program je nasilno isključen; u status.lock nema potvrde urednog gašenja, a vreme prekida nije moguće ograničiti jer Time nije ispravan. Prethodno stanje: {PreviousState}, Busy={PreviousBusy}.",
					previous.State, previous.Busy);
			}
		}

		// Baca FileNotFoundException kad fajla nema, a I/O, pristup i JsonException kad se ne može pouzdano pročitati.
		private PreviousSession ReadPreviousSession()
		{
			// Puno deljenje (i Delete), kao spoljni čitaoci — doc/StatusSemaphore.md.
			using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var document = JsonDocument.Parse(stream);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object) {
				throw new JsonException($"Sadržaj nije JSON objekat ({root.ValueKind}).");
			}

			// Odsutno polje ili null (stariji format) = nema potvrde urednog gašenja; pogrešan tip nije validan status.
			bool cleanShutdown = root.TryGetProperty(nameof(StatusPayload.CleanShutdown), out var clean) && clean.ValueKind switch
			{
				JsonValueKind.True => true,
				JsonValueKind.False or JsonValueKind.Null => false,
				_ => throw new JsonException($"{nameof(StatusPayload.CleanShutdown)} nije bool ({clean.ValueKind}).")
			};
			// Vreme se čita iz polja: neispravno ostaje null, a ne godina 0001 iz podrazumevane deserializacije.
			DateTimeOffset? time = root.TryGetProperty(nameof(StatusPayload.Time), out var t) && t.ValueKind == JsonValueKind.String
									&& t.TryGetDateTimeOffset(out var parsed) ? parsed : null;
			bool? busy = root.TryGetProperty(nameof(StatusPayload.Busy), out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False
							? b.GetBoolean() : null;
			string? state = root.TryGetProperty(nameof(StatusPayload.State), out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;

			return new PreviousSession(cleanShutdown, time, busy, state);
		}

		private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

		private sealed class StatusPayload
		{
			public bool Busy { get; set; }
			public bool? Operational { get; set; }
			public string Role { get; set; } = "";
			public string State { get; set; } = "";
			public DateTimeOffset Time { get; set; }
			public bool CleanShutdown { get; set; }
			public int ProcessId { get; set; }
			// null = vreme starta nije pročitano (Agent ne može da poveže pipe sa procesom → nepoznato stanje).
			public string? ProcessStartUtc { get; set; }
			// null se upisuje (kanal isključen); Agent odsustvo polja i null tretira isto.
			public AgentChannelPayload? AgentChannel { get; set; }
		}

		private sealed class AgentChannelPayload
		{
			public string PipeName { get; set; } = "";
		}

		private sealed record PreviousSession(bool CleanShutdown, DateTimeOffset? Time, bool? Busy, string? State);
	}
}
