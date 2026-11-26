using System.IO;
using Microsoft.Extensions.Logging;
using TopPos.Agent.Contracts;
using TopPos.Platform;
using TopPos.Kiosk.Workflow;

namespace TopPos.Kiosk.AgentCommunication
{
	/// <summary>
	/// Lokalni kanal ka Agentu (doc/notes/agent-client-komunikacija-i-komande-plan.md, §4 i §5): named pipe server sa
	/// eksplicitnom DACL, proverom identiteta klijenta i statusom preko pretplate. Kiosk je server, Agent klijent; obe
	/// strane su na istom računaru (ODL-050).
	///
	/// Ime pipe-a se zauzima sa <c>FirstPipeInstance</c>: ako ga je neko već zauzeo, kanal se ne pokreće (bez drugog imena
	/// i slabijeg kanala). Posle toga uvek postoji jedan slobodan slušalac, pa ime ne nestaje između veza.
	///
	/// Heartbeat se šalje samo kad UI nit potvrdi da radi (<c>uiLivenessProbe</c>); zaglavljen UI zato ne daje lažno
	/// svež status — Agentu status zastari.
	///
	/// v4: transport je <see cref="IAgentChannelTransport"/> iz platformskog sloja — named pipe (Windows, kao v2) ili Unix
	/// socket (Linux, ugovor „Transport na Linuxu“). Zauzimanje imena i slobodan slušalac su u transportu; ovde je isto
	/// ponašanje veza kao u v2.
	/// </summary>
	public sealed class AgentPipeServer : IAsyncDisposable
	{
		private readonly AgentPipeServerOptions _options;
		private readonly KioskStatusSource _statusSource;
		private readonly Func<CancellationToken, Task<bool>> _uiLivenessProbe;
		private readonly ILogger? _logger;
		private readonly AgentCommandDispatcher? _commands;
		private readonly CancellationTokenSource _cts = new();
		private readonly object _connectionsLock = new();
		private readonly List<AgentPipeConnection> _connections = [];
		private readonly List<Task> _connectionTasks = [];
		private IAgentChannelTransport? _transport;
		private Task? _acceptLoop;
		private Task? _heartbeatLoop;
		private int _nextConnectionId;
		private bool _stopped;
		private Task? _stop;

		/// <param name="commands">Komande koje menjaju stanje kioska; null = samo statusne komande.</param>
		public AgentPipeServer(AgentPipeServerOptions options, KioskStatusSource statusSource, Func<CancellationToken, Task<bool>> uiLivenessProbe, ILogger? logger,
								AgentCommandDispatcher? commands = null)
		{
			_options = options;
			_statusSource = statusSource;
			_uiLivenessProbe = uiLivenessProbe;
			_logger = logger;
			_commands = commands;
		}

		/// <summary>Broj aktivnih veza (testovi, dijagnostika).</summary>
		public int ConnectionCount {
			get { lock (_connectionsLock) { return _connections.Count; } }
		}

		/// <summary>Adresa kanala (ime pipe-a ili putanja socket-a) kad je server pokrenut; inače <c>null</c>.</summary>
		public string? Address { get; private set; }

		/// <summary>
		/// Zauzima ime kanala i počinje da prima veze. Vraća false (uz grešku u logu) ako je ime već zauzeto ili kanal ne može
		/// da se napravi — kiosk tada radi dalje bez kanala.
		/// </summary>
		public bool Start()
		{
			// v4: server je jednokratan — posle zaustavljanja (ili drugog pokretanja) ne zauzima ime ponovo.
			lock (_connectionsLock) {
				if (_stopped || _transport != null) {
					throw new InvalidOperationException("Server kanala ka Agentu se pokreće samo jednom.");
				}
			}
			var transport = PlatformServices.CreateAgentChannelTransport(_options.ToTransportOptions(), _logger);
			if (!transport.TryStart(out string error)) {
				_logger?.LogError("Agent pipe: ime {PipeName} je već zauzeto ili kanal ne može da se napravi; kanal ka Agentu se NE pokreće: {Error}",
									transport.Address, error);
				return false;
			}
			_transport = transport;
			Address = transport.Address;
			_logger?.LogInformation("Agent pipe: server pokrenut ({PipeName}, dozvoljen klijent {AllowedSid}, najviše {MaxConnections} veze).",
									transport.Address, transport.AllowedClient, _options.MaxConnections);
			_acceptLoop = Task.Run(AcceptLoopAsync);
			_heartbeatLoop = Task.Run(HeartbeatLoopAsync);
			return true;
		}

		/// <summary>
		/// Zaustavlja prijem novih veza, daje postojećim vezama kratko vreme da pošalju ono što je već u redu (npr. status
		/// <c>ShuttingDown</c>), pa ih zatvara.
		/// v4: zaustavljanje se radi jednom; svaki sledeći poziv (i <see cref="DisposeAsync"/>) čeka isto zaustavljanje — u v2 je
		/// drugi poziv odmah vraćao, pa je Dispose mogao da oslobodi token dok prvo zaustavljanje još prazni veze.
		/// </summary>
		public Task StopAsync()
		{
			AgentPipeConnection[] connections;
			Task[] connectionTasks;
			lock (_connectionsLock) {
				if (_stop != null) {
					return _stop;
				}
				_stopped = true;
				connections = [.. _connections];
				connectionTasks = [.. _connectionTasks];
				_stop = StopCoreAsync(connections, connectionTasks);
				return _stop;
			}
		}

