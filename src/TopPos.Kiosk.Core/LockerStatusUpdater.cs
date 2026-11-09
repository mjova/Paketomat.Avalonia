using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using TopPos.LockController;
using TopPos.Service;

namespace TopPos.Client.WPF
{
	public static class Helpers
	{
		public static string ToPrijavaGreskePozicije(this DoorStatusCode? doorStatusCode)
			=> doorStatusCode == null ? PrijavaGreskePozicije.DoorStatusUnknown :
				doorStatusCode.Value.IsClosed ? PrijavaGreskePozicije.DoorClosed : PrijavaGreskePozicije.DoorOpen;

        public static string Format(this DoorStatusCode? status)
        {
            if (status is null) {
                return "?";
            }
            var s = status.Value;
            if (s.IsOpen) {
                return "O";
            }
            if (s.IsClosed) {
                return "X";
            }
            return $"0x{s.RawValue:X2}";
        }

        public static string Format(this DoorWatcher.DoorStatus d)
        {
            var sb = new StringBuilder();
            sb.Append($"{d.DoorNumber}:{d.Status.Format()}");
            if (d.IsInFailedState) {
                sb.Append("(F)");
			}   
            return sb.ToString();
		}
	}


	public record LockerAndDoorsStatus(bool? PendingIsLockerOperational, 
                                        IEnumerable<DoorWatcher.DoorStatus>? PendingDoorStatuses,
                                        bool? ServerIsLockerOperational,
                                        IEnumerable<DoorWatcher.DoorStatus>? ServerDoorStatuses)
    {
        public string SerializeStatusCompact()
        {
            static char EncodeBool(bool? value) => value.HasValue ? (value.Value ? 'T' : 'F') : '-';

            var sb = new StringBuilder();
            sb.Append("PendingLocker=").Append(EncodeBool(PendingIsLockerOperational));
            sb.Append(" | ServerLocker=").Append(EncodeBool(ServerIsLockerOperational));
            sb.Append(" | PendingDoors=").Append(FormatDoorStatuses(PendingDoorStatuses));
            sb.Append(" | ServerDoors=").Append(FormatDoorStatuses(ServerDoorStatuses));
            return sb.ToString();
        }
        

        public static string FormatDoorStatuses(IEnumerable<DoorWatcher.DoorStatus>? statuses)
        {
            if (statuses?.Any() != true) {
                return "<no statuses>";
            }
            var items = statuses.OrderBy(d => d.DoorNumber)
                                .Select(d => d.Format());
            return string.Join(",", items);
        }
    }

    public class LockerStatusUpdater
    {
        private readonly LockerStatusUpdaterConfiguration _configuration;
        private readonly TopPosServiceClient _serviceClient;
        private bool? _serverIsLockerOperational;
        private readonly Dictionary<int, DoorWatcher.DoorStatus> _serverDoorStatuses; // successfully sent to the server
        private bool? _pendingIsLockerOperational;
        private bool? _appIsLockerOperational;  // poslednji status javljen aplikaciji (LockerStatusChanged)
        private readonly Dictionary<int, DoorWatcher.DoorStatus> _pendingDoorStatuses;
        // Release samo ako je semafor preuzet: otkazan WaitAsync ga ne preuzima, a Release bez preuzimanja pustio bi
        // drugu nit da menja statuse (Dictionary) dok prva još radi. Zato čekanje pre try, ili acquired flag gde catch mora da ostane.
        private readonly SemaphoreSlim _semaphore;
        private readonly TaskCompletionSource<bool> _ctsInitializationCompleted;
        private bool _sendLastKnownLockerStatusToServer;
        private bool _sendInitialLockerStatusUpdateToUi;
        private bool _skipDoorStatusesThisIteration;
        private bool _deferLockerOfflineStatus;
        private DateTime? _lastHeartbeatTime;
        private readonly TimeSpan _pollInterval;
        private readonly CancellationTokenSource _cts = new();
        private readonly ILogger? _logger;
        private static readonly JsonSerializerOptions __jsonOptions;
        private const int ReplaceStatusFileAttempts = 5;
        private static readonly TimeSpan __replaceStatusFileRetryDelay = TimeSpan.FromMilliseconds(50);

        static LockerStatusUpdater()
        {
            __jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            __jsonOptions.Converters.Add(new DoorStatusCodeJsonConverter());
        }

