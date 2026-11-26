using System.IO;
using Microsoft.Extensions.Logging;
using TopPos.Agent.Contracts;
using TopPos.Platform;
using TopPos.Kiosk.Workflow;

namespace TopPos.Kiosk.AgentCommunication
{
	/// <summary>
	/// Jedna veza sa Agentom: provera identiteta, handshake, prijem zahteva i slanje odgovora/događaja kroz jedan pisac
	/// (<see cref="AgentOutboundQueue"/>). Transport bez poslovne logike: statusne komande čitaju <see cref="KioskStatusSource"/>,
	/// a komande koje menjaju stanje kioska izvršava <see cref="AgentCommandDispatcher"/>.
	/// v4: veza je <see cref="IAgentChannelConnection"/> (named pipe ili Unix socket); identitet klijenta utvrđuje transport.
	/// </summary>
	internal sealed class AgentPipeConnection
	{
		private static readonly string[] __statusCommands = [AgentCommands.GetStatus, AgentCommands.SubscribeStatus];
		private const int MaxRequestIdLength = 64;

		private readonly IAgentChannelConnection _channel;
		private readonly Stream _pipe;
		private readonly int _connectionId;
		private readonly AgentPipeServerOptions _options;
		private readonly KioskStatusSource _statusSource;
		private readonly AgentCommandDispatcher? _commands;
		private readonly ILogger? _logger;
		private readonly string _allowedClient;
		// Klijent proveren u handshake-u (za log komandi).
		private AgentPeer? _peer;
		private readonly AgentOutboundQueue _outbound;
		private readonly CancellationTokenSource _cts;
		private readonly object _subscriptionLock = new();
		private IDisposable? _subscription;
		private string? _subscriptionId;
		private Task? _writer;
		private readonly object _closeLock = new();
		private Task? _close;

		public AgentPipeConnection(IAgentChannelConnection channel, int connectionId, AgentPipeServerOptions options, KioskStatusSource statusSource,
									AgentCommandDispatcher? commands, ILogger? logger, CancellationToken serverToken, string allowedClient)
		{
			_channel = channel;
			_pipe = channel.Stream;
			_connectionId = connectionId;
			_options = options;
			_statusSource = statusSource;
			_commands = commands;
			_logger = logger;
			_allowedClient = allowedClient;
			_outbound = new AgentOutboundQueue(options.MaxPendingResponses);
			_cts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
		}

		public int ConnectionId => _connectionId;

		/// <summary>ID aktivne pretplate; <c>null</c> dok Agent nije poslao SubscribeStatus.</summary>
		public string? SubscriptionId {
			get { lock (_subscriptionLock) { return _subscriptionId; } }
		}

		/// <summary>Heartbeat za pretplaćenu vezu (poziva server posle potvrde UI niti).</summary>
		public void EnqueueHeartbeat(KioskStatusSnapshot status)
		{
			var subscriptionId = SubscriptionId;
			if (subscriptionId != null) {
				_outbound.EnqueueHeartbeat(status, subscriptionId);
			}
		}

		public async Task RunAsync()
		{
			var token = _cts.Token;
			try {
				if (!await HandshakeAsync(token).ConfigureAwait(false)) {
					return;
				}
				_writer = Task.Run(() => WriteLoopAsync(token), CancellationToken.None);
				await ReadLoopAsync(token).ConfigureAwait(false);
			}
			catch (Exception) when (token.IsCancellationRequested) {
				// gašenje servera ili je pisac zatvorio vezu (razlog je već u logu)
			}
			catch (Exception ex) when (ex is IOException or AgentProtocolException or ObjectDisposedException or InvalidOperationException) {
				_logger?.LogWarning("Agent pipe [{ConnectionId}]: veza je prekinuta: {Error}", _connectionId, ex.Message);
			}
			catch (Exception ex) {
				_logger?.LogError(ex, "Agent pipe [{ConnectionId}]: neočekivana greška veze.", _connectionId);
			}
			finally {
				await CloseAsync(TimeSpan.Zero).ConfigureAwait(false);
				// Čitalac (ova metoda) i pisac su završili — tek sada se oslobađa linked CTS, čime se uklanja i njegova
				// registracija na tokenu servera (bez Dispose bi svaka veza ostala registrovana do gašenja servera).
				if (_writer != null) {
					await _writer.ConfigureAwait(false);
				}
				_cts.Dispose();
			}
		}

		/// <summary>
		/// Zatvara vezu: odjavljuje pretplatu, pušta pisca da pošalje ono što je već u redu (najviše <paramref name="drainTimeout"/>),
		/// pa prekida I/O i čeka pisca. Zatvaranje se radi jednom; svaki sledeći poziv čeka isto zatvaranje.
		/// </summary>
		public Task CloseAsync(TimeSpan drainTimeout)
		{
			lock (_closeLock) {
				return _close ??= CloseCoreAsync(drainTimeout);
			}
		}

