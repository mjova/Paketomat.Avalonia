using System.Text.Json.Nodes;

namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>
	/// v4: adresa kanala ka Agentu za E2E — ime pipe-a (Windows) ili putanja Unix socket-a (Linux, ugovor „Transport na
	/// Linuxu“). Socket je u kratkoj putanji u privatnom runtime folderu naloga (<see cref="LinuxSocketRoot"/>; putanja socket-a
	/// ima najviše 107 bajtova, a sandbox zavisi od <c>TMPDIR</c> i nosi ime testa); folder pravi kiosk (<c>0700</c>), a stare
	/// briše <see cref="KioskSession"/> pri startu. Pravljenje adrese ne dira disk (test može biti preskočen).
	/// </summary>
	public static class AgentChannel
	{
		/// <summary>Linux: <c>$XDG_RUNTIME_DIR/toppos-e2e-agent</c> (<c>/run/user/&lt;uid&gt;</c>, samo nalog), inače u <c>/tmp</c>.</summary>
		public static string LinuxSocketRoot
			=> Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime
					? Path.Combine(runtime, "toppos-e2e-agent")
					: $"/tmp/toppos-e2e-agent-{Environment.UserName}";

		public static string NewAddress()
			=> OperatingSystem.IsWindows()
					? $"TopPos.E2E.{Guid.NewGuid():N}"
					: Path.Combine(LinuxSocketRoot, $"{Guid.NewGuid():N}"[..12], "agent.v1.sock");

		/// <summary>Sekcija <c>AgentCommunication</c> klijenta: kanal uključen na <paramref name="address"/>.</summary>
		public static JsonObject Section(string address) => new() {
			["Enabled"] = true,
			[OperatingSystem.IsWindows() ? "PipeName" : "SocketPath"] = address,
		};

		/// <summary>Kanal uključen na <paramref name="address"/> (v2 <c>WithPipe</c>).</summary>
		public static KioskOptions Options(string address) => new() {
			ConfigureClient = c => c["AgentCommunication"] = Section(address),
		};

		/// <summary>Ime polja adrese u izjavi <c>AgentChannel</c> (status.lock) i u konfiguraciji.</summary>
		public static string AddressField => OperatingSystem.IsWindows() ? "PipeName" : "SocketPath";

		/// <summary>Identitet dozvoljenog klijenta u logu kioska (prihvaćena komanda): SID LocalSystem-a, odnosno root.</summary>
		public static string AllowedClientInLog => OperatingSystem.IsWindows() ? "klijent S-1-5-18" : "klijent uid 0";
	}
}