        public LockerStatusUpdater(LockerStatusUpdaterConfiguration configuration,
                                   TopPosServiceClient serviceClient, 
                                    int numberOfDoors,
                                    ILogger? logger)
        {
            _configuration = configuration;
            _serviceClient = serviceClient;
            _serverDoorStatuses = new ();
            _pendingDoorStatuses = new();
            _semaphore = new SemaphoreSlim(1, 1);
            _ctsInitializationCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _sendLastKnownLockerStatusToServer = false; // If available, always send the last known status on startup, because the server has put us offline during restart
            _sendInitialLockerStatusUpdateToUi = true; 

            _pollInterval = TimeSpan.FromMilliseconds(_configuration.ProcessingDelayInMilliseconds ?? 1000);
        
            _logger = logger;
            Task.Run(() => ProcessPendingStatusesLoop(numberOfDoors, _cts.Token));
        }

        public Func<bool, ValueTask>? LockerStatusChanged { get; set; }

        public bool? ServerIsLockerOperational => _serverIsLockerOperational;
        public bool? PendingIsLockerOperational => _pendingIsLockerOperational;

        public Task Stop() => Task.Run(() => _cts.Cancel());

		public async ValueTask UpdateStatuses(bool? isLockerOperational, IEnumerable<DoorWatcher.DoorStatus> updates, CancellationToken ct)
            => await UpdateStatusesInternal(isLockerOperational, updates, failedStatuses: false, ct).ConfigureAwait(false);

		public async ValueTask SetFailedStatuses(bool? isLockerOperational, IEnumerable<DoorWatcher.DoorStatus> updates, CancellationToken ct)
			=> await UpdateStatusesInternal(isLockerOperational, updates, failedStatuses: true, ct).ConfigureAwait(false);

		private async ValueTask UpdateStatusesInternal(bool? isLockerOperational, IEnumerable<DoorWatcher.DoorStatus> updates, bool failedStatuses, CancellationToken ct)    
        {
            bool acquired = false;
            try
            {
                if (!_ctsInitializationCompleted.Task.IsCompleted) {
                    _logger?.LogInformation($"{nameof(LockerStatusUpdater)}: {nameof(UpdateStatuses)}: cekanje da se {nameof(LockerStatusUpdater)} inicijalizacija zavrsi...");
                    // Svaki pozivalac čeka sa svojim tokenom — otkazivanje jednog (npr. DoorWatcher.Pause) ne sme da otkaže
                    // zajednički signal inicijalizacije za sve ostale.
                    await _ctsInitializationCompleted.Task.WaitAsync(ct).ConfigureAwait(false);
                }

                await _semaphore.WaitAsync(ct).ConfigureAwait(false);
                acquired = true;

                await TrySendInitialLockerStatusToUi(isLockerOperational).ConfigureAwait(false);    // one-off

                _pendingIsLockerOperational = isLockerOperational;
                foreach (var ds in updates)
                {
                    if (!failedStatuses)
                    {
                        // Greška zatvorenih vrata koja čeka slanje (npr. „N“: vrata se nisu otvorila) ostaje dok je server ne primi:
                        // zatvorena vrata ne dokazuju popravku — isto kao za grešku koju je server već primio (niže).
                        if (_pendingDoorStatuses.TryGetValue(ds.DoorNumber, out var pendingError) &&
                            pendingError.IsInFailedState && pendingError.Status?.IsClosed == true && ds.Status?.IsClosed == true)
                        {
                            continue;
                        }

                        if (_serverDoorStatuses.TryGetValue(ds.DoorNumber, out var ss) &&
                            ss.Status == ds.Status && ss.IsInFailedState == ds.IsInFailedState)
                        {
                            // if there was a pending state, remove it because the state is back to being the same as on the server
                            _pendingDoorStatuses.Remove(ds.DoorNumber);
                            continue;
                        }

                        // Never clear a failed state that exists on the server unless explicitly requested via SetFailedStatuses
                        if (_serverDoorStatuses.TryGetValue(ds.DoorNumber, out var doorStatus) &&
                            doorStatus.IsInFailedState && !ds.IsInFailedState)
                        {
                            // Check if error has been resolved (door is now closed)
                            if (ds.Status?.IsClosed == true && (doorStatus.Status?.IsOpen == true || doorStatus.Status == null))
                            {
                                // Door was open or controller was unavailable, but is now closed - allow clearing the error automatically
                                _logger?.LogInformation($"{nameof(UpdateStatuses)}: vrata {ds.DoorNumber} su bila u failed state-u (status: {doorStatus.Status.Format()}), ali su sada zatvorena. Greška se automatski briše.");
                                // Don't continue - allow the update to proceed and clear the error
                            }
                            else
                            {
                                // Server has failed state for other reasons (e.g., failed to open) - preserve error state
                                _logger?.LogDebug($"{nameof(UpdateStatuses)}: vrata {ds.DoorNumber} imaju IsInFailedState=true na serveru, ignorisanje novog statusa da bi se sačuvao error state.");
                                _pendingDoorStatuses.Remove(ds.DoorNumber); // Remove any pending status that would clear the error
                                continue;
                            }
                        }

                        if (_pendingDoorStatuses.TryGetValue(ds.DoorNumber, out var existing)) {
                            if (existing.Status == ds.Status && existing.IsInFailedState == ds.IsInFailedState) {
                                continue;
                            }
                        }
						// Add a new or replace an existing status, but never update the statuses of closed doors in failed state.
						// These statuses will be cleared only by a technician. A technician will manually remove the status file and
						// restart the app, thus re-sending all (regular and repaired) statuses to the server.
						if (existing == null || !existing.IsInFailedState || ds.Status != DoorStatusCode.Closed) {
						    _pendingDoorStatuses[ds.DoorNumber] = ds;
                        }
					}
                    else {
						_pendingDoorStatuses[ds.DoorNumber] = new DoorWatcher.DoorStatus(ds.DoorNumber, ds.Status, isInFailedState: true);
					}
				}
                await WriteStatuses(ct).ConfigureAwait(false);
            }
            catch (Exception ex) {
                _logger?.LogError($"{nameof(LockerStatusUpdater)}: {nameof(UpdateStatuses)}: exception: {ex.Message}");
            }
            finally {
                if (acquired) {
                    _semaphore.Release();
                }
            }
        }

