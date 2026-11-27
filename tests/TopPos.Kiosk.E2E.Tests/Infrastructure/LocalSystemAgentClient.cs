using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using TopPos.Agent.Contracts;
using Xunit;

namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>
	/// Agent kao privilegovan klijent: <c>TopPos.AgentPipe.TestClient</c> pokrenut kroz <c>psexec -s</c> (Windows, LocalSystem)
	/// ili <c>sudo -n</c> (Linux, root; odluka D4). Kiosk prima samo LocalSystem, odnosno root (doc/AgentCommunication.md §1,
	/// ugovor „Transport na Linuxu“ tačka 3), pa je ovo jedini način da E2E pošalje komandu Agenta pravom klijentu.
	/// <list type="bullet">
	/// <item>Windows: <c>psexec -s</c> radi samo iz procesa sa administratorskim pravima (instalira svoj servis); klijent je
	/// iz build izlaza (kao v2).</item>
	/// <item>Linux: klijent je kopija koju menja samo root, sa sudo pravilom bez lozinke samo za nju
	/// (<c>tools/install-e2e-agent-client.sh</c>; putanja u <see cref="LinuxClientVariable"/>). Svaki poziv pre slanja proverava
	/// kiosk kao Agent (tačka 4: PID, nalog, izvršni fajl, <c>boot_id</c> i <c>starttime</c> iz status.lock); pod sudo klijent
	/// ne čita ni ne piše fajlove po izboru pozivaoca, pa raspored ide u argumentu, a odgovor na standardni izlaz.</item>
	/// </list>
	/// Bez toga se test preskače. v4: ime klase i potpisi kao v2, da scenariji iz v2 prolaze bez izmena.
	/// </summary>
	public static class LocalSystemAgentClient
	{
		public const string LinuxClientVariable = "TOPPOS_E2E_AGENT_CLIENT";
		public const string LinuxDefaultClient = "/usr/local/lib/toppos-e2e/agent-client/TopPos.AgentPipe.TestClient";

		private static readonly TimeSpan __timeout = TimeSpan.FromSeconds(120);
		private const string ResultPrefix = "RESULT ";

		/// <summary>Preskače test kad privilegovan klijent nije dostupan (razlog u poruci).</summary>
		public static void RequireAvailable()
		{
			if (OperatingSystem.IsWindows()) {
				Skip.If(FindPsExec() == null, "psexec.exe nije u PATH-u (Sysinternals) — komanda Agenta pod LocalSystem-om se ne može poslati.");
				using var identity = WindowsIdentity.GetCurrent();
				Skip.IfNot(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
							"E2E nije pokrenut kao administrator — psexec -s (LocalSystem) to traži; pokrenuti run-e2e.cmd iz admin konzole.");
				return;
			}
			var client = LinuxClient;
			Skip.IfNot(File.Exists(client),
						$"test klijent za root ({client}) nije instaliran — pokrenuti tools/install-e2e-agent-client.sh (D4) ili postaviti {LinuxClientVariable}.");
			Skip.IfNot(SelfCheck(client), $"sudo -k -n {client} --self-check ne radi kao root bez lozinke — pokrenuti tools/install-e2e-agent-client.sh (D4).");
		}

		/// <summary>
		/// <c>--shutdown &lt;purpose&gt;</c> test klijenta kao Agent. Izlazni kod je kod test klijenta (psexec i sudo ga prenose):
		/// 0 Completed i proces kioska izašao, 5 komanda odbijena, 6 proces nije izašao 60 s posle Completed, 7 kiosk nije prošao
		/// proveru (Linux).
		/// </summary>
		public static Task<(int ExitCode, string Output)> ShutdownAsync(KioskSession kiosk, string pipeName, string purpose)
			=> RunAsync(kiosk, pipeName, ["--shutdown", purpose]);

		/// <summary>
		/// Komanda servisne sesije (<c>--remote-service-begin</c>, <c>--open-doors</c>, <c>--remote-service-end</c>,
		/// <c>--command-result</c> sa svojim argumentima) ili preuzimanja (<c>--recipient-pickup</c>). Odgovor kioska test klijent
		/// upisuje u fajl u sandbox-u (Windows; stariji PsExec ne prenosi ispis) ili ispisuje (Linux), pa se ne čita iz ostatka
		/// ispisa; null = odgovor nije stigao. Izlazni kod: 0 Completed ili InProgress, 5 komanda odbijena (razlog je u odgovoru).
		/// </summary>
		public static async Task<(int ExitCode, AgentResponse? Response)> RemoteServiceAsync(KioskSession kiosk, string pipeName, params string[] args)
		{
			AgentResponse? response;
			int exitCode;
			if (OperatingSystem.IsWindows()) {
				var directory = Path.Combine(kiosk.Directory, "agent");
				Directory.CreateDirectory(directory);
				var resultFile = Path.Combine(directory, $"response-{Guid.NewGuid():N}.json");
				(exitCode, _) = await RunAsync(kiosk, pipeName, [.. args, "--result-file", resultFile]).ConfigureAwait(false);
				response = File.Exists(resultFile) ? AgentJson.Deserialize<AgentResponse>(await File.ReadAllBytesAsync(resultFile).ConfigureAwait(false)) : null;
			}
			else {
				(exitCode, var output) = await RunAsync(kiosk, pipeName, [.. WithLayoutText(args), "--print-result"]).ConfigureAwait(false);
				var line = output.Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.StartsWith(ResultPrefix, StringComparison.Ordinal));
				response = line != null ? AgentJson.Deserialize<AgentResponse>(Encoding.UTF8.GetBytes(line[ResultPrefix.Length..])) : null;
			}
			kiosk.Trace(response == null ? "odgovor kioska nije stigao" : $"odgovor kioska: {response.ResultCode} — {response.Message}");
			return (exitCode, response);
		}

		// Pod sudo klijent ne čita fajl rasporeda: CSV iz sandbox-a (piše ga test) ide u argument.
		private static IEnumerable<string> WithLayoutText(string[] args)
		{
			for (int i = 0; i < args.Length; i++) {
				if (args[i] == "--layout" && i + 1 < args.Length) {
					yield return "--layout-text";
					yield return string.Join('|', File.ReadAllLines(args[++i]).Where(l => l.Trim().Length > 0));
				}
				else {
					yield return args[i];
				}
			}
		}

		private static async Task<(int ExitCode, string Output)> RunAsync(KioskSession kiosk, string address, IEnumerable<string> args)
		{
			ProcessStartInfo startInfo;
			List<string> arguments;
			if (OperatingSystem.IsWindows()) {
				var psexec = FindPsExec() ?? throw new E2EException("psexec.exe nije u PATH-u.");
				var testClient = Path.Combine(E2ESettings.Current.AgentClientOutput, "TopPos.AgentPipe.TestClient.exe");
				if (!File.Exists(testClient)) {
					throw new E2EException($"Test klijent nije build-ovan: {testClient}");
				}
				startInfo = new ProcessStartInfo(psexec);
				// -nobanner traži PsExec 2.x; v1.98 ga ne poznaje i samo ispiše uputstvo (izlaz -1).
				arguments = ["-accepteula", "-nobanner", "-s", testClient, "--pipe", address, .. args];
			}
			else {
				// Root proverava kiosk kao Agent: nalog E2E-a i izvršni fajl kioska iz sandbox-a umesto produkcionih.
				// -k: bez keširane lozinke — radi samo pravilo bez lozinke (kao u RequireAvailable).
				startInfo = new ProcessStartInfo("sudo");
				arguments = ["-k", "-n", LinuxClient, "--socket", address, "--status-lock", Path.Combine(kiosk.ClientDirectory, "status.lock"),
							 "--expect-uid", CurrentUid(), "--expect-exe", kiosk.ClientExecutable, .. args];
			}
			startInfo.UseShellExecute = false;
			startInfo.RedirectStandardOutput = true;
			startInfo.RedirectStandardError = true;
			startInfo.StandardOutputEncoding = Encoding.UTF8;
			foreach (var arg in arguments) {
				startInfo.ArgumentList.Add(arg);
			}
			kiosk.Trace($"{startInfo.FileName} {string.Join(" ", arguments)}");
			using var process = Process.Start(startInfo) ?? throw new E2EException($"{startInfo.FileName} se nije pokrenuo.");
			var stdout = process.StandardOutput.ReadToEndAsync();
			var stderr = process.StandardError.ReadToEndAsync();
			using (var cts = new CancellationTokenSource(__timeout)) {
				try {
					await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) {
					process.Kill(entireProcessTree: true);
					throw new E2EException($"{startInfo.FileName} nije završio za {__timeout.TotalSeconds:0} s.");
				}
			}
			var output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
			kiosk.Trace($"test klijent (Agent) izlaz {process.ExitCode}:{Environment.NewLine}{output.TrimEnd()}");
			return (process.ExitCode, output);
		}

		private static string LinuxClient => Environment.GetEnvironmentVariable(LinuxClientVariable) is { Length: > 0 } path ? path : LinuxDefaultClient;

		// Klijent kroz sudo bez keširane lozinke potvrđuje da radi kao root (izlaz 0), bez kanala ka kiosku.
		private static bool SelfCheck(string client)
		{
			try {
				using var process = Process.Start(new ProcessStartInfo("sudo", ["-k", "-n", client, "--self-check"]) {
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
				});
				if (process == null) {
					return false;
				}
				var output = Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
				if (!process.WaitForExit(30_000)) {
					process.Kill(entireProcessTree: true);
					return false;
				}
				output.Wait(5_000);
				return process.ExitCode == 0;
			}
			catch (Exception) {
				return false;
			}
		}

		// Efektivni uid procesa E2E-a (kiosk radi pod istim nalogom; SO_PEERCRED nosi efektivni uid).
		private static string CurrentUid() => File.ReadLines("/proc/self/status").First(l => l.StartsWith("Uid:", StringComparison.Ordinal))
															.Split('\t', StringSplitOptions.RemoveEmptyEntries)[2];

		private static string? FindPsExec()
			=> (Environment.GetEnvironmentVariable("PATH") ?? "")
				.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.SelectMany(dir => new[] { "PsExec64.exe", "PsExec.exe" }.Select(exe => Path.Combine(dir, exe)))
				.FirstOrDefault(File.Exists);
	}
}
