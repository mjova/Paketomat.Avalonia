using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Evidencija urednog gašenja u status.lock (doc/notes/status-lock-uredno-gasenje-plan.md): uredno gašenje upisuje
	/// CleanShutdown=true, a kill ostavlja false; sledeći start u log upisuje ishod prethodne sesije uz njeno poslednje Time,
	/// pa počinje novu sesiju sa false. Druga instanca, koja se odmah ugasi, fajl aktivne instance ne dira.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class ShutdownEvidenceTests(ITestOutputHelper output)
	{
		private const string StatusFile = "status.lock";
		private static readonly TimeSpan __exitTimeout = TimeSpan.FromSeconds(30);

		/// <summary>
		/// Nov sandbox (fajla nema) → restart zatvaranjem prozora ili kill-om → novi proces loguje uredno/nasilno gašenje sa vremenom
		/// poslednjeg upisa starog procesa (pročitanim posle njegovog izlaska), ne sa vremenom svog starta.
		/// </summary>
		[SkippableTheory]
		[InlineData(true)]
		[InlineData(false)]
		public Task Restart_LogsPreviousShutdownWithPreviousTime(bool hard)
			=> KioskSession.RunAsync($"{nameof(Restart_LogsPreviousShutdownWithPreviousTime)}-{(hard ? "kill" : "close")}", output, async kiosk => {
				Assert.NotEmpty(kiosk.Log.LinesAfter(0, "StatusSemaphore: status.lock ne postoji; nema podatka o prethodnom gašenju"));
				var first = ReadStatus(kiosk);
				Assert.False(CleanShutdown(first));
				Assert.Equal(kiosk.ClientProcessId, ProcessId(first));

				JsonNode? atExit = null;
				var logMark = kiosk.Log.Mark();
				await kiosk.RestartClientAsync(hard, afterExit: () => atExit = ReadStatus(kiosk));

				Assert.NotNull(atExit);
				Assert.Equal(!hard, CleanShutdown(atExit));
				string previousTime = DateTimeOffset.Parse(atExit["Time"]!.GetValue<string>(), CultureInfo.InvariantCulture).ToString("o");
				kiosk.Trace($"status.lock posle izlaska: CleanShutdown={CleanShutdown(atExit)}, Time={previousTime}");
				if (hard) {
					await kiosk.WaitForLogLineAsync($"StatusSemaphore: program je nasilno isključen posle {previousTime}", logMark);
					Assert.Empty(kiosk.Log.LinesAfter(logMark, "StatusSemaphore: prethodna sesija je uredno"));
				}
				else {
					Assert.NotEmpty(kiosk.Log.LinesAfter(logMark, "StatusSemaphore: uredno gašenje zabeleženo"));
					await kiosk.WaitForLogLineAsync($"StatusSemaphore: prethodna sesija je uredno isključena. Poslednji upis: {previousTime}", logMark);
					Assert.Empty(kiosk.Log.LinesAfter(logMark, "StatusSemaphore: program je nasilno"));
				}
				// Završni upis zadržava izjavu starog procesa; nova sesija ponovo počinje bez potvrde urednog gašenja, sa svojom.
				Assert.Equal(ProcessId(first), ProcessId(atExit));
				Assert.Equal(ProcessStartUtc(first), ProcessStartUtc(atExit));
				var second = ReadStatus(kiosk);
				Assert.False(CleanShutdown(second));
				Assert.Equal(kiosk.ClientProcessId, ProcessId(second));
				Assert.NotEqual(ProcessStartUtc(first), ProcessStartUtc(second));
			});

		/// <summary>
		/// Odjava ili gašenje Windows-a: klijent završi gašenje (kontroler, završni upis) pre odgovora na WM_QUERYENDSESSION, jer
		/// posle WM_ENDSESSION Windows proces gasi bez čekanja. Ranije je gašenje počinjalo tek posle odgovora i prekidalo se:
		/// ostajali su status.lock.tmp i status.lock bez CleanShutdown (KIOSK1, odjave msi 24.10.2026).
		/// </summary>
		[PendingFlow("kraj Windows sesije", "F7")]	// v4
		public Task WindowsSessionEnd_CompletesShutdownBeforeAnswering()
			=> KioskSession.RunAsync(nameof(WindowsSessionEnd_CompletesShutdownBeforeAnswering), output, async kiosk => {
				int processId = kiosk.ClientProcessId;
				var mark = kiosk.Log.Mark();

				var answers = await kiosk.EndWindowsSessionAsync();

				Assert.NotEmpty(answers);
				Assert.All(answers, Assert.True);
				int sessionEnding = SingleLine(kiosk, mark, "Kraj Windows sesije (Logoff)");
				int controllerClosing = SingleLine(kiosk, mark, "Izlazak iz aplikacije: zatvaranje veze sa kontrolerom vrata.");
				int controllerClosed = SingleLine(kiosk, mark, "LockControllerShutdown: veza sa kontrolerom vrata je zatvorena");
				int cleanShutdown = SingleLine(kiosk, mark, "StatusSemaphore: uredno gašenje zabeleženo");
				Assert.True(sessionEnding < controllerClosing && controllerClosing < controllerClosed && controllerClosed < cleanShutdown,
							$"redosled u logu: kraj sesije {sessionEnding}, kontroler {controllerClosing}–{controllerClosed}, završni upis {cleanShutdown}");
				kiosk.Trace($"gašenje: kraj sesije → završni upis {(kiosk.Log.TimeOf(cleanShutdown) - kiosk.Log.TimeOf(sessionEnding)).TotalMilliseconds:0} ms");

				var status = ReadStatus(kiosk);
				Assert.True(CleanShutdown(status));
				Assert.Equal(processId, ProcessId(status));
				Assert.False(File.Exists(Path.Combine(kiosk.ClientDirectory, StatusFile + ".tmp")), $"{StatusFile}.tmp ne sme da ostane");
			});

		/// <summary>
		/// WM_QUERYENDSESSION dok izlazak iz programa (zatvaranje prozora, ShutdownApplication) čeka kontroler: dispečer poruku
		/// obradi usred čekanja. Odgovor sme da stigne tek posle završnog upisa, jer Windows posle njega gasi proces (ovde Kill).
		/// Kontroler odgovara za 900 ms (rok klijenta je 1 s), a provera vrata radi često: zatvaranje prozora odmah posle slanja
		/// komande zatiče je u toku, pa čekanje traje dovoljno da poruka stigne usred njega.
		/// </summary>
		[PendingFlow("kraj Windows sesije", "F7")]	// v4
		public Task WindowsSessionEndDuringExit_AnswersAfterCleanShutdown()
			=> KioskSession.RunAsync(nameof(WindowsSessionEndDuringExit_AnswersAfterCleanShutdown), output, SlowController, async kiosk => {
				int processId = kiosk.ClientProcessId;
				await kiosk.WaitForLogLineAsync("SendCommandAsync: TX >", kiosk.Log.Mark());
				var mark = kiosk.Log.Mark();

				kiosk.CloseClientWindow();
				await kiosk.WaitForLogLineAsync("Izlazak iz aplikacije: zatvaranje veze sa kontrolerom vrata.", mark);
				var answers = await kiosk.EndWindowsSessionAsync();

				Assert.NotEmpty(answers);
				Assert.All(answers, Assert.True);
				// Bez poruke usred čekanja test ne bi ništa dokazao.
				int controllerClosing = SingleLine(kiosk, mark, "Izlazak iz aplikacije: zatvaranje veze sa kontrolerom vrata.");
				int sessionEnding = SingleLine(kiosk, mark, "Kraj Windows sesije (Logoff)");
				int controllerClosed = SingleLine(kiosk, mark, "LockControllerShutdown: veza sa kontrolerom vrata je zatvorena");
				int cleanShutdown = SingleLine(kiosk, mark, "StatusSemaphore: uredno gašenje zabeleženo");
				Assert.True(controllerClosing < sessionEnding && sessionEnding < controllerClosed && controllerClosed < cleanShutdown,
							$"redosled u logu: kontroler {controllerClosing}–{controllerClosed}, kraj sesije {sessionEnding}, završni upis {cleanShutdown}");

				var status = ReadStatus(kiosk);
				Assert.True(CleanShutdown(status));
				Assert.Equal(processId, ProcessId(status));
				Assert.False(File.Exists(Path.Combine(kiosk.ClientDirectory, StatusFile + ".tmp")), $"{StatusFile}.tmp ne sme da ostane");
			});

		private static KioskOptions SlowController { get; } = new() {
			ConfigureClient = c => c["Locker"]!["DoorWatcherFrequency"] = 1000,
			ConfigureLocker = c => c["LockController"]!["NetworkModule"] = new JsonObject { ["ResponseDelayMs"] = 900 },
		};

		/// <summary>
		/// Druga instanca klijenta se odmah gasi (single-instance mutex) i ne čita, ne prepisuje i ne označava kao uredno
		/// ugašen status.lock aktivne instance. Poslednji korak testa: druga instanca ne može da otvori dnevni log aktivne,
		/// pa Serilog piše u nov fajl i ClientLog posle toga prati njega.
		/// </summary>
		[SkippableFact]
		public Task SecondInstance_DoesNotTouchStatusLock()
			=> KioskSession.RunAsync(nameof(SecondInstance_DoesNotTouchStatusLock), output, async kiosk => {
				var before = ReadStatusBytes(kiosk);

				using var second = Process.Start(new ProcessStartInfo(kiosk.ClientExecutable) {
										WorkingDirectory = kiosk.ClientDirectory,
										UseShellExecute = false,
									}) ?? throw new E2EException("Pokretanje druge instance nije uspelo");
				kiosk.Trace($"druga instanca pokrenuta (pid {second.Id})");
				using (var cts = new CancellationTokenSource(__exitTimeout)) {
					try {
						await second.WaitForExitAsync(cts.Token);
					}
					catch (OperationCanceledException) {
						second.Kill(entireProcessTree: true);
						throw new E2EException($"Druga instanca nije izašla za {__exitTimeout.TotalSeconds:0} s.");
					}
				}
				kiosk.Trace($"druga instanca izašla (exit code {second.ExitCode})");

				kiosk.EnsureClientAlive();
				Assert.Equal(before, ReadStatusBytes(kiosk));
				var status = ReadStatus(kiosk);
				Assert.False(CleanShutdown(status));
				Assert.Equal(kiosk.ClientProcessId, ProcessId(status));
			});

		private static int SingleLine(KioskSession kiosk, int mark, string text)
		{
			Assert.Single(kiosk.Log.LinesAfter(mark, text));
			return kiosk.Log.FirstLineAfter(mark, text)!.Value;
		}

		private static bool CleanShutdown(JsonNode status) => status["CleanShutdown"]!.GetValue<bool>();

		private static int ProcessId(JsonNode status) => status["ProcessId"]!.GetValue<int>();

		private static string ProcessStartUtc(JsonNode status) => status["ProcessStartUtc"]!.GetValue<string>();

		private static JsonNode ReadStatus(KioskSession kiosk)
			=> JsonNode.Parse(ReadStatusBytes(kiosk)) ?? throw new E2EException($"{StatusFile} je prazan JSON (null).");

		/// <summary>Sadržaj status.lock; čita se uz FileShare.Delete, da čitanje ne obori klijentovu zamenu fajla.</summary>
		private static byte[] ReadStatusBytes(KioskSession kiosk)
		{
			using var stream = new FileStream(Path.Combine(kiosk.ClientDirectory, StatusFile), FileMode.Open, FileAccess.Read,
												FileShare.ReadWrite | FileShare.Delete);
			var buffer = new MemoryStream();
			stream.CopyTo(buffer);
			return buffer.ToArray();
		}
	}
}