        private async ValueTask TrySendInitialLockerStatusToUi(bool? isLockerOperational)
        {
            // Prvo očitavanje vrata javlja aplikaciji i kad posle restarta u fajlu čeka status koji server nije primio: petlja
            // aplikaciji javlja samo promene posle ovog javljanja (SendLockerStatusIfChanged), a ono stiže tek kad je
            // aplikacija spremna (DoorWatcher se pokreće posle WireWorkflowMachine).
            if (_sendInitialLockerStatusUpdateToUi && isLockerOperational.HasValue)
            {
                try
                {
                    var handler = LockerStatusChanged;
                    if (handler != null) {
                        _logger?.LogInformation($"{nameof(LockerStatusUpdater)}: {nameof(TrySendInitialLockerStatusToUi)}: slanje inicijalnog statusa paketomata: [{isLockerOperational}]");
                        _appIsLockerOperational = isLockerOperational.Value;
                        _sendInitialLockerStatusUpdateToUi = false; // only send once
                        await handler(isLockerOperational.Value).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) {
                    _logger?.LogError($"{nameof(LockerStatusUpdater)}: {nameof(TrySendInitialLockerStatusToUi)}: {nameof(LockerStatusChanged)} handler exception: {ex.Message}");
                }
            }
        }

        private async Task ProcessPendingStatusesLoop(int numberOfDoors, CancellationToken ct)
        {
            _logger?.LogInformation($"{nameof(LockerStatusUpdater)}: starting processing loop.");
            try 
            { 
                await TryInitializeStatuses(numberOfDoors, ct).ConfigureAwait(false);
                _serviceClient.OnHeartbeatSent += OnHeartbeatSent;

				string messageLogged = "";
				bool paketomatJeVanFunkcijeLogged = false;
				while (!ct.IsCancellationRequested)
                {

					string message = $"{nameof(ProcessPendingStatusesLoop)}: _serverIsLockerOperational={_serverIsLockerOperational}, _deferLockerOfflineStatus={_deferLockerOfflineStatus}, _pendingDoorStatuses.Count={_pendingDoorStatuses.Count}";
					if(message.CompareTo(messageLogged) != 0)
					{
						_logger?.LogDebug(message);
						messageLogged = message;
					}

					// Locker status must be sent first, otherwise door status updates might fail (if the server doesn't know yet
					// that the locker is operational, or there was a restart so the server has put us offline)
					await TrySendLastKnownLockerStatus().ConfigureAwait(false); // one-off, or manual
                    await SendLockerStatusIfChanged().ConfigureAwait(false);

                    // Ako je paketomat van funkcije i NIJE u toku odloženo slanje (tj. već smo poslali PrijavaGreskePaketomata),
                    // onda ne šaljemo statuse vrata dok paketomat ponovo ne postane operativan
                    if (_serverIsLockerOperational != true && !_deferLockerOfflineStatus) {
						if (!paketomatJeVanFunkcijeLogged) {
							_logger?.LogDebug($"{nameof(ProcessPendingStatusesLoop)}: paketomat je van funkcije, slanje statusa vrata se odlaže dok paketomat ponovo ne postane operativan. Pending statusa: {_pendingDoorStatuses.Count}");
							paketomatJeVanFunkcijeLogged = true;
						}							
						await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
                        continue;
                    }
					else
						paketomatJeVanFunkcijeLogged = false;

					// Ako je paketomat upravo ponovo postao operativan u ovoj iteraciji, preskoci slanje statusa vrata
					// Da bi se prvo poslalo PonistavanjeGreskePaketomata, pa tek onda statusi vrata u sledecoj iteraciji
					if (_skipDoorStatusesThisIteration) {
                        _logger?.LogDebug($"{nameof(ProcessPendingStatusesLoop)}: preskakanje slanja statusa vrata u ovoj iteraciji (paketomat je upravo ponovo postao operativan)");
                        _skipDoorStatusesThisIteration = false;
                        await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
                        continue;
                    }

					DoorWatcher.DoorStatus[]? batchStatuses;
                    await _semaphore.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        batchStatuses = _pendingDoorStatuses.Select(s => s.Value).ToArray();
                    }
                    finally {
                        _semaphore.Release();
                    }
                    if (batchStatuses.Length == 0) {
                        // Ako nema pending statusa vrata, ali ima odložen status paketomata, pošalji ga
                        await TrySendDeferredLockerOfflineStatus().ConfigureAwait(false);
                        await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
                        continue;
                    }
                    _logger?.LogDebug($"{nameof(ProcessPendingStatusesLoop)}: izdvojeno {batchStatuses.Length} statusa za slanje.");
                    _logger?.LogDebug($"{nameof(ProcessPendingStatusesLoop)}: statusi za slanje: {LockerAndDoorsStatus.FormatDoorStatuses(batchStatuses)}.");

					foreach (var status in batchStatuses)
                    {
                        bool sent = await SendDoorStatus(status).ConfigureAwait(false);
                        if (sent)
                        {
                            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
                            try
                            {
                                var previousServerStatus = _serverDoorStatuses.GetValueOrDefault(status.DoorNumber);
                                _serverDoorStatuses[status.DoorNumber] = status;
                                if (_pendingDoorStatuses.TryGetValue(status.DoorNumber, out var s) && s.Status == status.Status) {
                                    _pendingDoorStatuses.Remove(status.DoorNumber);
                                }
                                else if (!_pendingDoorStatuses.ContainsKey(status.DoorNumber) &&
                                         previousServerStatus != null && previousServerStatus.Status != status.Status) {
                                    _logger?.LogInformation($"{nameof(ProcessPendingStatusesLoop)}: Korekcija za vrata {status.DoorNumber} — poslat status {status.Status.Format()} ali prethodni serverStatus je bio {previousServerStatus.Status.Format()}. Dodajem korekciju u pending.");
                                    _pendingDoorStatuses[status.DoorNumber] = previousServerStatus;
                                }
                                await WriteStatuses(ct).ConfigureAwait(false);
                            }
                            finally {
                                _semaphore.Release();
                            }
                        }
                    }

                    // Ako je bilo odloženo slanje PrijavaGreskePaketomata, sada ga pošalji
                    // (nakon što su svi statusi vrata poslati)
                    await TrySendDeferredLockerOfflineStatus().ConfigureAwait(false);

                    await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
				}
            }
            catch (Exception ex) {
                _logger?.LogError($"{nameof(ProcessPendingStatusesLoop)}: exception: {ex.Message}");
            }
        }

