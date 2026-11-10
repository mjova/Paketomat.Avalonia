using Xunit;

namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>
	/// Podešavanja E2E okruženja. Testovi rade samo kad je TOPPOS_E2E=1; portovi se mogu pregaziti env promenljivama
	/// (mašine se razlikuju). Podrazumevani com0com parovi: skener COM18 (klijent) ↔ COM19 (simulator),
	/// POS COM20 (klijent) ↔ COM21 (simulator), kontroler vrata Serial COM22 (klijent) ↔ COM23 (simulator; samo testovi sa
	/// <see cref="KioskOptions.SerialController"/>). Vidi doc/plan-e2e-ui-testing.md, sekcija 3.8.
	/// </summary>
	public sealed class E2ESettings
	{
		public const string EnableVariable = "TOPPOS_E2E";
		public const string SkipReason = $"E2E testovi su isključeni; postaviti {EnableVariable}=1 (traži com0com parove i otključan desktop).";

		public static bool IsEnabled => Environment.GetEnvironmentVariable(EnableVariable) == "1";

		/// <summary>Prvi red svakog [SkippableFact] E2E testa: preskače test kad E2E nije uključen.</summary>
		public static void RequireEnabled() => Skip.IfNot(IsEnabled, SkipReason);

		/// <summary>Preskače test koji traži Debug-only kod klijenta (npr. SimulatedPosClient) kad se E2E gradi u Release-u.</summary>
		public static void RequireDebugClient(string feature)
			=> Skip.If(Current.BuildConfiguration != "Debug", $"{feature} postoji samo u Debug build-u klijenta (E2E je u {Current.BuildConfiguration}).");

		public const string LockerPortsVariable = "TOPPOS_E2E_LOCKER_PORTS";

		/// <summary>
		/// Preskače test sa kontrolerom Serial kad com0com par za kontroler ne postoji — treći par nije uslov za ostatak paketa.
		/// </summary>
		public static void RequireLockerSerialPorts()
		{
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

		public required int ServerPort { get; init; }
		public required int LockerTcpPort { get; init; }
		public required int LockerApiPort { get; init; }
		public required string ScannerClientPort { get; init; }
		public required string ScannerSimulatorPort { get; init; }
		public required string PosClientPort { get; init; }
		public required string PosSimulatorPort { get; init; }
		public required string LockerClientPort { get; init; }
		public required string LockerSimulatorPort { get; init; }

		public string LockerId => "111";
		public string CourierQrCode => "KURIR-E2E-1";

		public string ProjectOutput(string projectName)
			=> Path.Combine(RepositoryRoot, projectName, "bin", BuildConfiguration, "net9.0-windows");

		private static E2ESettings Load()
		{
			var (scannerClient, scannerSimulator) = PortPair("TOPPOS_E2E_SCANNER_PORTS", "COM18", "COM19");
			var (posClient, posSimulator) = PortPair("TOPPOS_E2E_POS_PORTS", "COM20", "COM21");
			var (lockerClient, lockerSimulator) = PortPair(LockerPortsVariable, "COM22", "COM23");
			return new E2ESettings {
				RepositoryRoot = FindRepositoryRoot(),
#if DEBUG
				BuildConfiguration = "Debug",
#else
				BuildConfiguration = "Release",
#endif
				SandboxRoot = Path.Combine(Path.GetTempPath(), "toppos-e2e"),
				ServerPort = IntVariable("TOPPOS_E2E_SERVER_PORT", 5191),
				LockerTcpPort = IntVariable("TOPPOS_E2E_LOCKER_TCP_PORT", 4091),
				LockerApiPort = IntVariable("TOPPOS_E2E_LOCKER_API_PORT", 5291),
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
				if (File.Exists(Path.Combine(dir.FullName, "TopPos.sln"))) {
					return dir.FullName;
				}
			}
			throw new InvalidOperationException($"TopPos.sln nije pronađen iznad {AppContext.BaseDirectory}");
		}

		private static int IntVariable(string name, int defaultValue)
			=> int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : defaultValue;

		private static (string Client, string Simulator) PortPair(string name, string defaultClient, string defaultSimulator)
		{
			var parts = Environment.GetEnvironmentVariable(name)?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
			return parts is { Length: 2 } ? (parts[0], parts[1]) : (defaultClient, defaultSimulator);
		}
	}
}
