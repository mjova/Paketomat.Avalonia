using Xunit;

namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>
	/// Podešavanja E2E okruženja. Testovi rade samo kad je TOPPOS_E2E=1.
	/// v4: TCP portovi nisu fiksni — simulatori rade na slobodnim portovima i javljaju ih u READY redu.
	/// Serijski uređaji (skener, POS, kontroler Serial): Windows — com0com parovi kao u v2 (skener COM18 (klijent) ↔ COM19
	/// (simulator), POS COM20 ↔ COM21, kontroler COM22 ↔ COM23, menjaju se env promenljivama); Linux — simulator sam pravi
	/// pty par i symlink koji otvara kiosk (<c>KioskPortName</c>), u folderu <c>dev</c> u sandbox korenu.
	/// </summary>
	public sealed class E2ESettings
	{
		public const string EnableVariable = "TOPPOS_E2E";

		public const string SkipReason = $"E2E testovi su isključeni; postaviti {EnableVariable}=1 (traži otključan desktop; na Windows-u com0com parove).";

		public static bool IsEnabled => Environment.GetEnvironmentVariable(EnableVariable) == "1";

		/// <summary>Prvi red svakog [SkippableFact] E2E testa: preskače test kad E2E nije uključen.</summary>
		public static void RequireEnabled() => Skip.IfNot(IsEnabled, SkipReason);

		/// <summary>Preskače test koji traži Debug-only kod klijenta (npr. SimulatedPosClient) kad se E2E gradi u Release-u.</summary>
		public static void RequireDebugClient(string feature)
			=> Skip.If(Current.BuildConfiguration != "Debug", $"{feature} postoji samo u Debug build-u klijenta (E2E je u {Current.BuildConfiguration}).");

		/// <summary>
		/// v4: preskače test sa pravim mišem (Home, dug pritisak) pre pokretanja sesije — na Linuxu ga UI ne podržava
		/// (AtSpiKioskUi), a preskakanje usred scenarija troši ~20 s na start kioska.
		/// </summary>
		public static void RequireRealMouse()
		{
			RequireEnabled();
			Skip.If(OperatingSystem.IsLinux(), "Pravi miš (Home, dug pritisak) na Linuxu nije podržan u E2E (AT-SPI nema klik mišem, Wayland ne dozvoljava ubacivanje).");
		}

		public const string LockerPortsVariable = "TOPPOS_E2E_LOCKER_PORTS";

		/// <summary>
		/// Preskače test sa kontrolerom Serial kad com0com par za kontroler ne postoji — treći par nije uslov za ostatak paketa.
		/// v4, Linux: par pravi simulator, uvek postoji.
		/// </summary>
		public static void RequireLockerSerialPorts()
		{
			if (Current.UsesVirtualSerialPairs) {
				return;
			}
			var ports = System.IO.Ports.SerialPort.GetPortNames();
			var missing = new[] { Current.LockerClientPort, Current.LockerSimulatorPort }
							.Where(port => !ports.Contains(port, StringComparer.OrdinalIgnoreCase)).ToList();
			Skip.If(missing.Count > 0,
					$"Nema com0com para za kontroler vrata ({string.Join(", ", missing)}); napraviti ga sa setupc install PortName={Current.LockerClientPort} PortName={Current.LockerSimulatorPort} ili postaviti {LockerPortsVariable}.");
		}

		public static E2ESettings Current { get; } = Load();

		public required string RepositoryRoot { get; init; }

		public required string BuildConfiguration { get; init; }

		public required string SandboxRoot { get; init; }

		/// <summary>v4, Linux: serijske parove pravi simulator (pty); strana simulatora se ne zadaje (prazno ime).</summary>
		public bool UsesVirtualSerialPairs => OperatingSystem.IsLinux();

		public required string ScannerClientPort { get; init; }

		public required string ScannerSimulatorPort { get; init; }

		public required string PosClientPort { get; init; }

		public required string PosSimulatorPort { get; init; }

		public required string LockerClientPort { get; init; }

		public required string LockerSimulatorPort { get; init; }

		public string LockerId => "111";

		public string CourierQrCode => "KURIR-E2E-1";

		/// <summary>Build izlaz kioska (<c>TopPos.Kiosk</c>).</summary>
		public string KioskOutput => Path.Combine(RepositoryRoot, "src", "TopPos.Kiosk.Avalonia", "bin", BuildConfiguration, "net10.0");

		/// <summary>Build izlaz simulatora servera i paketomata (<c>TopPos.Simulators</c>).</summary>
		public string SimulatorsOutput => Path.Combine(RepositoryRoot, "tools", "TopPos.Simulators", "bin", BuildConfiguration, "net10.0");

		private static E2ESettings Load()
		{
			string sandboxRoot = Path.Combine(Path.GetTempPath(), "toppos-e2e");
			// Linux: kiosk otvara symlink pty para koji simulator napravi (jedna sesija u isto vreme — session.lock)
			string Pty(string name) => Path.Combine(sandboxRoot, "dev", name);
			var (scannerClient, scannerSimulator) = OperatingSystem.IsLinux() ? (Pty("ttySCAN"), "") : PortPair("TOPPOS_E2E_SCANNER_PORTS", "COM18", "COM19");
			var (posClient, posSimulator) = OperatingSystem.IsLinux() ? (Pty("ttyPOS"), "") : PortPair("TOPPOS_E2E_POS_PORTS", "COM20", "COM21");
			var (lockerClient, lockerSimulator) = OperatingSystem.IsLinux() ? (Pty("ttyLOCK"), "") : PortPair(LockerPortsVariable, "COM22", "COM23");
			return new E2ESettings {
				RepositoryRoot = FindRepositoryRoot(),
#if DEBUG
				BuildConfiguration = "Debug",
#else
				BuildConfiguration = "Release",
#endif
				SandboxRoot = sandboxRoot,
				ScannerClientPort = scannerClient,
				ScannerSimulatorPort = scannerSimulator,
				PosClientPort = posClient,
				PosSimulatorPort = posSimulator,
				LockerClientPort = lockerClient,
				LockerSimulatorPort = lockerSimulator,
			};
		}

		private static string FindRepositoryRoot()
		{
			for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent) {
				if (File.Exists(Path.Combine(dir.FullName, "Paketomat.slnx"))) {
					return dir.FullName;
				}
			}
			throw new InvalidOperationException($"Paketomat.slnx nije pronađen iznad {AppContext.BaseDirectory}");
		}

		private static (string Client, string Simulator) PortPair(string name, string defaultClient, string defaultSimulator)
		{
			var parts = Environment.GetEnvironmentVariable(name)?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
			return parts is { Length: 2 } ? (parts[0], parts[1]) : (defaultClient, defaultSimulator);
		}
	}
}