		private async Task StopCoreAsync(AgentPipeConnection[] connections, Task[] connectionTasks)
		{
			await Task.Yield();     // van lock-a
			try {
				// Prvo pražnjenje (veze još rade), pa prekid svega.
				await Task.WhenAll(connections.Select(c => c.CloseAsync(_options.ShutdownDrainTimeout))).ConfigureAwait(false);
				_cts.Cancel();
				var loops = new[] { _acceptLoop, _heartbeatLoop }.Where(t => t != null).Cast<Task>();
				try {
					await Task.WhenAll(loops.Concat(connectionTasks)).ConfigureAwait(false);
				}
				catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
			}
			finally {
				// Posle petlje prihvatanja, da slušalac napravljen u poslednjem trenutku ne ostane otvoren; i kad pražnjenje
				// baci izuzetak, da ime kanala (lock na Linuxu) ne ostane zauzeto.
				if (_transport != null) {
					await _transport.DisposeAsync().ConfigureAwait(false);
				}
			}
			_logger?.LogInformation("Agent pipe: server zaustavljen.");
		}

		public async ValueTask DisposeAsync()
		{
			await StopAsync().ConfigureAwait(false);
			_cts.Dispose();
		}

		private async Task AcceptLoopAsync()
		{
			var token = _cts.Token;
			var transport = _transport!;
			while (!token.IsCancellationRequested) {
				IAgentChannelConnection channel;
				try {
					channel = await transport.AcceptAsync(token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested) {
					return;
				}
				catch (Exception ex) {
					_logger?.LogError(ex, "Agent pipe: prijem veza je stao; kanal ka Agentu više ne prima veze.");
					return;
				}

				int connectionId = Interlocked.Increment(ref _nextConnectionId);
				lock (_connectionsLock) {
					if (_stopped) {
						_ = channel.DisposeAsync();
						return;
					}
					if (_connections.Count >= _options.MaxConnections) {
						_logger?.LogWarning("Agent pipe [{ConnectionId}]: dostignut je najveći broj veza ({MaxConnections}); nova veza se odbija.",
											connectionId, _options.MaxConnections);
						_ = channel.DisposeAsync();
						continue;
					}
					var connection = new AgentPipeConnection(channel, connectionId, _options, _statusSource, _commands, _logger, token, transport.AllowedClient);
					_connections.Add(connection);
					_connectionTasks.Add(RunConnectionAsync(connection));
				}
			}
		}

		private async Task RunConnectionAsync(AgentPipeConnection connection)
		{
			await Task.Yield();
			_logger?.LogInformation("Agent pipe [{ConnectionId}]: nova veza.", connection.ConnectionId);
			try {
				await connection.RunAsync().ConfigureAwait(false);
			}
			finally {
				lock (_connectionsLock) {
					_connections.Remove(connection);
					_connectionTasks.RemoveAll(t => t.IsCompleted);
				}
				_logger?.LogInformation("Agent pipe [{ConnectionId}]: veza zatvorena.", connection.ConnectionId);
			}
		}

		private async Task HeartbeatLoopAsync()
		{
			var token = _cts.Token;
			bool uiWasResponsive = true;
			using var timer = new PeriodicTimer(_options.HeartbeatInterval);
			try {
				while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) {
					AgentPipeConnection[] subscribed;
					lock (_connectionsLock) {
						subscribed = _connections.Where(c => c.SubscriptionId != null).ToArray();
					}
					if (subscribed.Length == 0) {
						continue;
					}
					bool responsive = await ProbeUiAsync(token).ConfigureAwait(false);
					if (!responsive) {
						if (uiWasResponsive) {
							_logger?.LogWarning("Agent pipe: UI nit nije potvrdila rad u roku od {Timeout}; heartbeat se ne šalje dok ne proradi.", _options.UiAckTimeout);
						}
						uiWasResponsive = false;
						continue;
					}
					if (!uiWasResponsive) {
						_logger?.LogInformation("Agent pipe: UI nit ponovo odgovara; heartbeat se nastavlja.");
						uiWasResponsive = true;
					}
					var status = _statusSource.Current;
					foreach (var connection in subscribed) {
						connection.EnqueueHeartbeat(status);
					}
				}
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested) {
			}
		}

		private async Task<bool> ProbeUiAsync(CancellationToken token)
		{
			using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
			probeCts.CancelAfter(_options.UiAckTimeout);
			try {
				var probe = _uiLivenessProbe(probeCts.Token);
				// Izuzetak probe koja završi posle roka ne sme da ostane neopažen.
				_ = probe.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
				var completed = await Task.WhenAny(probe, Task.Delay(Timeout.Infinite, probeCts.Token)).ConfigureAwait(false);
				return completed == probe && probe.IsCompletedSuccessfully && probe.Result;
			}
			catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) {
				return false;
			}
		}
	}
}