		private async Task CloseCoreAsync(TimeSpan drainTimeout)
		{
			lock (_subscriptionLock) {
				_subscription?.Dispose();
				_subscription = null;
			}
			_outbound.Complete();
			var writer = _writer;
			if (writer != null && drainTimeout > TimeSpan.Zero) {
				await Task.WhenAny(writer, Task.Delay(drainTimeout)).ConfigureAwait(false);
			}
			try {
				_cts.Cancel();
			}
			catch (ObjectDisposedException) { }
			try {
				await _channel.DisposeAsync().ConfigureAwait(false);
			}
			catch (Exception ex) {
				_logger?.LogDebug("Agent pipe [{ConnectionId}]: zatvaranje veze: {Error}", _connectionId, ex.Message);
			}
			if (writer != null) {
				await writer.ConfigureAwait(false);   // izuzeci pisca su obrađeni u WriteLoopAsync
			}
		}

		// ClientHello (sirov, ograničen frame) → provera identiteta → parsiranje → Hello. Vraća false ako veza ne prolazi.
		private async Task<bool> HandshakeAsync(CancellationToken token)
		{
			byte[]? helloBytes;
			using (var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(token)) {
				handshakeCts.CancelAfter(_options.HandshakeTimeout);
				try {
					helloBytes = await AgentFrameCodec.ReadFrameAsync(_pipe, AgentProtocol.MaxClientHelloBytes, handshakeCts.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (!token.IsCancellationRequested) {
					_logger?.LogWarning("Agent pipe [{ConnectionId}]: klijent nije poslao ClientHello u roku od {Timeout}; veza se zatvara.",
										_connectionId, _options.HandshakeTimeout);
					return false;
				}
			}
			if (helloBytes == null) {
				_logger?.LogInformation("Agent pipe [{ConnectionId}]: klijent je zatvorio vezu pre ClientHello.", _connectionId);
				return false;
			}

			// Identitet pre tumačenja sadržaja: DACL (Windows), odnosno prava na socket-u (Linux) su prva linija, ovo druga
			// (plan §4, zahtev 3). v4: utvrđuje ga transport (SID ili SO_PEERCRED uid).
			AgentChannelClient client;
			try {
				client = _channel.GetClient();
			}
			catch (Exception ex) {
				_logger?.LogWarning("Agent pipe [{ConnectionId}]: identitet klijenta nije moguće utvrditi; veza se odbija: {Error}",
									_connectionId, ex.Message);
				return false;
			}
			var clientSid = client.Identity;
			var processId = client.ProcessId;
			if (!client.IsAllowed) {
				_logger?.LogWarning("Agent pipe [{ConnectionId}]: odbijen klijent {ClientSid} (PID {ProcessId}); dozvoljen je samo {AllowedSid}.",
									_connectionId, clientSid, processId, _allowedClient);
				return false;
			}

			var (type, protocolVersion) = AgentJson.ReadEnvelope(helloBytes);
			if (type != AgentMessageTypes.ClientHello) {
				throw new AgentProtocolException($"Prva poruka mora biti {AgentMessageTypes.ClientHello}, a stigla je {type}.");
			}
			var clientHello = AgentJson.Deserialize<ClientHello>(helloBytes);
			_peer = new AgentPeer(_connectionId, clientSid, processId);
			_logger?.LogInformation("Agent pipe [{ConnectionId}]: klijent {ClientSid} (PID {ProcessId}, Agent {AgentVersion}, protokol {ProtocolVersion}) je proveren.",
									_connectionId, clientSid, processId, Sanitize(clientHello.AgentVersion), protocolVersion);

			var hello = new ServerHello {
				LockerId = _statusSource.LockerId,
				ClientInstanceId = _statusSource.ClientInstanceId,
				ApplicationVersion = _options.ApplicationVersion,
				HeartbeatIntervalMs = (int) _options.HeartbeatInterval.TotalMilliseconds,
				SupportedCommands = [.. __statusCommands, .. _commands?.Commands ?? []],
			};
			await WriteFrameAsync(AgentJson.Serialize(hello), token).ConfigureAwait(false);
			if (protocolVersion != AgentProtocol.Version) {
				_logger?.LogWarning("Agent pipe [{ConnectionId}]: klijent koristi protokol {ClientVersion}, kiosk {ServerVersion}; zahtevi druge verzije dobijaju {Code}.",
									_connectionId, protocolVersion, AgentProtocol.Version, AgentResultCodes.UnsupportedVersion);
			}
			return true;
		}

		private async Task ReadLoopAsync(CancellationToken token)
		{
			while (!token.IsCancellationRequested) {
				var body = await AgentFrameCodec.ReadFrameAsync(_pipe, _options.MaxMessageBytes, token, _options.HandshakeTimeout).ConfigureAwait(false);
				if (body == null) {
					_logger?.LogInformation("Agent pipe [{ConnectionId}]: klijent je zatvorio vezu.", _connectionId);
					return;
				}
				var (type, _) = AgentJson.ReadEnvelope(body);
				if (type != AgentMessageTypes.Request) {
					throw new AgentProtocolException($"Klijent sme da šalje samo {AgentMessageTypes.Request}, a stigla je {type}.");
				}
				var request = AgentJson.Deserialize<AgentRequest>(body);
				if (!HandleRequest(request)) {
					_logger?.LogWarning("Agent pipe [{ConnectionId}]: previše neposlatih odgovora ({Max}); veza se zatvara.", _connectionId, _options.MaxPendingResponses);
					return;
				}
			}
		}

		// Vraća false ako odgovor ne može u red (prekoračen limit) — veza se tada zatvara.
		private bool HandleRequest(AgentRequest request)
		{
			string? rejection = ValidateRequest(request, out string? message);
			if (rejection != null) {
				_logger?.LogWarning("Agent pipe [{ConnectionId}]: zahtev {RequestId} ({Command}) odbijen: {ResultCode} — {Message}",
									_connectionId, Sanitize(request.RequestId), Sanitize(request.Command), rejection, message);
				return _outbound.TryEnqueueResponse(NewResponse(request, rejection, message));
			}

			switch (request.Command) {
				case AgentCommands.GetStatus: {
					var response = NewResponse(request, AgentResultCodes.Completed, null);
					response.Status = _statusSource.ToContract(_statusSource.Current);
					_logger?.LogDebug("Agent pipe [{ConnectionId}]: GetStatus {RequestId} → revizija {Revision}.", _connectionId, request.RequestId, response.Status.StatusRevision);
					return _outbound.TryEnqueueResponse(response);
				}
				case AgentCommands.SubscribeStatus:
					return Subscribe(request);
				default: {
					if (_commands?.Dispatch(request, _peer!) is { } result) {
						return EnqueueCommandResult(request, result);
					}
					// OpenDoor je u ugovoru, ali ga je zamenila servisna sesija (OpenDoors).
					var text = request.Command is AgentCommands.OpenDoor
								? "Komanda nije podržana u ovoj verziji kioska."
								: "Nepoznata komanda.";
					_logger?.LogWarning("Agent pipe [{ConnectionId}]: zahtev {RequestId}: komanda {Command} nije podržana.",
										_connectionId, Sanitize(request.RequestId), Sanitize(request.Command));
					return _outbound.TryEnqueueResponse(NewResponse(request, AgentResultCodes.UnsupportedCommand, text));
				}
			}
		}

		// Odgovor komande; radnja posle odgovora (npr. gašenje) čeka ishod upisa i izvršava se van niti veze. I kad odgovor
		// ne može u red, radnja se izvršava (sa „nije upisano“): odluka je doneta. Odložen odgovor (Pending) ide u red kad se
		// posao završi; čitanje sledećih zahteva ne čeka.
		private bool EnqueueCommandResult(AgentRequest request, AgentCommandResult result)
		{
			if (result.Pending is { } pending) {
				pending.ContinueWith(t => {
					var final = t.IsCompletedSuccessfully
						? t.Result
						: new AgentCommandResult(AgentResultCodes.InternalError, "Greška kioska pri izvršavanju komande.");
					if (!EnqueueCommandResult(request, final with { Pending = null })) {
						_logger?.LogWarning("Agent pipe [{ConnectionId}]: previše neposlatih odgovora ({Max}); veza se zatvara.", _connectionId, _options.MaxPendingResponses);
						_ = CloseAsync(TimeSpan.Zero);
					}
				}, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
				return true;
			}
			var response = NewResponse(request, result.ResultCode, result.Message);
			response.Result = result.Result;
			if (result.AfterResponse is not { } afterResponse) {
				return _outbound.TryEnqueueResponse(response);
			}
			var written = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			written.Task.ContinueWith(t => {
				try {
					afterResponse(t.Result);
				}
				catch (Exception ex) {
					_logger?.LogError(ex, "Agent pipe [{ConnectionId}]: radnja posle odgovora na {Command} {RequestId} bacila je izuzetak.",
										_connectionId, request.Command, request.RequestId);
				}
			}, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
			bool enqueued = _outbound.TryEnqueueResponse(response, written);
			if (!enqueued) {
				written.TrySetResult(false);
			}
			return enqueued;
		}

		// Atomska pretplata: odgovor sa početnim snapshot-om ide u red pod lock-om izvora, pre prve naredne promene.
		// Ponovljen zahtev na istoj vezi obnavlja postojeću pretplatu (isti ID), bez dupliranja događaja.
		private bool Subscribe(AgentRequest request)
		{
			lock (_subscriptionLock) {
				_subscription?.Dispose();
				_subscriptionId ??= Guid.NewGuid().ToString("N");
				var subscriptionId = _subscriptionId;
				bool enqueued = false;
				_subscription = _statusSource.Subscribe(
					onChanged: status => _outbound.EnqueueStatusChanged(status, subscriptionId),
					onInitial: status => {
						var response = NewResponse(request, AgentResultCodes.Completed, null);
						response.SubscriptionId = subscriptionId;
						response.Status = _statusSource.ToContract(status);
						_outbound.NoteStatusSent(status);
						enqueued = _outbound.TryEnqueueResponse(response);
					});
				_logger?.LogInformation("Agent pipe [{ConnectionId}]: pretplata {SubscriptionId} na status (zahtev {RequestId}).",
										_connectionId, subscriptionId, request.RequestId);
				return enqueued;
			}
		}

		// Vraća kod odbijanja ili null ako je zahtev ispravan za ovu instancu.
		private string? ValidateRequest(AgentRequest request, out string? message)
		{
			message = null;
			if (request.ProtocolVersion != AgentProtocol.Version) {
				message = $"Kiosk podržava protokol {AgentProtocol.Version}.";
				return AgentResultCodes.UnsupportedVersion;
			}
			if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > MaxRequestIdLength) {
				message = "RequestId nedostaje ili je predugačak.";
				return AgentResultCodes.InvalidRequest;
			}
			if (string.IsNullOrWhiteSpace(request.Command)) {
				message = "Command nedostaje.";
				return AgentResultCodes.InvalidRequest;
			}
			if (request.LockerId != _statusSource.LockerId) {
				message = "Zahtev nije namenjen ovom paketomatu.";
				return AgentResultCodes.WrongInstance;
			}
			if (request.ClientInstanceId != null && request.ClientInstanceId != _statusSource.ClientInstanceId) {
				message = "Zahtev je namenjen drugoj instanci kioska (kiosk je u međuvremenu restartovan).";
				return AgentResultCodes.WrongInstance;
			}
			return null;
		}

		private AgentResponse NewResponse(AgentRequest request, string resultCode, string? message) => new() {
			RequestId = request.RequestId ?? "",
			LockerId = _statusSource.LockerId,
			ClientInstanceId = _statusSource.ClientInstanceId,
			Command = request.Command ?? "",
			ResultCode = resultCode,
			Message = message,
			ServerTimeUtc = DateTimeOffset.UtcNow,
		};

		private async Task WriteLoopAsync(CancellationToken token)
		{
			AgentOutboundQueue.Entry? entry = null;
			try {
				while (true) {
					entry = await _outbound.DequeueAsync(token).ConfigureAwait(false);
					if (entry == null) {
						return;
					}
					byte[] body = entry.Response != null
									? AgentJson.Serialize(entry.Response)
									: AgentJson.Serialize(new AgentEvent {
										EventId = Guid.NewGuid().ToString("N"),
										Event = entry.EventKind!,
										LockerId = _statusSource.LockerId,
										ClientInstanceId = _statusSource.ClientInstanceId,
										SubscriptionId = entry.SubscriptionId,
										Status = _statusSource.ToContract(entry.Status!),
									});
					await WriteFrameAsync(body, token).ConfigureAwait(false);
					entry.Written?.TrySetResult(true);
				}
			}
			catch (Exception) when (token.IsCancellationRequested) {
				// veza se već zatvara
			}
			catch (Exception ex) {
				// Spor ili nestao Agent: veza se zatvara, Agent se ponovo povezuje i dobija ceo snapshot.
				_logger?.LogWarning("Agent pipe [{ConnectionId}]: slanje nije uspelo, veza se zatvara: {Error}", _connectionId, ex.Message);
				try {
					_cts.Cancel();
				}
				catch (ObjectDisposedException) { }
			}
			finally {
				// Odgovor čiji upis nije završen i oni koji nisu ni krenuli: „nije upisano“ (radnja posle odgovora ne visi).
				entry?.Written?.TrySetResult(false);
				_outbound.CompleteAndDropUnsent();
			}
		}

		private async Task WriteFrameAsync(byte[] body, CancellationToken token)
		{
			using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
			writeCts.CancelAfter(_options.WriteTimeout);
			try {
				await AgentFrameCodec.WriteFrameAsync(_pipe, body, _options.MaxMessageBytes, writeCts.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (!token.IsCancellationRequested) {
				throw new IOException($"Upis nije završen u roku od {_options.WriteTimeout}.");
			}
		}

		// Tekst koji šalje klijent ide u log samo skraćen i bez kontrolnih znakova (v4: AgentLogText).
		private static string Sanitize(string? value, int maxLength = 80) => AgentLogText.Sanitize(value, maxLength);
	}
}
