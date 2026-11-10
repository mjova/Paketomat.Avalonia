using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace TopPos.Simulators.Tests
{
	/// <summary>
	/// Proces <c>TopPos.Simulators</c> kako ga pokreće E2E harness: READY red sa stvarnim portovima, gašenje (SIGTERM, kraj
	/// roditelja), izlazni kodovi i ponovni start na istom portu posle gašenja sa otvorenom vezom kioska.
	/// </summary>
	public sealed class HostProcessTests : IDisposable
	{
		private static readonly TimeSpan __startTimeout = TimeSpan.FromSeconds(30);
		private static readonly TimeSpan __exitTimeout = TimeSpan.FromSeconds(15);

		private readonly string _dir = Directory.CreateTempSubdirectory("toppos-sim-").FullName;
		private readonly List<SimProcess> _processes = [];

		public void Dispose()
		{
			foreach (var process in _processes) {
				process.Dispose();
			}
			try {
				Directory.Delete(_dir, recursive: true);
			}
			catch (IOException) { }
		}

		[Fact]
		public async Task Both_report_ready_ports_and_answer_health()
		{
			var sim = Start("both", "--server-config", ServerConfig(), "--locker-config", LockerConfig(tcpPort: 0));
			var ready = await sim.WaitReady();

			using var http = new HttpClient();
			var serverPort = ready.GetProperty("server").GetProperty("port").GetInt32();
			var locker = ready.GetProperty("locker");
			var server = await http.GetFromJsonAsync<JsonElement>($"http://localhost:{serverPort}/_sim/health");
			Assert.Equal(serverPort, server.GetProperty("port").GetInt32());
			var lockerHealth = await http.GetFromJsonAsync<JsonElement>($"http://localhost:{locker.GetProperty("controlApiPort").GetInt32()}/_sim/health");
			Assert.Equal(locker.GetProperty("tcpPort").GetInt32(), lockerHealth.GetProperty("tcpPort").GetInt32());

			using var tcp = new TcpClient();
			await tcp.ConnectAsync(IPAddress.Loopback, locker.GetProperty("tcpPort").GetInt32());
		}

		[LinuxFact]
		public async Task Sigterm_shuts_down_with_exit_code_0()
		{
			var sim = Start("both", "--server-config", ServerConfig(), "--locker-config", LockerConfig(tcpPort: 0));
			await sim.WaitReady();

			sim.Terminate();

			Assert.Equal(0, await sim.WaitExit());
		}

		[Fact]
		public async Task Exits_when_parent_process_ends()
		{
			var parent = Start("server", "--config", ServerConfig());
			await parent.WaitReady();
			var child = Start("locker", "--config", LockerConfig(tcpPort: 0), "--parent-pid", parent.Id.ToString());
			await child.WaitReady();

			parent.Kill();

			Assert.Equal(0, await child.WaitExit());
		}

		[Fact]
		public async Task Busy_port_exits_with_code_2()
		{
			using var occupied = new TcpListener(IPAddress.Loopback, 0);
			occupied.Start();
			var port = ((IPEndPoint)occupied.LocalEndpoint).Port;

			var sim = Start("locker", "--config", LockerConfig(tcpPort: port));

			Assert.Equal(2, await sim.WaitExit());
		}

		[Theory]
		[InlineData()]
		[InlineData("nepoznato")]
		[InlineData("both", "--server-config", "server.json")]
		public async Task Bad_arguments_exit_with_code_1(params string[] args)
		{
			Assert.Equal(1, await Start(args).WaitExit());
		}

		[Fact]
		public async Task Invalid_configuration_exits_with_code_1()
		{
			// Codex: prazna ili semantički neispravna konfiguracija ne sme da obori host neobrađenim izuzetkom
			Assert.Equal(1, await Start("locker", "--config", Write("empty.json", new { })).WaitExit());
			Assert.Equal(1, await Start("server", "--config", Write("bad-port.json", new { Server = new { Port = 70000 } })).WaitExit());
			Assert.Equal(1, await Start("server", "--config", Path.Combine(_dir, "nema.json")).WaitExit());
		}

		[Fact]
		public async Task Restarts_on_same_port_after_shutdown_with_open_connection()
		{
			// Kiosk drži vezu ka NCU24L; gašenje simulatora je zatvara sa njegove strane (TIME_WAIT na portu simulatora).
			// Novi simulator na istom portu mora da krene — E2E ponovo pokreće simulatore na istim portovima.
			using var probe = new TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			var port = ((IPEndPoint)probe.LocalEndpoint).Port;
			probe.Stop();
			var config = LockerConfig(tcpPort: port);

			var first = Start("locker", "--config", config);
			await first.WaitReady();
			using (var kiosk = new TcpClient()) {
				await kiosk.ConnectAsync(IPAddress.Loopback, port);
				await Task.Delay(200);
				first.Terminate();
				await first.WaitExit();
			}

			var second = Start("locker", "--config", config);
			var ready = await second.WaitReady();
			Assert.Equal(port, ready.GetProperty("locker").GetProperty("tcpPort").GetInt32());
		}

		// ─── Pomoćno ───

		private string ServerConfig()
			=> Write("server.json", new {
				Server = new { Port = 0, PathPrefix = "/Api", BindAddress = "localhost", EnableControlApi = true },
				DefaultCourierCode = "K001",
				CourierQrCodes = Array.Empty<string>(),
				PaketomatId = "111",
				DoorCount = 48,
			});

		private string LockerConfig(int tcpPort)
			=> Write($"locker-{tcpPort}.json", new {
				Locker = new { ModuleCount = 2, BoxCountPerModule = 24 },
				LockController = new {
					Type = "NCU24L",
					TcpController = new { IpAddress = "127.0.0.1", Port = tcpPort },
					ListenAddress = "127.0.0.1",
					NetworkModule = new { ReleaseDelayMs = 100, LockAfterAbandonedMs = 0 },
				},
				ControlApi = new { Port = 0 },
			});

		private string Write(string name, object content)
		{
			var path = Path.Combine(_dir, name);
			File.WriteAllText(path, JsonSerializer.Serialize(content));
			return path;
		}

		private SimProcess Start(params string[] args)
		{
			var process = new SimProcess(_dir, args);
			_processes.Add(process);
			return process;
		}

		private sealed class SimProcess : IDisposable
		{
			private readonly Process _process;
			private readonly TaskCompletionSource<JsonElement> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
			private readonly List<string> _output = [];

			public SimProcess(string workingDirectory, string[] args)
			{
				var info = new ProcessStartInfo("dotnet") {
					WorkingDirectory = workingDirectory,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
				};
				info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TopPos.Simulators.dll"));
				foreach (var arg in args) {
					info.ArgumentList.Add(arg);
				}
				_process = new Process { StartInfo = info };
				_process.OutputDataReceived += (_, e) => OnLine(e.Data, stdout: true);
				_process.ErrorDataReceived += (_, e) => OnLine(e.Data, stdout: false);
				_process.Start();
				_process.BeginOutputReadLine();
				_process.BeginErrorReadLine();
			}

			public int Id => _process.Id;

			private void OnLine(string? line, bool stdout)
			{
				if (line == null) {
					return;
				}
				lock (_output) {
					_output.Add(line);
				}
				if (stdout && line.StartsWith("READY ", StringComparison.Ordinal)) {
					_ready.TrySetResult(JsonSerializer.Deserialize<JsonElement>(line["READY ".Length..]));
				}
			}

			private string Output()
			{
				lock (_output) {
					return string.Join('\n', _output);
				}
			}

			public async Task<JsonElement> WaitReady()
			{
				var exited = _process.WaitForExitAsync();
				var done = await Task.WhenAny(_ready.Task, exited, Task.Delay(__startTimeout));
				Assert.True(done == _ready.Task, $"Simulator nije javio READY.\n{Output()}");
				return await _ready.Task;
			}

			public async Task<int> WaitExit()
			{
				using var timeout = new CancellationTokenSource(__exitTimeout);
				try {
					await _process.WaitForExitAsync(timeout.Token);
				}
				catch (OperationCanceledException) {
					Assert.Fail($"Simulator se nije ugasio za {__exitTimeout.TotalSeconds} s.\n{Output()}");
				}
				return _process.ExitCode;
			}

			/// <summary>Linux: SIGTERM (uredno gašenje). Windows: konzolni proces bez prozora ne prima Ctrl+C — gasi se nasilno.</summary>
			public void Terminate()
			{
				if (OperatingSystem.IsLinux()) {
					Assert.Equal(0, kill(_process.Id, __sigterm));
				}
				else {
					Kill();
				}
			}

			public void Kill() => _process.Kill(entireProcessTree: true);

			public void Dispose()
			{
				try {
					if (!_process.HasExited) {
						_process.Kill(entireProcessTree: true);
						_process.WaitForExit(5000);
					}
				}
				catch (InvalidOperationException) { }
				_process.Dispose();
			}

			private const int __sigterm = 15;

			[DllImport("libc", SetLastError = true)]
			private static extern int kill(int pid, int sig);
		}
	}
}
