using System.Text.Json;
using System.Text.Json.Nodes;

namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>
	/// Podešavanje sandbox-a za jedan test, preko zajedničkih šablona iz Config\. Bez opcija (null) sesija je ista kao
	/// pre — postojeći testovi se ne menjaju.
	/// </summary>
	public sealed record KioskOptions
	{
		public const string PosSimulatorFile = "app-configuration.Simulator.json";

		/// <summary>Menja app-configuration.json klijenta posle zamene tokena, pre upisa.</summary>
		public Action<JsonObject>? ConfigureClient { get; init; }

		/// <summary>Menja app-configuration.json ServerSimulator-a posle zamene tokena, pre upisa.</summary>
		public Action<JsonObject>? ConfigureServer { get; init; }

		/// <summary>Menja app-configuration.json LockerSimulator-a posle zamene tokena, pre upisa.</summary>
		public Action<JsonObject>? ConfigureLocker { get; init; }

		/// <summary>Raspored vrata koji su opcije podesile u konfiguracijama; po njemu <see cref="TestData"/> bira vrata.</summary>
		public LockerLayout Layout { get; init; } = LockerLayout.Ncu24L;

		/// <summary>
		/// Kontroler Serial (COM, par <see cref="E2ESettings.LockerPortsVariable"/>) umesto NCU24L, raspored
		/// <see cref="LockerLayout.Serial"/>, u sva tri šablona. Test pre sesije zove
		/// <see cref="E2ESettings.RequireLockerSerialPorts"/>.
		/// </summary>
		public static KioskOptions SerialController(E2ESettings settings)
		{
			var layout = LockerLayout.Serial;
			return new KioskOptions {
				Layout = layout,
				ConfigureClient = c => {
					var locker = c["Locker"]!;
					locker["ControllerType"] = "Serial";
					locker["LockerBoxCount"] = layout.DoorCount;
					locker["ComPort"]!["PortName"] = settings.LockerClientPort;
				},
				ConfigureLocker = c => {
					c["Locker"]!["ModuleCount"] = layout.Modules;
					c["Locker"]!["BoxCountPerModule"] = layout.DoorsPerModule;
					c["LockController"]!["Type"] = "Serial";
					c["LockController"]!["ComPort"]!["PortName"] = settings.LockerSimulatorPort;
				},
				ConfigureServer = c => c["DoorCount"] = layout.DoorCount,
			};
		}

		/// <summary>
		/// Priprema folder klijenta posle <see cref="Sandbox.AssertClean"/>, a pre starta procesa — sme da upiše i fajlove sa
		/// spiska <see cref="Sandbox.ExcludedFiles"/> (npr. <see cref="PosSimulatorFile"/>, PaymentConfirmation.json).
		/// </summary>
		public Action<string>? PrepareClientDirectory { get; init; }

		/// <summary>
		/// Radnja kad simulatori već rade, a klijent još nije pokrenut — npr. paket na serveru pre nego što klijent pri startu
		/// pošalje potvrdu iz pripremljenog fajla.
		/// </summary>
		public Func<KioskSession, Task>? BeforeClientStartAsync { get; init; }

		/// <summary>
		/// Kontroler ne radi od starta (npr. moduli isključeni u <see cref="BeforeClientStartAsync"/>): klijent ostaje u Idle,
		/// sa porukom da paketomat nije u funkciji, i ne prikazuje StartPage — Start tek kad paketomat proradi
		/// (MainWindowViewModel.OnLockerStatusChanged). Start klijenta se zato čeka po toj poruci u logu. Scenario pre kraja
		/// uključuje module, da opšte provere posle scenarija nađu kiosk na Start-u.
		/// </summary>
		public bool StartsOutOfOrder { get; init; }

		/// <summary>
		/// Klijent (Debug) umesto pravog POS-a preko com0com koristi SimulatedPosClient sa zadatim scenarijem
		/// (Approve, Cancel, Refuse, Error, Timeout, Throw). Proveru da je simulator stvarno uključen radi
		/// <see cref="KioskSteps.AssertPosSimulator"/>.
		/// </summary>
		public KioskOptions WithPosSimulator(string scenario, int stepDelayMs = 200)
		{
			var previous = PrepareClientDirectory;
			return this with {
				PrepareClientDirectory = directory => {
					previous?.Invoke(directory);
					File.WriteAllText(Path.Combine(directory, PosSimulatorFile),
										JsonSerializer.Serialize(new { Pos = new { Scenario = scenario, StepDelayMs = stepDelayMs } }));
				},
			};
		}
	}

	/// <summary>Raspored vrata: broj modula × vrata po modulu (isto u klijentu, LockerSimulator-u i ServerSimulator-u).</summary>
	public sealed record LockerLayout(int Modules, int DoorsPerModule)
	{
		/// <summary>Zajednički šabloni iz Config\: NCU24L, 2 × 24.</summary>
		public static LockerLayout Ncu24L { get; } = new(2, 24);

		/// <summary>Serial, kao app-configuration-Ananas.json: 4 × 8.</summary>
		public static LockerLayout Serial { get; } = new(4, 8);

		public int DoorCount => Modules * DoorsPerModule;
	}
}