        private async ValueTask TrySendLastKnownLockerStatus()
        {
            if (_sendLastKnownLockerStatusToServer)
            {
                if (!_serverIsLockerOperational.HasValue) {
                    _logger?.LogWarning($"{nameof(ProcessPendingStatusesLoop)}: paketomat nema poznat status, ne moze se poslati poslednji poznati status serveru.");
                    return;
                }
                _logger?.LogInformation($"{nameof(ProcessPendingStatusesLoop)}: slanje poslednjeg poznatog statusa paketomata serveru.");
                await SendLockerStatus(_serverIsLockerOperational.Value).ConfigureAwait(false);
                _sendLastKnownLockerStatusToServer = false; // only send once
            }
        }

        private async ValueTask TrySendDeferredLockerOfflineStatus()
        {
            if (!_deferLockerOfflineStatus) {
                return;
            }

            // Proveri da li ima još pending statusa vrata
            bool hasPendingDoorStatuses;
            bool? currentIsLockerOperational;
            await _semaphore.WaitAsync(_cts.Token).ConfigureAwait(false);
            try {
                hasPendingDoorStatuses = _pendingDoorStatuses.Count > 0;
                currentIsLockerOperational = _pendingIsLockerOperational ?? _serverIsLockerOperational;
            }
            finally {
                _semaphore.Release();
            }

            // Paketomat je ponovo operativan pre nego što je odložena prijava poslata: zastareli „van funkcije“ se ne šalje.
            // Inače bi, posle slanja poslednjeg poznatog statusa (heartbeat) koje briše pending, server ostao na false.
            if (currentIsLockerOperational != false) {
                _logger?.LogInformation($"{nameof(TrySendDeferredLockerOfflineStatus)}: paketomat je ponovo operativan, odloženi PrijavaGreskePaketomata se ne šalje.");
                _deferLockerOfflineStatus = false;
                return;
            }

            if (hasPendingDoorStatuses) {
                _logger?.LogDebug($"{nameof(TrySendDeferredLockerOfflineStatus)}: još uvek ima pending statusa vrata, odlaganje slanja PrijavaGreskePaketomata.");
                return;
            }

            // Svi statusi vrata su poslati, sada pošalji odloženi PrijavaGreskePaketomata
            _logger?.LogInformation($"{nameof(TrySendDeferredLockerOfflineStatus)}: svi statusi vrata su poslati, slanje odloženog PrijavaGreskePaketomata.");
            bool sent = await SendLockerStatus(false).ConfigureAwait(false);
            if (sent) {
                _deferLockerOfflineStatus = false;
            }
        }

