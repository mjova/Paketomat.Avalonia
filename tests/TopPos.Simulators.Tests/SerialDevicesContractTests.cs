using System.IO.Ports;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using Flatsoft.Common;
using Microsoft.Extensions.Logging.Abstractions;
using TopPos.LockController;
using TopPos.LockController.Serial;
using TopPos.LockerSimulator;
using TopPos.Simulators.Serial;
using Xunit;
using BarcodeScannerClient = TopPos.Kiosk.BarcodeScannerClient;
using KioskScannerConfiguration = TopPos.Kiosk.BarcodeScannerConfiguration;
using SimulatorScannerConfiguration = TopPos.LockerSimulator.BarcodeScannerConfiguration;

namespace TopPos.Simulators.Tests
{
	/// <summary>
	/// Serijski uređaji simulatora kroz pty par (Linux) i pravi klijenti kioska: serijski kontroler brava
	/// (<see cref="LockControllerClientSerial"/>), skener (<see cref="BarcodeScannerClient"/>) i POS kontrolni API.
	/// Windows: isti kod ide kroz com0com (POS: <c>PosEndToEndTests</c> uz POS_SIM_E2E_PORTS).
	/// </summary>
	public sealed class SerialDevicesContractTests : IAsyncLifetime
	{
		private readonly string _dir = Path.Combine(Path.GetTempPath(), $"toppos-sim-{Guid.NewGuid():N}");
		private LockerEngine? _engine;
		private HttpClient _control = default!;

		public Task InitializeAsync()
		{
			if (!OperatingSystem.IsLinux()) {
				return Task.CompletedTask;
			}
			_engine = new LockerEngine(new LockerSimulatorConfiguration {
				Locker = new LockerConfiguration { ModuleCount = 6, BoxCountPerModule = 8 },	// serijski kontroler: 8 vrata po modulu
				LockController = new LockControllerConfiguration {
					Type = "Serial",
					ComPort = Port(),
					KioskPortName = Path.Combine(_dir, "ttyLOCK"),
				},
				BarcodeScanner = new SimulatorScannerConfiguration { ComPort = Port(), KioskPortName = Path.Combine(_dir, "ttySCAN") },
				PosTerminal = new PosTerminalSimulatorConfiguration { ComPort = Port(), KioskPortName = Path.Combine(_dir, "ttyPOS") },
				ControlApi = new ControlApiConfiguration { Port = 0 },
			}, NullLogger.Instance);
			_control = new HttpClient { BaseAddress = new Uri($"http://localhost:{_engine.ControlApiPort}/_sim/") };
			return Task.CompletedTask;
		}

		public async Task DisposeAsync()
		{
			_control?.Dispose();
			if (_engine != null) {
				await _engine.DisposeAsync();
			}
			if (Directory.Exists(_dir)) {
				Directory.Delete(_dir, recursive: true);
			}
		}

		private static ComPortConfiguration Port(string? name = null)
			=> new() { PortName = name ?? "", BaudRate = 9600, DataBits = 8, StopBits = StopBits.One, Parity = "None" };

		[LinuxFact]
		public void Kiosk_ports_are_stable_symlinks()
		{
			Assert.Equal(Path.Combine(_dir, "ttyLOCK"), _engine!.LockControllerPort);
			Assert.Equal(Path.Combine(_dir, "ttySCAN"), _engine.BarcodeScannerPort);
			Assert.Equal(Path.Combine(_dir, "ttyPOS"), _engine.PosTerminalPort);
			Assert.StartsWith("/dev/pts/", new FileInfo(_engine.PosTerminalPort!).LinkTarget);
			Assert.Null(_engine.TcpPort);
		}

