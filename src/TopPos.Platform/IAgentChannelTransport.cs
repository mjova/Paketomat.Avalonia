namespace TopPos.Platform
{
	/// <summary>
	/// Transport lokalnog kanala ka Agentu (<c>doc/plan.md</c> 1a): Windows named pipe sa SID proverom (kao v2), Linux Unix
	/// socket sa <c>SO_PEERCRED</c> (ugovor „Transport na Linuxu“). Protokol iznad transporta je isti; kiosk je server.
	/// Koristi ga jedna nit (petlja prihvatanja); <see cref="IAsyncDisposable.DisposeAsync"/> se zove posle nje.
	/// </summary>
	public interface IAgentChannelTransport : IAsyncDisposable
	{
		/// <summary>Adresa kanala za log i izjavu u <c>status.lock</c>: ime pipe-a ili putanja socket-a.</summary>
		string Address { get; }

		/// <summary>Jedini dozvoljeni klijent, za log (npr. <c>S-1-5-18</c>, <c>uid 0</c>).</summary>
		string AllowedClient { get; }

		/// <summary>
		/// Zauzima ime kanala. <c>false</c> = ime drži druga instanca ili kanal ne može da se napravi (<paramref name="error"/>);
		/// kanal se tada ne pokreće — bez drugog imena i slabijeg kanala.
		/// </summary>
		bool TryStart(out string error);

		/// <summary>
		/// Sledeća veza. Prolazne greške (klijent odustao pre prihvatanja) transport ponavlja sam; otkazan
		/// <paramref name="cancellationToken"/> završava sa <see cref="OperationCanceledException"/>.
		/// </summary>
		ValueTask<IAgentChannelConnection> AcceptAsync(CancellationToken cancellationToken);
	}

	/// <summary>Jedna prihvaćena veza.</summary>
	public interface IAgentChannelConnection : IAsyncDisposable
	{
		Stream Stream { get; }

		/// <summary>
		/// Identitet klijenta. Zove se posle prve poruke klijenta (<c>ClientHello</c>): Windows impersonaciju dozvoljava tek
		/// posle pročitanih podataka. Baca izuzetak ako identitet ne može da se utvrdi.
		/// </summary>
		AgentChannelClient GetClient();
	}

	/// <param name="Identity">SID (Windows) ili <c>uid N</c> (Linux), za log i dnevnik komandi.</param>
	/// <param name="ProcessId">PID klijenta, ako ga sistem vrati.</param>
	/// <param name="IsAllowed">Da li je to jedini dozvoljeni klijent (LocalSystem, odnosno root).</param>
	public sealed record AgentChannelClient(string Identity, int? ProcessId, bool IsAllowed);

	public sealed class AgentChannelTransportOptions
	{
		/// <summary>Ime named pipe-a (Windows).</summary>
		public required string PipeName { get; init; }

		/// <summary>Putanja Unix socket-a (Linux); lock fajl je pored nje, sa nastavkom <c>.lock</c>.</summary>
		public required string SocketPath { get; init; }

		/// <summary>Najviše istovremenih veza; transport drži i slobodnog slušaoca, pa mu treba malo više.</summary>
		public int MaxConnections { get; init; } = 2;

		/// <summary>Bafer named pipe-a: bar jedna cela poruka sa zaglavljem.</summary>
		public int MessageBufferBytes { get; init; }

		/// <summary>
		/// Samo testovi: dozvoljen klijent je tekući korisnik umesto LocalSystem-a, odnosno root-a. Nije u konfiguraciji
		/// (kao v2 <c>AllowedClientSid</c>).
		/// </summary>
		public bool AllowCurrentUser { get; init; }
	}
}
