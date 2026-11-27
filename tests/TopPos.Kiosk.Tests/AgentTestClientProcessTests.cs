using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.AgentCommunication;
using TopPos.Kiosk.Workflow;
using TopPos.Platform;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// v4: test klijent (<c>tools/TopPos.AgentPipe.TestClient</c>) kao zaseban proces protiv servera kanala u ovom procesu.
	/// Na Linuxu klijent proverava server kao Agent (ugovor „Transport na Linuxu“, tačka 4) iz izjave koju piše
	/// <see cref="StatusSemaphore"/>: <c>SO_PEERCRED</c> drugog procesa, <c>/proc/&lt;pid&gt;</c> i <c>boot_id</c> se tako proveravaju
	/// van koda kioska. Dozvoljeni klijent je tekući nalog (kao u <see cref="AgentPipeServerTests"/>); root i LocalSystem su E2E.
	/// </summary>
	public sealed class AgentTestClientProcessTests : IDisposable
	{
		private readonly string _root = Path.Combine(Path.GetTempPath(), $"toppos-client-{Guid.NewGuid():N}"[..22]);

		public AgentTestClientProcessTests() => Directory.CreateDirectory(_root);

		public void Dispose() => Directory.Delete(_root, recursive: true);

		private string Address => OperatingSystem.IsWindows() ? $"TopPos.Test.{Guid.NewGuid():N}" : Path.Combine(_root, "run", "agent.v1.sock");

		private string StatusLock => Path.Combine(_root, "status.lock");

		[Fact]
		public async Task Gets_status_from_the_server_in_another_process()
		{
			var address = Address;
			await using var server = StartServer(address);
			var args = OperatingSystem.IsWindows()
						? new List<string> { "--pipe", address }
						: ["--socket", address, "--status-lock", StatusLock, "--expect-uid", CurrentUid(), "--expect-exe", CurrentExecutable()];

			var (exitCode, output) = await RunClientAsync(args);

			Assert.True(exitCode == 0, output);
			Assert.Contains($"Server: PID {Environment.ProcessId}", output);
			Assert.Contains("Hello: ", output);
			Assert.Contains("\"resultCode\":\"Completed\"", output);
			if (OperatingSystem.IsLinux()) {
				Assert.Contains("Server proveren", output);
			}
		}

		[Fact]
		public async Task Rejected_command_gives_exit_code_5()
		{
			var address = Address;
			await using var server = StartServer(address);

			var (exitCode, output) = await RunClientAsync([OperatingSystem.IsWindows() ? "--pipe" : "--socket", address, "--command", "NepoznataKomanda"]);

			Assert.True(exitCode == 5, output);
			Assert.Contains("Komanda odbijena: ", output);
		}

		[LinuxFact]
		public async Task Refuses_server_that_does_not_match_the_declaration_before_sending_anything()
		{
			// Lažni server (ovaj proces) beleži šta mu klijent pošalje; izjava je prava izjava ovog procesa.
			var address = Address;
			Directory.CreateDirectory(Path.GetDirectoryName(address)!);
			using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
			listener.Bind(new UnixDomainSocketEndPoint(address));
			listener.Listen(8);
			WriteDeclaration(new AgentChannelAddress(null, address));
			var declared = JsonNode.Parse(await File.ReadAllTextAsync(StatusLock))!;
			string[] expectProcess = ["--expect-uid", CurrentUid(), "--expect-exe", CurrentExecutable()];
			var cases = new (string Name, Func<string, Task> Write, string[] Extra, string Error)[] {
				("ProcessId", Changed(declared, d => d["ProcessId"] = Environment.ProcessId + 1), expectProcess, "PID servera"),
				("ProcessId van Int32", Changed(declared, d => d["ProcessId"] = (1L << 32) + Environment.ProcessId), expectProcess, "PID servera"),
				("ProcessStartTicks", Changed(declared, d => d["ProcessStartTicks"] = (long) d["ProcessStartTicks"]! + 1), expectProcess, "starttime servera"),
				("ProcessStartTicks decimalan", Changed(declared, d => d["ProcessStartTicks"] = (long) d["ProcessStartTicks"]! + 0.5), expectProcess, "starttime servera"),
				("ProcessBootId", Changed(declared, d => d["ProcessBootId"] = Guid.NewGuid().ToString()), expectProcess, "boot_id "),
				("SocketPath", Changed(declared, d => d["AgentChannel"]!["SocketPath"] = address + ".x"), expectProcess, "socket "),
				("uid", Changed(declared, _ => { }), ["--expect-uid", (uint.Parse(CurrentUid()) + 1).ToString(), "--expect-exe", CurrentExecutable()], "uid servera"),
				("exe", Changed(declared, _ => { }), ["--expect-uid", CurrentUid(), "--expect-exe", "/bin/sh"], "exe servera"),
				// Bez --expect-exe: izvršni fajl izdanja (/opt/toppos-kiosk/current/TopPos.Kiosk), ne ovaj proces.
				("exe izdanja", Changed(declared, _ => { }), ["--expect-uid", CurrentUid()], "exe servera"),
				("koren niz", path => File.WriteAllTextAsync(path, "[]"), expectProcess, "izjava "),
				("link", path => { File.CreateSymbolicLink(path, StatusLock); return Task.CompletedTask; }, expectProcess, "izjava "),
				("FIFO", path => { using var mkfifo = Process.Start("mkfifo", [path]); mkfifo.WaitForExit(); return Task.CompletedTask; }, expectProcess, "nije običan fajl"),
				("folder", path => { Directory.CreateDirectory(path); return Task.CompletedTask; }, expectProcess, "izjava "),
			};

			foreach (var (name, write, extra, error) in cases) {
				var file = Path.Combine(_root, $"status-{cases.ToList().FindIndex(c => c.Name == name)}.lock");
				await write(file);

				var (exitCode, output) = await RunClientAsync(["--socket", address, "--status-lock", file, .. extra]);

				Assert.True(exitCode == 7, $"{name}: {output}");
				Assert.Contains("Server NIJE proveren: ", output);
				Assert.Contains(error, output);
				using var accepted = await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));
				var received = await accepted.ReceiveAsync(new byte[64], SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(5));
				Assert.True(received == 0, $"{name}: klijent je poslao {received} bajtova pre provere servera");
			}
		}

		[WindowsFact("na Linuxu se server proverava (Refuses_server_that_does_not_match_the_declaration_before_sending_anything)")]
		public async Task Server_check_is_refused_on_windows_instead_of_being_skipped()
		{
			var (exitCode, output) = await RunClientAsync(["--pipe", $"TopPos.Test.{Guid.NewGuid():N}", "--status-lock", StatusLock]);

			Assert.Equal(1, exitCode);
			Assert.Contains("--status-lock postoji samo na Linuxu.", output);
		}

		private static Func<string, Task> Changed(JsonNode declared, Action<JsonNode> change)
			=> path => {
				var declaration = declared.DeepClone();
				change(declaration);
				return File.WriteAllTextAsync(path, declaration.ToJsonString());
			};

		// Server kanala i izjava ovog procesa u status.lock, kao kiosk (StatusSemaphore piše isto što i u aplikaciji).
		private AgentPipeServer StartServer(string address)
		{
			var source = new KioskStatusSource("112", logger: null);
			source.SetLifecycle(KioskLifecycles.Ready);
			source.SetOperational(true);
			source.SetWorkflowState(WorkflowState.Start);
			var server = new AgentPipeServer(new AgentPipeServerOptions { PipeName = address, SocketPath = address, AllowCurrentUser = true },
											source, _ => Task.FromResult(true), logger: null);
			Assert.True(server.Start());
			WriteDeclaration(OperatingSystem.IsWindows() ? new AgentChannelAddress(address, null) : new AgentChannelAddress(null, address));
			return server;
		}

		private void WriteDeclaration(AgentChannelAddress channel)
			=> new StatusSemaphore(_root, logger: null, StatusDeclaration.ForCurrentProcess(channel, logger: null)).InitializeSession();

		// Klijent iz izlaza svog projekta (ista konfiguracija i TFM kao testovi), preko dotnet host-a koji pokreće testove.
		private static async Task<(int ExitCode, string Output)> RunClientAsync(IEnumerable<string> args)
		{
			var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
			var repository = output.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
			var client = Path.Combine(repository, "tools", "TopPos.AgentPipe.TestClient", "bin", output.Parent.Name, output.Name, "TopPos.AgentPipe.TestClient.dll");
			Assert.True(File.Exists(client), $"Test klijent nije build-ovan: {client}");
			var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			startInfo.ArgumentList.Add(client);
			foreach (var arg in args) {
				startInfo.ArgumentList.Add(arg);
			}
			using var process = Process.Start(startInfo)!;
			var stdout = process.StandardOutput.ReadToEndAsync();
			var stderr = process.StandardError.ReadToEndAsync();
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
			try {
				await process.WaitForExitAsync(timeout.Token);
			}
			catch (OperationCanceledException) {
				process.Kill(entireProcessTree: true);
				throw;
			}
			return (process.ExitCode, await stdout + await stderr);
		}

		private static string CurrentUid() => File.ReadLines("/proc/self/status").First(l => l.StartsWith("Uid:", StringComparison.Ordinal))
															.Split('\t', StringSplitOptions.RemoveEmptyEntries)[2];

		private static string CurrentExecutable() => new FileInfo("/proc/self/exe").LinkTarget!;
	}
}