		[LinuxFact]
		public async Task Serial_lock_controller_opens_door_and_reports_status()
		{
			var client = new LockControllerClientSerial(Port(_engine!.LockControllerPort));
			var timeout = TimeSpan.FromSeconds(3);

			Assert.Equal(DoorStatusCode.Closed, await client.CheckDoorSingle(5, timeout, CancellationToken.None));
			Assert.Equal(DoorStatusCode.Open, await client.OpenDoorSingle(5, timeout, CancellationToken.None));
			Assert.Equal(DoorStatusCode.Open, await client.CheckDoorSingle(5, timeout, CancellationToken.None));

			var module1 = await client.CheckDoorMultiple(1, timeout, CancellationToken.None);
			Assert.NotNull(module1);
			Assert.Equal(8, module1.Length);
			Assert.Equal(DoorStatusCode.Open, module1[4]);
			Assert.Equal(7, module1.Count(s => s == DoorStatusCode.Closed));

			var unlock = Assert.Single(await _control.GetFromJsonAsync<JsonElement[]>("unlocks") ?? []);
			Assert.Equal(5, unlock.GetProperty("door").GetInt32());

			Assert.True((await _control.PostAsync("doors/5/close", new StringContent(""))).IsSuccessStatusCode);
			Assert.Equal(DoorStatusCode.Closed, await client.CheckDoorSingle(5, timeout, CancellationToken.None));
		}

		[LinuxFact]
		public async Task Serial_lock_controller_module_off_does_not_answer()
		{
			var client = new LockControllerClientSerial(Port(_engine!.LockControllerPort));
			Assert.True((await _control.PostAsync("modules/4/off", new StringContent(""))).IsSuccessStatusCode);

			Assert.Null(await client.CheckDoorSingle(30, TimeSpan.FromMilliseconds(500), CancellationToken.None));
			Assert.Equal(DoorStatusCode.Closed, await client.CheckDoorSingle(5, TimeSpan.FromSeconds(3), CancellationToken.None));
		}

		[LinuxFact]
		public async Task Scanner_answers_presence_check_and_sends_barcode()
		{
			var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
			var client = new BarcodeScannerClient(new KioskScannerConfiguration { ComPort = Port(_engine!.BarcodeScannerPort) },
				barcode => { received.TrySetResult(barcode); return ValueTask.FromResult(true); }, logger: null, CancellationToken.None);

			Assert.True(await client.CheckPresence(CancellationToken.None));

			client.AcceptBarcodes();
			await Task.Delay(200);	// kiosk čeka barkodove (čitanje je pokrenuto)
			var response = await _control.PostAsJsonAsync("barcode", new { barcode = "PKT00001" });
			Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

			Assert.Equal("PKT00001", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
			client.EnterCommandMode();
		}

		[LinuxFact]
		public async Task Pos_control_api_reports_state_and_reset_restores_defaults()
		{
			var state = await _control.GetFromJsonAsync<JsonElement>("pos");
			Assert.Equal("Idle", state.GetProperty("phase").GetString());

			var settings = await _control.PostAsJsonAsync("pos/settings", new { autoCustomer = true, autoCard = "VisaChip" });
			Assert.True(settings.IsSuccessStatusCode);
			Assert.True((await _control.PostAsJsonAsync("pos/faults", new { fault = "NoResponse", count = 2 })).IsSuccessStatusCode);
			// kupac ne može da prinese karticu dok terminal ne čeka karticu
			Assert.Equal(System.Net.HttpStatusCode.Conflict, (await _control.PostAsJsonAsync("pos/card", new { card = "VisaChip" })).StatusCode);

			Assert.True((await _control.PostAsync("reset", new StringContent(""))).IsSuccessStatusCode);

			state = await _control.GetFromJsonAsync<JsonElement>("pos");
			Assert.False(state.GetProperty("autoCustomer").GetBoolean());
			Assert.Equal("MastercardContactless", state.GetProperty("autoCard").GetString());
			Assert.Empty(state.GetProperty("faults").EnumerateObject());
		}
	}

	/// <summary>Virtuelni par sam: bajtovi u oba smera, odbacivanje dok druga strana nije otvorena, symlink.</summary>
	[SupportedOSPlatform("linux")]
	public sealed class PtySerialPairTests : IDisposable
	{
		private readonly string _dir = Path.Combine(Path.GetTempPath(), $"toppos-pty-{Guid.NewGuid():N}");

