using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace TopPos.Platform.Windows
{
	/// <summary>
	/// Named pipe sa eksplicitnom DACL i proverom SID-a klijenta (v2 <c>AgentPipeServer</c>, deo koji zavisi od pipe-a).
	/// Ime se zauzima sa <c>FirstPipeInstance</c>; posle toga uvek postoji jedan slobodan slušalac, pa ime ne nestaje između
	/// veza — novi slušalac se pravi pre nego što se prihvaćena veza preda serveru.
	/// </summary>
	[SupportedOSPlatform("windows")]
	internal sealed class WindowsAgentPipeTransport : IAgentChannelTransport
	{
		private readonly AgentChannelTransportOptions _options;
		private readonly ILogger? _logger;
		private readonly SecurityIdentifier _allowedClientSid;
		private NamedPipeServerStream? _listener;

		public WindowsAgentPipeTransport(AgentChannelTransportOptions options, ILogger? logger)
		{
			_options = options;
			_logger = logger;
			// Agent je Windows servis pod LocalSystem-om; tekući nalog samo u testovima.
			_allowedClientSid = options.AllowCurrentUser
									? WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Nalog procesa nema SID.")
									: new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
		}

		public string Address => _options.PipeName;

		public string AllowedClient => _allowedClientSid.Value;

		public bool TryStart(out string error)
		{
			try {
				_listener = CreateInstance(firstInstance: true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				error = ex.Message;
				return false;
			}
			error = "";
			return true;
		}

		public async ValueTask<IAgentChannelConnection> AcceptAsync(CancellationToken cancellationToken)
		{
			while (true) {
				var listener = _listener ?? throw new InvalidOperationException("Kanal nije pokrenut.");
				try {
					await listener.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
				}
				catch (Exception) when (cancellationToken.IsCancellationRequested) {
					throw new OperationCanceledException(cancellationToken);
				}
				catch (Exception ex) when (ex is IOException or ObjectDisposedException) {
					// Klijent je odustao pre prihvatanja; slušalac se menja novim. Novi se pravi PRE zatvaranja starog, da ime
					// pipe-a ni za trenutak ne ostane bez instance (drugi proces bi ga inače mogao zauzeti).
					_logger?.LogDebug("Agent pipe: neuspelo prihvatanje veze: {Error}", ex.Message);
					try {
						await ReplaceListenerAsync(cancellationToken).ConfigureAwait(false);
					}
					finally {
						listener.Dispose();
					}
					continue;
				}

				// Novi slušalac pre bilo čega drugog, da ime pipe-a ostane zauzeto.
				try {
					await ReplaceListenerAsync(cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException) {
					listener.Dispose();
					throw;
				}
				return new Connection(listener, _allowedClientSid);
			}
		}

		public ValueTask DisposeAsync()
		{
			// Posle petlje prihvatanja, da slušalac napravljen u poslednjem trenutku ne ostane otvoren.
			try {
				_listener?.Dispose();
			}
			catch (Exception) { }
			return ValueTask.CompletedTask;
		}

		private NamedPipeServerStream CreateInstance(bool firstInstance)
		{
			var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
			// Dva mesta više od broja veza: slušalac postoji i kad su sve veze zauzete i dok se višak veze odbija.
			// Bafer za bar jednu celu poruku: bez njega upis traje dok Agent ne pročita, pa poslednji status pri gašenju ne
			// bi stigao da ode.
			return NamedPipeServerStreamAcl.Create(_options.PipeName, PipeDirection.InOut, _options.MaxConnections + 2,
													PipeTransmissionMode.Byte, options, inBufferSize: _options.MessageBufferBytes,
													outBufferSize: _options.MessageBufferBytes, AgentPipeSecurity.Create(_allowedClientSid));
		}

		// Pravi novog slušaoca; posle neuspeha pokušava ponovo (ograničen backoff) dok se kanal ne zaustavi.
		private async Task ReplaceListenerAsync(CancellationToken cancellationToken)
		{
			var delay = TimeSpan.FromMilliseconds(250);
			while (true) {
				cancellationToken.ThrowIfCancellationRequested();
				try {
					_listener = CreateInstance(firstInstance: false);
					return;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
					_logger?.LogWarning("Agent pipe: nova instanca pipe-a nije napravljena, ponavlja se za {Delay}: {Error}", delay, ex.Message);
				}
				await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
				delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(5).Ticks));
			}
		}

		private sealed class Connection(NamedPipeServerStream pipe, SecurityIdentifier allowedClientSid) : IAgentChannelConnection
		{
			public Stream Stream => pipe;

			public AgentChannelClient GetClient()
			{
				var processId = AgentPipeSecurity.GetClientProcessId(pipe);
				var sid = AgentPipeSecurity.GetClientSid(pipe);
				return new AgentChannelClient(sid.Value, processId, sid == allowedClientSid);
			}

			public ValueTask DisposeAsync() => pipe.DisposeAsync();
		}
	}
}