        private async Task<bool> SendDoorStatus(DoorWatcher.DoorStatus ds)
        {
            try
            {
                bool confirmed = false;
                string? errorMessage = null;

                var status = ds.Format();
                _logger?.LogWarning($"{nameof(SendDoorStatus)}: status vrata {ds.DoorNumber} je promenjen u [{status}]. Status ce biti poslat serveru.");
                if (ds.IsInFailedState) {
					// PrijavaGreskePozicije - vrata su u failed state (npr. failed to open/close)
					(confirmed, errorMessage) = await _serviceClient.ReportDoorErrorState(ds.DoorNumber, ds.Status.ToPrijavaGreskePozicije(), _cts.Token).ConfigureAwait(false);
				}
                else {
                    // IsInFailedState = false - proveriti status vrata
                    if (ds.Status?.IsClosed == true) {
                        // Vrata su zatvorena - ponistavanje greske
                        (confirmed, errorMessage) = await _serviceClient.ClearDoorErrorState(ds.DoorNumber, _cts.Token).ConfigureAwait(false);
                    }
                    else if (ds.Status?.IsOpen == true) {
                        // Vrata su otvorena - prijavi kao gresku "O"
                        (confirmed, errorMessage) = await _serviceClient.ReportDoorErrorState(ds.DoorNumber, PrijavaGreskePozicije.DoorOpen, _cts.Token).ConfigureAwait(false);
                    }
                    else {
                        // Status je null - kontroler nedostupan, prijavi kao gresku "K"
                        (confirmed, errorMessage) = await _serviceClient.ReportDoorErrorState(ds.DoorNumber, PrijavaGreskePozicije.DoorStatusUnknown, _cts.Token).ConfigureAwait(false);
                    }
                }
                if (!confirmed)
                {
                    errorMessage ??= $"Azuriranje statusa ({ds.Status}) vrata {ds.DoorNumber} na serveru nije uspelo.";
                    _logger?.LogError($"{nameof(SendDoorStatus)}: {errorMessage}");
                }
                return confirmed; 
            }
            catch (Exception ex)
            {
                _logger?.LogError($"{nameof(SendDoorStatus)}: exception: {ex.Message}");
                return false;
            }
        }