		public void Dispose()
		{
			if (Directory.Exists(_dir)) {
				Directory.Delete(_dir, recursive: true);
			}
		}

		private static SerialPort Open(string name)
		{
			var port = new SerialPort(name, 9600, Parity.None, 8, StopBits.One) { ReadTimeout = 3000, WriteTimeout = 3000 };
			port.Open();
			return port;
		}

		private static byte[] Read(SerialPort port, int count)
		{
			var buffer = new byte[count];
			int read = 0;
			while (read < count) {
				read += port.Read(buffer, read, count - read);
			}
			return buffer;
		}

		[LinuxFact]
		public void Bytes_pass_unchanged_in_both_directions()
		{
			using var pair = new PtySerialPair(Path.Combine(_dir, "tty"), null);
			using var simulator = Open(pair.SimulatorPortName);
			using var kiosk = Open(pair.KioskPortName);
			Assert.True(pair.WaitForSimulator(TimeSpan.FromSeconds(5)));
			byte[] frame = [0x02, 0x00, 0x03, 0x0D, 0x0A, 0x11, 0x13, 0x7F, 0xFF, 0x1A, 0x04];

			kiosk.Write(frame, 0, frame.Length);
			Assert.Equal(frame, Read(simulator, frame.Length));

			simulator.Write(frame, 0, frame.Length);
			Assert.Equal(frame, Read(kiosk, frame.Length));
		}

		[LinuxFact]
		public void Bytes_sent_while_kiosk_port_is_closed_are_dropped()
		{
			using var pair = new PtySerialPair(Path.Combine(_dir, "tty"), null);
			using var simulator = Open(pair.SimulatorPortName);
			Assert.True(pair.WaitForSimulator(TimeSpan.FromSeconds(5)));

			simulator.Write([0x06, 0x06], 0, 2);	// niko ne sluša — kao kabl bez uređaja
			Thread.Sleep(300);
			using var kiosk = Open(pair.KioskPortName);
			Thread.Sleep(300);

			Assert.Equal(0, kiosk.BytesToRead);
			simulator.Write([0x15], 0, 1);
			Assert.Equal([0x15], Read(kiosk, 1));
		}

		[LinuxFact]
		public void Bytes_sent_just_before_kiosk_opens_are_not_delivered_late()
		{
			// Codex: pumpa vidi zatvoren kiosk i pauzira; simulator pošalje; kiosk otvori port — zaostali bajtovi ne smeju
			// da stignu posle otvaranja (ECR bi ih pročitao kao odgovor na sledeću komandu).
			using var pair = new PtySerialPair(Path.Combine(_dir, "tty"), null);
			using var simulator = Open(pair.SimulatorPortName);
			Assert.True(pair.WaitForSimulator(TimeSpan.FromSeconds(5)));	// strana simulatora je otvorena od starta (READY)
			for (int i = 0; i < 30; i++) {
				simulator.Write([0x06], 0, 1);
				using var kiosk = Open(pair.KioskPortName);
				Thread.Sleep(150);
				Assert.True(kiosk.BytesToRead == 0, $"pokušaj {i}: stiglo {kiosk.BytesToRead} B poslatih dok kiosk nije bio otvoren");
				kiosk.Close();
				Thread.Sleep(30);
			}
		}

		[LinuxFact]
		public void Symlink_is_removed_on_dispose_and_regular_file_is_not_replaced()
		{
			var link = Path.Combine(_dir, "tty");
			using (var pair = new PtySerialPair(link, null)) {
				Assert.NotNull(new FileInfo(link).LinkTarget);
			}
			Assert.False(File.Exists(link));

			File.WriteAllText(link, "nije port");
			Assert.Throws<IOException>(() => new PtySerialPair(link, null));
			Assert.Equal("nije port", File.ReadAllText(link));
		}
	}
}
