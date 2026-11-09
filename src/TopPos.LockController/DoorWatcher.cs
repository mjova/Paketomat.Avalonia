using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace TopPos.LockController
{
	public class DoorWatcher
	{
		public class DoorStatus
		{
			public int DoorNumber { get; set; }

			// Status vrata: može biti null (nepoznat), ili imati vrednost (otvoreno/zatvoreno)
			public DoorStatusCode? Status { get; protected set; }

			// vrata su zaglavljena i zatvorena, ne treba im resetovati status jer nisu operativna
			// ako su vrata otvorena, onda više nisu zaglavljena i ovaj flag treba postaviti na false
			public bool IsInFailedState { get; protected set; }

			[JsonConstructor]
			public DoorStatus(int doorNumber, DoorStatusCode? status, bool isInFailedState = false)
			{
				DoorNumber= doorNumber;
				Status= status;
				IsInFailedState= isInFailedState;
			}

			public void ClearStatus() => Status = null;
		}

		public class DoorWatcherData
		{
			// loaded from file (if any)
			public DateTime? LastUpdateTime { get; set; }
			/// <summary>
			/// null - unknown,
			/// true - available,
			/// false - not available
			/// </summary>
			public bool?[] ModuleIsAvailable { get; set; }
			public List<DoorStatus>? PreviousDoorStatuses { get; set; }

			public DoorWatcherData(int installedDoors, int numberOfDoorsPerController)
			{
				ModuleIsAvailable= new bool?[GetModuleCount(installedDoors, numberOfDoorsPerController)];
				for(int i = 0; i<ModuleIsAvailable.Length; i++)
					ModuleIsAvailable[i] = null;
			}

			[JsonConstructor]
			public DoorWatcherData(DateTime? lastUpdateTime, bool?[] moduleIsAvailable, List<DoorStatus>? previousDoorStatuses)
			{
				LastUpdateTime= lastUpdateTime;
				ModuleIsAvailable= moduleIsAvailable;
				PreviousDoorStatuses = previousDoorStatuses;
			}

		}

		// Current statuses
		public List<DoorStatus>? DoorStatuses { get; private set; }

		// private readonly AppConfiguration _configuration;
		private readonly ILogger? _logger;

		private ILockControllerClient _client;
		private TimeSpan _timeSpanAllDoors;
		private TimeSpan _readWriteTimeOut;
		private readonly TimeSpan _multipleDoorsCheckCooldown;
		private readonly int _installedDoors;
		private readonly Task? _taskMainLoop;
		private DoorWatcherData? _data;

		// _running, _ctsAbortCurrentOperation i _ioInFlight menjaju se samo pod _stateLock: Pause/Resume stižu sa UI i pool niti.
		// Invarijanta: _running == true  <=>  _ctsAbortCurrentOperation != null.
		private readonly object _stateLock = new();
		private volatile bool _running;		// čita se i van lock-a (MainLoop)
		private CancellationToken _masterCancellationToken;
		// local cancellation token, control pause/resume
		private CancellationTokenSource? _ctsAbortCurrentOperation;
		// kontrolerski poziv u toku (CheckDoorMultiple); PauseAsync čeka samo njega, ne i obradu statusa posle njega
		private TaskCompletionSource? _ioInFlight;

		int _moduleNo;
		int _lastModuleNumber;

        private const int MinModuleNumber = 0x01;

        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="client">RS232 controller interface</param>
        /// <param name="numberOfDoors">Number of installed doors.</param>
        /// <param name="mainLoopTimeSpan">Frequency of doors status checks (30s)</param>
        /// <param name="getDoorStatusTimeSpan">Time required to perform module status check (2.5s)</param>
        /// <param name="doorsStatusesFile">File name where door statuses will be saved (json)</param>
        /// <param name="notificationFn">Function that will be called if a door status change occurs.</param>
        /// <param name="logger"></param>
        /// <param name="cancellationToken"></param>
        public DoorWatcher(ILockControllerClient client,
			int numberOfDoors,
			TimeSpan mainLoopTimeSpan,
			TimeSpan getDoorStatusTimeSpan,
			TimeSpan readWriteTimeOut,
			Func<bool?, IEnumerable<DoorStatus>, CancellationToken, ValueTask>? statusUpdateHandler,
			bool startImmediately,
            ILogger? logger,
			CancellationToken cancellationToken)
		{
			// _configuration= configuration;
			_logger = logger;
			_client = client;
			_data= new DoorWatcherData(numberOfDoors, _client.NumberOfDoorsPerController);

			_data.PreviousDoorStatuses = null;
			_timeSpanAllDoors = mainLoopTimeSpan;
			_multipleDoorsCheckCooldown = getDoorStatusTimeSpan;
			_masterCancellationToken = cancellationToken;
            StatusUpdateHandler = statusUpdateHandler;
			_installedDoors = numberOfDoors;
			_readWriteTimeOut= readWriteTimeOut;

			_moduleNo= -1;			// 1 ... x
			_lastModuleNumber = GetModuleCount(_installedDoors, _client.NumberOfDoorsPerController);

            _jsonOptions = new JsonSerializerOptions() { WriteIndented = true };
            _jsonOptions.Converters.Add(new DoorStatusCodeJsonConverter());

			DoorStatuses= _data.PreviousDoorStatuses;

			if (startImmediately) { 
				Resume();
			}
			// MainLoop cancellation only by outside token
			_taskMainLoop = MainLoop();	

		}

        public Func<bool?, IEnumerable<DoorStatus>, CancellationToken, ValueTask>? StatusUpdateHandler;

		/// <summary>
		/// Broj modula za dati broj vrata. Poslednji modul moze biti delimično
		/// popunjen (npr. 35 vrata / 8 = 5 modula, peti sa 3 vrata).
		/// </summary>
		public static int GetModuleCount(int installedDoors, int doorsPerModule)
			=> (installedDoors + doorsPerModule - 1) / doorsPerModule;

		/// <summary>
		/// Vrata modula koja su povezana (prva vrata su 1).
		/// Kontroler vraća status za sve pozicije, ali se gledaju samo vrata do
		/// installedDoors.
		/// </summary>
		public static (int FirstDoor, int LastDoor) GetModuleDoorRange(int moduleNo, int installedDoors, int doorsPerModule)
			=> ((moduleNo - 1) * doorsPerModule + 1, Math.Min(moduleNo * doorsPerModule, installedDoors));

		public bool IsLockerFullyChecked => this.DoorStatuses?.Count == _installedDoors;

		/// <summary>
		/// Moduli (od 1) za koje je provera utvrdila da ne odgovaraju (<see cref="DoorWatcherData.ModuleIsAvailable"/> =
		/// <c>false</c>); modul sa nepoznatim stanjem se ne navodi. Snimak u trenutku poziva.
		/// </summary>
		public IReadOnlyList<int> GetUnavailableModules()
		{
			var available = _data!.ModuleIsAvailable;
			var modules = new List<int>();
			for (int i = 0; i < available.Length; i++) {
				if (available[i] == false) {
					modules.Add(i + 1);
				}
			}
			return modules;
		}

		public bool? IsLockerOperational()
		{
			if (!this.IsLockerFullyChecked)
				return null;		// još nemamo uvid u sva vrata (prvi start)

			return DoorStatuses!.Any(d => d.Status?.IsClosed == true && !d.IsInFailedState);
		}

		/// <summary>
		/// Ako promenjen status vrata van DoorWatcher
		/// </summary>
		/// <param name="doorNumber">Broj vrata</param>
		/// <param name="unexpectedStatusCode">Nov status</param>
		public void InvalidateDoorStatus(int doorNumber)
		{
			_logger?.LogDebug($"{nameof(InvalidateDoorStatus)}: status vrata {doorNumber} je od sada nepoznat.");

            if (_data!.PreviousDoorStatuses != null) {
				DoorStatus? door = _data!.PreviousDoorStatuses.Find(d => d.DoorNumber == doorNumber);
				if(door != null) {
					/// samo ako već postoji status vrata, podesi ga
					/// ako vrata nemaju status (nisu u listi), status će biti zabeležen sledećim čitanjem.
					door.ClearStatus();
					_logger?.LogDebug($"{nameof(InvalidateDoorStatus)}: status vrata {door.DoorNumber} je invalidiran, prethodni status je bio {door.Status}.");
				}
			}
			/// else  ako nema liste ili vrata - onda ne treba ništa raditi
		}

		private async ValueTask<List<DoorStatus>?> GetNextModuleDoorStatuses(CancellationToken cancellationToken)
		{
			List<DoorStatus> currentDoorStatuses = new List<DoorStatus>();
			// moduleDoorStatuses.Clear();

			if(_moduleNo < MinModuleNumber || _moduleNo > _lastModuleNumber)
				_moduleNo = MinModuleNumber;

			try {

				var (firstDoor, lastDoor) = GetModuleDoorRange(_moduleNo, _installedDoors, _client.NumberOfDoorsPerController);
				_logger?.LogDebug($"{nameof(DoorWatcher)}: {nameof(GetNextModuleDoorStatuses)}: provera statusa modula [{_moduleNo}], pozicije od {firstDoor} do {lastDoor}..");
				var statuses = await _client.CheckDoorMultiple(_moduleNo, _readWriteTimeOut, _readWriteTimeOut, cancellationToken);
				if(cancellationToken.IsCancellationRequested)
					return null;
				if(statuses != null) {
					// pozicije bez vrata (delimicno popunjen modul) označavamo sa '-'
					var shortStatuses = statuses.Select((s, j) => firstDoor+j > lastDoor ? "-" : s.IsClosed? "X" : "O");
					string logStatuses = $"|{string.Join('|', shortStatuses)}|";
					_logger?.LogDebug($"{nameof(DoorWatcher)}: {nameof(GetNextModuleDoorStatuses)}: status modula [{_moduleNo}]: {logStatuses}.");

					_data!.ModuleIsAvailable[_moduleNo-1]= true;
					for(int j = 0; j<statuses.Length && firstDoor+j <= lastDoor; j++) {
						DoorStatus ds = new DoorStatus(firstDoor+j, statuses[j]);
						currentDoorStatuses.Add(ds);
					}
					// module to check next time 
					_moduleNo++;
				}
				else {
					// error reading module
					bool? previousStatus = _data!.ModuleIsAvailable[_moduleNo-1];

					if(previousStatus == true) {
						// if previous status was 'available' (true), then set status to 'unknown',
						_logger?.LogDebug($"{nameof(DoorWatcher)}: {nameof(GetNextModuleDoorStatuses)}: status modula [{_moduleNo}] nije dobijen, ponovi proveru statusa.");
						_data!.ModuleIsAvailable[_moduleNo-1]= null;
						// skip reporting and scan this module again in next loop
						// _moduleNo++; do not inc, check same module again
					}
					else {
						// if previous status was 'not available' (false), then it is still 'not available' (false)
						// if previous status was 'unknown' (null), then set status to 'not available' (false)
						_logger?.LogDebug($"{nameof(DoorWatcher)}: {nameof(GetNextModuleDoorStatuses)}: status modula [{_moduleNo}] nije dobijen. Sva vrata modula su u statusu <nepoznat>.");
						_data!.ModuleIsAvailable[_moduleNo-1]= false;
						//		and create the report
						for(int door = firstDoor; door <= lastDoor; door++)
							currentDoorStatuses.Add(new DoorStatus(door, null));
						// module to check next time 
						_moduleNo++;
					}
				}
			}
			catch(Exception ex) {
				_logger?.LogError($"{nameof(DoorWatcher)}: {nameof(GetNextModuleDoorStatuses)}: {ex.Message}.");
			}

			return currentDoorStatuses;
		}


		/// <summary>Zaustavlja proveru i otkazuje kontrolerski poziv u toku, ali ga ne čeka (vidi <see cref="PauseAsync"/>).</summary>
		public void Pause() => _ = PauseAsync();

		/// <summary>
		/// Zaustavlja proveru; vraćeni Task se završava kad se završi kontrolerski poziv koji je bio u toku (ako ga je bilo).
		/// Ne čeka obradu statusa posle poziva: iz nje (StatusUpdateHandler → … → otvaranje vrata) može da se stigne do ove
		/// metode, pa bi čekanje cele iteracije čekalo samo sebe. Bezbedno za pozive iz više niti; ponovljen poziv ne smeta.
		/// </summary>
		public Task PauseAsync()
		{
			CancellationTokenSource? cts;
			Task pendingIo;
			lock(_stateLock) {
				pendingIo = _ioInFlight?.Task ?? Task.CompletedTask;
				if(!_running)
					return pendingIo;
				_running= false;
				cts = _ctsAbortCurrentOperation;
				_ctsAbortCurrentOperation = null;
			}
			_logger?.LogInformation($"{nameof(DoorWatcher)}: {nameof(Pause)}: Zaustavljanje provere statusa.");
			DebugLogStatuses(nameof(Pause), _data!.PreviousDoorStatuses);
			cts?.Cancel();		// van lock-a: otkazivanje sinhrono izvršava registracije (kontroler)
			return pendingIo;
		}

		public void Resume()
		{
			int moduleNo;
			lock(_stateLock) {
				if(_running)
					return;
				if(_moduleNo < MinModuleNumber || _moduleNo > _lastModuleNumber)
					_moduleNo = MinModuleNumber;
				moduleNo = _moduleNo;
				_ctsAbortCurrentOperation= new CancellationTokenSource();
				_running= true;
			}
			_logger?.LogInformation($"{nameof(DoorWatcher)}: {nameof(Resume)}: Nastavak provere statusa (modul {moduleNo}).");
			DebugLogStatuses(nameof(Resume), _data!.PreviousDoorStatuses);
		}

		public void Restart()
		{
			lock(_stateLock) {
				if(_running)
					return;
				_moduleNo= -1;
			}
			_logger?.LogInformation($"{nameof(DoorWatcher)}: {nameof(Restart)}: Resetovanje tekućeg modula na početni.");
			DebugLogStatuses(nameof(Restart), _data!.PreviousDoorStatuses);
			Resume();
		}

		/// <summary>
		/// Ako provera radi: token za tekuću iteraciju i prijava kontrolerskog poziva (pod istim lock-om kao Pause, pa
		/// PauseAsync posle ove tačke sigurno vidi poziv u toku). Inače null.
		/// </summary>
		private TaskCompletionSource? TryBeginIo(out CancellationToken abortToken)
		{
			lock(_stateLock) {
				if(!_running) {
					abortToken = default;
					return null;
				}
				abortToken = _ctsAbortCurrentOperation!.Token;
				_ioInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				return _ioInFlight;
			}
		}

		private void EndIo(TaskCompletionSource io)
		{
			lock(_stateLock) {
				if(_ioInFlight == io)
					_ioInFlight = null;
			}
			io.TrySetResult();
		}

		private void DebugLogStatuses(string callingFrom, List<DoorStatus>? doorStatuses)
		{
			char cClosed= 'X';
			char cOpen = 'O';
			char cUnknown = '.';
			char cNotFound = '_';

			if(callingFrom == null)
				callingFrom= nameof(DebugLogStatuses);

			if(doorStatuses == null) {
				_logger?.LogInformation($"{nameof(DoorWatcher)}: {callingFrom}: doorStatuses == null.");
				return;
			}

			try {
				_logger?.LogInformation($"{nameof(DoorWatcher)}: {callingFrom}: Installed/Scanned doors= {_installedDoors}/{doorStatuses.Count()}");
				_logger?.LogInformation($"{nameof(DoorWatcher)}: {callingFrom}: Legend/Door Closed='{cClosed}', Open='{cOpen}', Unknown='{cUnknown}', NotScanned='{cNotFound}'");

				string sb = "";
				for(int i = 0; i < _installedDoors; i++) {
					if(i % _client.NumberOfDoorsPerController == 0)
						sb= $"Module: {i/_client.NumberOfDoorsPerController+1}: ";
					DoorStatus? door = doorStatuses.Find(item => item.DoorNumber == i+1);
					if(door == null)
						sb+= cNotFound;
					else if(door.Status == null)
						sb+= cUnknown;
					else
						sb+= door.Status?.IsOpen == true? cOpen : cClosed;
					// kraj modula ili kraj poslednjeg modula
					if(i % _client.NumberOfDoorsPerController == _client.NumberOfDoorsPerController-1 || i == _installedDoors-1)
						_logger?.LogInformation($"{nameof(DoorWatcher)}: {callingFrom}: {sb} ");
				}

			}
			catch(Exception ex) {
				// to do or not to do
				_logger?.LogError($"{nameof(DoorWatcher)}: {callingFrom}: {ex.Message}.");
			}

		}

		private async Task MainLoop()
		{
			PeriodicTimer? timer = new PeriodicTimer(_timeSpanAllDoors);
			_logger?.LogInformation($"{nameof(DoorWatcher)}: {nameof(MainLoop)}: timer postavljen na {_timeSpanAllDoors}");
			// DebugLogStatuses(nameof(MainLoop)+"/Enter", _data!.PreviousDoorStatuses);

			bool initialNotificationSent = false;

            while(!_running) {
                await Task.Delay(500);
            }

            _logger?.LogInformation($"{nameof(DoorWatcher)}: {nameof(MainLoop)}: Start.");

            do
            { // loop for locker

				if(_running) {
					do { // scan module by module
						if(_masterCancellationToken.IsCancellationRequested || !_running) {
							_logger?.LogInformation($"{nameof(DoorWatcher)}: {nameof(MainLoop)}: Zaustavljanje provere.");
							break;  // pause: go to main loop
						}

						var io = TryBeginIo(out var abortToken);
						if(io == null) {
							_logger?.LogInformation($"{nameof(DoorWatcher)}: {nameof(MainLoop)}: Zaustavljanje provere.");
							break;  // pause: go to main loop
						}
						try {
							using var ctsCurrentExecution = CancellationTokenSource.CreateLinkedTokenSource(_masterCancellationToken, abortToken);

							// provera statusa vrata na modulu (8 kom)
							List<DoorStatus>? moduleDoorStatuses;
							try {
								moduleDoorStatuses = await GetNextModuleDoorStatuses(ctsCurrentExecution.Token);
							}
							finally {
								EndIo(io);		// pre pauze i obrade statusa: PauseAsync čeka samo kontroler
							}
							if(moduleDoorStatuses == null) {
								// token cancel while GetNextModuleDoorStatuses()
								_logger?.LogInformation($"{nameof(DoorWatcher)}: {nameof(MainLoop)}: Zaustavljanje provere.");
								break;  // pause: go to main loop
							}

							// wait 2.5s
							await Task.Delay(_multipleDoorsCheckCooldown, ctsCurrentExecution.Token).ConfigureAwait(false);
							if(ctsCurrentExecution.IsCancellationRequested || !_running) {
								_logger?.LogInformation($"{nameof(DoorWatcher)}: {nameof(MainLoop)}: Zaustavljanje provere.");
								break;  // pause: go to main loop
							}

							// ovde je _moduleNo već ++ u odnosu na ono što je GetNextModuleDoorStatuses obradio
							_logger?.LogInformation($"{nameof(DoorWatcher)}: {nameof(MainLoop)}: provera statusa modula ({_moduleNo-1}) zavrsena.");

							if(_data!.PreviousDoorStatuses == null)
								_data.PreviousDoorStatuses= new List<DoorStatus>();

							// uporedi da li postoje promene proverenog modula s prethodnim stanjem
							var differences = moduleDoorStatuses?.Where(
										item1 => !_data.PreviousDoorStatuses.Any(item2 => item2.DoorNumber == item1.DoorNumber && item2.Status == item1.Status)
									).Concat(
										moduleDoorStatuses.Where(
											item2 => !moduleDoorStatuses.Any(item1 => item1.DoorNumber == item2.DoorNumber && item1.Status == item2.Status
										)
									)
								).ToList();

							if(differences == null)
								throw new Exception($"{nameof(differences)} is null.");

							if(StatusUpdateHandler != null) {
								if(differences.Any()) {

									if(moduleDoorStatuses == null)
										throw new Exception($"{nameof(moduleDoorStatuses)} is null.");
									moduleDoorStatuses.AddRange(_data.PreviousDoorStatuses.Where(e1 => !moduleDoorStatuses.Any(e2 => e2.DoorNumber == e1.DoorNumber)));
									DebugLogStatuses(nameof(MainLoop)+"/Differences", moduleDoorStatuses);

									// update PreviousDoorStatuses with current info
									_data.PreviousDoorStatuses.Clear();
									_data.PreviousDoorStatuses.AddRange(moduleDoorStatuses);
									_data.PreviousDoorStatuses.Sort((x, y) => x.DoorNumber.CompareTo(y.DoorNumber));
									DoorStatuses= _data.PreviousDoorStatuses;

									await StatusUpdateHandler(IsLockerOperational(), differences, ctsCurrentExecution.Token);
									initialNotificationSent = true;
								}
								else if(!initialNotificationSent && IsLockerFullyChecked) {
									await StatusUpdateHandler(IsLockerOperational(), Enumerable.Empty<DoorStatus>(), ctsCurrentExecution.Token).ConfigureAwait(false);
									initialNotificationSent = true;
								}
							}
						}
						catch(OperationCanceledException) when(abortToken.IsCancellationRequested || _masterCancellationToken.IsCancellationRequested) {
							// pauza tokom pauze između modula — očekivano, nije greška
							_logger?.LogInformation($"{nameof(DoorWatcher)}: {nameof(MainLoop)}: Zaustavljanje provere.");
							break;  // pause: go to main loop
						}
						catch(Exception ex) {
							_logger?.LogError($"{nameof(DoorWatcher)}: {nameof(MainLoop)}: {ex.Message}.");
						}


					} while(_moduleNo<=_lastModuleNumber);

				} // if _running

				// and now wait for next pulse

			} while(await timer!.WaitForNextTickAsync(_masterCancellationToken));

		}

	}
}