        private async ValueTask SendLockerStatusIfChanged()
        {
            try
            {
                bool? pendingIsLockerOperational;
                bool? previousLockerOperational;
                bool? currentIsLockerOperational;
                bool notifyApp;
                bool sendToServer;
                await _semaphore.WaitAsync(_cts.Token).ConfigureAwait(false);
                try {
                    previousLockerOperational = _serverIsLockerOperational;
                    pendingIsLockerOperational = _pendingIsLockerOperational;
                    // Poslednje očitavanje: pending dok ga server ne primi, posle toga (pending = null) serverski status.
                    currentIsLockerOperational = _pendingIsLockerOperational ?? _serverIsLockerOperational;
                    // Aplikaciji se javlja samo promena, ne svaki ponovljeni pokušaj slanja dok server ne primi status:
                    // OnLockerStatusChanged(true) radi Reset, pa bi bez interneta kiosk svakog prolaza vraćao na Start.
                    // Pre prvog javljanja (TrySendInitialLockerStatusToUi) aplikacija možda još nije spremna (handler nije
                    // dodeljen, Start nema OnEntry), pa se ne javlja ništa — to bi javljanje bilo izgubljeno.
                    // Poredi se sa javljenim aplikaciji, ne sa serverskim: i povratak na status koji server već zna (server
                    // nije primio međustanje) je promena za aplikaciju.
                    notifyApp = _appIsLockerOperational.HasValue && currentIsLockerOperational.HasValue && currentIsLockerOperational != _appIsLockerOperational;
                    if (notifyApp) {
                        _logger?.LogWarning($"{nameof(SendLockerStatusIfChanged)}: status paketomata je promenjen. Paketomat je u funkciji = [{currentIsLockerOperational}]");
                        _appIsLockerOperational = currentIsLockerOperational;
                    }
                    sendToServer = _pendingIsLockerOperational.HasValue && _pendingIsLockerOperational != _serverIsLockerOperational;
                    if (!notifyApp && !sendToServer) {
                        return;
                    }
                }
                finally {
                    _semaphore.Release();
                }

                // Ukljucujemo i prelaz null -> false (prvi start sa offline kontrolerom),
                // kako bi se prvo poslali statusi vrata (K), pa tek onda PrijavaGreskePaketomata.
                bool isGoingOffline = previousLockerOperational != false && pendingIsLockerOperational == false;
                bool isGoingOnline = previousLockerOperational == false && pendingIsLockerOperational == true;

                // Regardless of whether we succeed in sending the current state to the server, we need to send
                // the status change to the app now, so that it can update the UI.
                if (notifyApp) {
                    try
                    {
                        var handler = LockerStatusChanged;
                        if (handler != null) {
                            await handler(currentIsLockerOperational!.Value).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) {
                        _logger?.LogError($"{nameof(SendLockerStatusIfChanged)}: {nameof(LockerStatusChanged)} handler exception: {ex.Message}");
                    }
                }

                if (!sendToServer) {
                    return;
                }

                // Ako paketomat prelazi iz ONLINE u OFFLINE, odloziti slanje PrijavaGreskePaketomata
                // dok se prvo ne posalju svi statusi vrata
                if (isGoingOffline) {
                    _logger?.LogInformation($"{nameof(SendLockerStatusIfChanged)}: paketomat prelazi u offline. Slanje PrijavaGreskePaketomata ce biti odlozeno dok se ne posalju statusi vrata.");
                    _deferLockerOfflineStatus = true;
                    return;
                }

                bool sent = await SendLockerStatus(pendingIsLockerOperational.Value).ConfigureAwait(false);

                // Ako je paketomat bio offline i sada je postao online, preskoci slanje statusa vrata u ovoj iteraciji
                // da bi se prvo poslalo PonistavanjeGreskePaketomata, pa tek onda statusi vrata u sledecoj iteraciji
                if (sent && isGoingOnline) {
                    _logger?.LogInformation($"{nameof(SendLockerStatusIfChanged)}: paketomat je ponovo operativan. Slanje statusa vrata ce biti odlozeno za sledecu iteraciju.");
                    _skipDoorStatusesThisIteration = true;
                }
            }
            catch (Exception ex) {
                _logger?.LogError($"{nameof(SendLockerStatusIfChanged)}: exception: {ex.Message}");
            }
        }

        private async Task<bool> SendLockerStatus(bool isLockerOperational)
        {
            try
            {
                bool sent = false;
                string? errorMessage = null;

                if (isLockerOperational != true) {
                    (sent, errorMessage) = await _serviceClient.ReportLockerErrorState(VrstaGreskePaketomat.C, _cts.Token).ConfigureAwait(false); // TODO
                }
                else {
                    (sent, errorMessage) = await _serviceClient.ClearLockerErrorState(_cts.Token).ConfigureAwait(false);    // TODO
                }
                if (sent) 
                {
                    // Only now it's OK to set the new state, after it has been confirmed with the server
                    await _semaphore.WaitAsync(_cts.Token).ConfigureAwait(false);
                    try {
                        _serverIsLockerOperational = isLockerOperational;
                        if (isLockerOperational == _pendingIsLockerOperational) {
                            _pendingIsLockerOperational = null; // reset pending status after a successful sending
                        }
                    }
                    finally {
                        _semaphore.Release();
                    }
                }
                else {
                    errorMessage ??= $"Azuriranje statusa paketomata na serveru nije uspelo.";
                    _logger?.LogError($"{nameof(SendLockerStatus)}: {errorMessage}");
                }
                return sent;
            }
            catch (Exception ex) {
                _logger?.LogError($"{nameof(SendLockerStatus)}: exception: {ex.Message}");
                return false;
            }
        }

        private void OnHeartbeatSent()
        {
            var now = DateTime.UtcNow;
            if (_lastHeartbeatTime.HasValue && (now - _lastHeartbeatTime.Value) >= TimeSpan.FromSeconds(60)) {
                _logger?.LogDebug($"{nameof(OnHeartbeatSent)}: heartbeat je poslat nakon duzeg perioda neaktivnosti ({(now - _lastHeartbeatTime.Value).TotalSeconds:F1} sekundi). Ponovo ce serveru biti poslat poslednji poznati status paketomata.");
                _sendLastKnownLockerStatusToServer = true; // send again because the server has probably put us offline
            }
            _lastHeartbeatTime = now;
        }

        private async ValueTask WriteStatuses(CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrEmpty(_configuration.ServerStatusFilename)) {
                    _logger?.LogError($"{nameof(WriteStatuses)}: ime fajla za snimanje statusa nije navedeno u konfiguraciji (ServerStatusFilename je null ili prazan).");
                    return;
                }
                var filename = _configuration.ServerStatusFilename;
                var tempFilename = filename + ".tmp";   // isti folder => isti volumen, atomic replace
                _logger?.LogInformation($"{nameof(WriteStatuses)}: snimanje statusa paketomata i vrata serveru u fajl '{Path.GetFullPath(filename)}'");
                var pendingDoorStatuses = _pendingDoorStatuses.Values.ToList();
                var serverDoorStatuses = _serverDoorStatuses.Values.ToList();
                var status = new LockerAndDoorsStatus(_pendingIsLockerOperational, pendingDoorStatuses, _serverIsLockerOperational, serverDoorStatuses);
                // Atomic write: prvo u privremeni fajl, pa zamena (da agent vidi kompletan fajl).
                // Zaostao .tmp (pad programa usred upisa) prepisuje sledeci upis.
                await using (var stream = new FileStream(tempFilename, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true)) {
                    await JsonSerializer.SerializeAsync(stream, status, __jsonOptions, cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);    // da nestanak struje posle zamene ne ostavi prazan fajl
                }
                if (await TryReplaceStatusFile(tempFilename, filename).ConfigureAwait(false)) {
                    _logger?.LogDebug($"{nameof(WriteStatuses)}: snimljeni statusi: {status.SerializeStatusCompact()}");
                }
            }
            catch (Exception ex) {
                var filename = string.IsNullOrEmpty(_configuration.ServerStatusFilename) ? "<nije navedeno>" : _configuration.ServerStatusFilename;
                _logger?.LogError($"{nameof(WriteStatuses)}: snimanje statusa paketomata i vrata na serveru u fajl '{filename}' nije uspelo: {ex.Message}");
            }
        }

        // File.Replace, da bi agent mogao da ga pročita (FileShare.Delete),
        // Replace trazi da cilj postoji, pa prvi upis mora Move.
        // Bez CancellationToken-a (najvise 200 ms cekanja): prekid posle neuspelog Replace-a mogao bi da ostavi fajl bez cilja.
        private async ValueTask<bool> TryReplaceStatusFile(string tempFilename, string filename)
        {
            for (int attempt = 1; ; attempt++) {
                try {
                    if (File.Exists(filename)) {
                        File.Replace(tempFilename, filename, destinationBackupFileName: null);
                    }
                    else {
                        File.Move(tempFilename, filename);
                    }
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    if (attempt >= ReplaceStatusFileAttempts) {
                        // Replace moze da obrise cilj, a da ne preimenuje .tmp (ERROR_UNABLE_TO_MOVE_REPLACEMENT) - ne ostavljati fajl bez cilja
                        if (!File.Exists(filename) && File.Exists(tempFilename)) {
                            try {
                                File.Move(tempFilename, filename);
                                return true;
                            }
                            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException) {
                                ex = moveEx;
                            }
                        }
                        _logger?.LogWarning($"{nameof(WriteStatuses)}: zamena fajla '{filename}' nije uspela ni posle {attempt} pokusaja, statusi ce biti snimljeni pri sledecem upisu: {ex.Message}");
                        return false;
                    }
                    await Task.Delay(__replaceStatusFileRetryDelay).ConfigureAwait(false);
                }
            }
        }

        private async ValueTask TryInitializeStatuses(int numberOfDoors, CancellationToken cancellationToken = default)
        {
            bool acquired = false;
            try
            {
                _logger?.LogInformation($"{nameof(LockerStatusUpdater)}: {nameof(TryInitializeStatuses)}: inicijalizacija...");
                await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;

                if (_configuration == null) {
                    throw new ArgumentNullException(nameof(_configuration), $"{nameof(LockerStatusUpdaterConfiguration)} cannot be null.");
                }
                if (string.IsNullOrEmpty(_configuration.ServerStatusFilename)) {
                    throw new ArgumentException("Ime fajla sa statusima mora biti navedeno.", nameof(_configuration.ServerStatusFilename));
                }
                _logger?.LogInformation($"{nameof(LockerStatusUpdater)}: {nameof(TryInitializeStatuses)}: citanje statusa vrata na serveru iz fajla {Path.GetFullPath(_configuration.ServerStatusFilename)}...");
                if (!File.Exists(_configuration.ServerStatusFilename)) {
                    _logger?.LogWarning($"{nameof(TryInitializeStatuses)}: fajl '{_configuration.ServerStatusFilename}' sa statusima vrata na serveru nije pronadjen.");
                    return;
                }
                using var stream = new FileStream(_configuration.ServerStatusFilename, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                var savedStatus = await JsonSerializer.DeserializeAsync<LockerAndDoorsStatus>(stream, __jsonOptions, cancellationToken).ConfigureAwait(false);
                if (savedStatus == null) {
                    _logger?.LogWarning($"{nameof(TryInitializeStatuses)}: fajl '{_configuration.ServerStatusFilename}' sa statusima vrata na serveru je prazan ili ne sadrzi validan JSON.");
                    return;
                }
                _logger?.LogDebug($"{nameof(TryInitializeStatuses)}: ucitani statusi: {savedStatus.SerializeStatusCompact()}");

                _pendingIsLockerOperational = savedStatus.PendingIsLockerOperational;
                // Remove old statuses that are not in the current range of doors
                if (savedStatus.PendingDoorStatuses?.Count() > 0) {
                    foreach (var doorStatus in savedStatus.PendingDoorStatuses.Where(ds => ds.DoorNumber > 0 && ds.DoorNumber <= numberOfDoors)) {   // door numbers are 1-based
                        _pendingDoorStatuses[doorStatus.DoorNumber] = doorStatus;
                    }
                }
                _serverIsLockerOperational = savedStatus.ServerIsLockerOperational;
                _sendLastKnownLockerStatusToServer = _serverIsLockerOperational.HasValue; // only send if we have a known state
                if (savedStatus.ServerDoorStatuses?.Count() > 0) {
                    foreach (var doorStatus in savedStatus.ServerDoorStatuses.Where(ds => ds.DoorNumber > 0 && ds.DoorNumber <= numberOfDoors)) {    // door numbers are 1-based
                        _serverDoorStatuses[doorStatus.DoorNumber] = doorStatus;
                    }
                }
            }
            catch (Exception ex) {
                _logger?.LogError($"{nameof(LockerStatusUpdater)}: {nameof(TryInitializeStatuses)}: Failed to read pending statuses from file '{_configuration.ServerStatusFilename}': {ex.Message}");
            }
            finally {
                _ctsInitializationCompleted.TrySetResult(true);    // don't care about the actual outcome, just signal that initialization is done
                _logger?.LogInformation($"{nameof(LockerStatusUpdater)}: {nameof(TryInitializeStatuses)}: inicijalizacija zavrsena.");
                if (acquired) {
                    _semaphore.Release();
                }
            }
        }
    }
}
