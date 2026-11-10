using System.Text.Json.Nodes;
using TopPos.Client.WPF.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Statusi vrata koje treba javiti serveru (ServerStatus.json, LockerStatusUpdater) i restart klijenta — vault: backlog/E2E
	/// — red potvrda plaćanja i restart klijenta (plan testa), scenario 7. Greška vrata koju server nije primio čeka u fajlu
	/// i posle restarta stiže serveru.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class DoorStatusPersistenceTests(ITestOutputHelper output)
	{
		private const string StatusFile = "ServerStatus.json";
		private static readonly TimeSpan __statusTimeout = TimeSpan.FromSeconds(20);

		/// <summary>
		/// Scenario 7: blokirana vrata → greška „N“ (zatvorena, a trebalo je otvoriti) → server odgovara 503, pa greška ostaje
		/// u ServerStatus.json → restart (pad procesa i uredno gašenje) → server proradi → prijava stiže posle restarta, a fajl
		/// je beleži kao poslatu. Kontroler posle restarta javlja samo zatvorena vrata — za grešku zna jedino fajl.
		/// </summary>
		[SkippableTheory]
		[InlineData(true)]
		[InlineData(false)]
		public Task RestartWhileDoorErrorPending_ErrorSentAfterRestart(bool hard)
			=> KioskSession.RunAsync($"{nameof(RestartWhileDoorErrorPending_ErrorSentAfterRestart)}-{(hard ? "kill" : "close")}", output, async kiosk => {
				var package = kiosk.Data.RecipientPackage();
				int door = package.BrojPozicije;
				// Pri startu klijent očita sva vrata i javi ih serveru kao ispravna (PonistavanjeGreskePozicije) — to stanje je
				// zapamćeno u fajlu i ono je ono što greška mora da nadjača.
				await WaitForStatusFileAsync(kiosk, s => FindDoor(s, "ServerDoorStatuses", door) is { } d && !IsFailed(d), $"vrata {door} ispravna na serveru");
				await kiosk.Server.AddPackageAsync(package);
				await kiosk.Locker.BlockDoorAsync(door);
				// count 0: Polly u klijentu ponavlja 5xx, a petlja statusa šalje ponovo svake sekunde.
				await kiosk.Server.ArmFaultAsync("PrijavaGreskePozicije", status: 503, count: 0);
				var serverMark = await kiosk.Server.MarkAsync();

				await kiosk.StartRecipientPickupAsync();
				var mark = await kiosk.EnterCodeAndPhoneAsync(package);
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(30));
				Assert.Equal("RecipientPickupDoorActionFailed", toStart.Trigger);
				await kiosk.Ui.WaitForPageAsync("StartPage");

				await WaitForDoorErrorCallAsync(kiosk, serverMark, door, statusCode: 503);
				var pending = await WaitForStatusFileAsync(kiosk, s => FindDoor(s, "PendingDoorStatuses", door) != null, $"greška vrata {door} čeka slanje");
				Assert.True(IsFailed(FindDoor(pending, "PendingDoorStatuses", door)!));
				Assert.False(IsFailed(FindDoor(pending, "ServerDoorStatuses", door)!));
				kiosk.Trace($"greška vrata {door} u {StatusFile}");

				// Restart: pri startu klijent ponovo očita vrata — zatvorena, bez greške, isto kao poslednje stanje na serveru.
				// Greška iz fajla to očitavanje mora da preživi. Stari proces u praznom hodu i dalje očitava vrata, pa se čeka od
				// prvog reda novog procesa: kraj očitavanja modula sa tim vratima, pa upis koji ga prati (DoorWatcher posle
				// modula poziva StatusUpdateHandler → UpdateStatuses → WriteStatuses, pre sledećeg modula; prvo očitavanje posle
				// starta javlja sva vrata, jer nema prethodnog).
				int module = (await kiosk.Locker.GetDoorAsync(door)).Module;
				var logMark = kiosk.Log.Mark();
				await kiosk.RestartClientAsync(hard);
				var restartMark = await kiosk.Server.MarkAsync();
				int newProcess = kiosk.Log.FirstLineAfter(logMark, "LockerStatusUpdater: starting processing loop")
									?? throw new E2EException("Novi proces klijenta nije pokrenuo LockerStatusUpdater (nema linije u logu).");
				string moduleScanned = $"provera statusa modula ({module}) zavrsena";
				await kiosk.WaitForLogLineAsync(moduleScanned, newProcess);
				await kiosk.WaitForLogLineAsync("WriteStatuses: snimljeni statusi", kiosk.Log.FirstLineAfter(newProcess, moduleScanned)!.Value);
				var afterRestart = FindDoor(ReadStatusFile(kiosk)!, "PendingDoorStatuses", door);
				Assert.True(afterRestart != null && IsFailed(afterRestart), $"Greška vrata {door} izgubljena posle restarta (očitavanje vrata pri startu).");

				await kiosk.Server.ClearFaultsAsync();
				kiosk.Trace("greške servera obrisane");
				var calls = await WaitForDoorErrorCallAsync(kiosk, restartMark, door, statusCode: 200);
				Assert.Equal("N", calls.Last(c => IsDoorError(c, door)).RequestValue("GreskaPozicije"));

				var sent = await WaitForStatusFileAsync(kiosk, s => FindDoor(s, "PendingDoorStatuses", door) == null, $"greška vrata {door} poslata");
				Assert.True(FindDoor(sent, "ServerDoorStatuses", door) is { } onServer && IsFailed(onServer), "Poslata greška mora ostati zapamćena kao stanje na serveru.");
				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "PonistavanjeGreskePozicije" && c.RequestValue("BrojPozicije") == door.ToString());
			});

		/// <summary>
		/// Nečitljiv ServerStatus.json pri startu: klijent radi normalno (Start ekran), a u logu je greška čitanja. Fajl se ne
		/// čuva sa strane kao fajl potvrda plaćanja — prvi sledeći upis ga zameni ispravnim. Gubitak nije kritičan za vrata
		/// koja kontroler ponovo očita, ali greške koje server još nije primio nestaju (zato je ovde samo zabeleženo ponašanje).
		/// </summary>
		[SkippableTheory]
		[InlineData("prazan", "")]
		[InlineData("skracen", "{\"PendingIsLockerOperational\":true,\"PendingDoorStatuses\":[{\"DoorNumber\":3,\"Sta")]
		public Task UnreadableFileAtStart_ClientStarts_FileReplaced(string variant, string content)
		{
			var options = new KioskOptions {
				PrepareClientDirectory = directory => File.WriteAllText(Path.Combine(directory, StatusFile), content),
			};
			return KioskSession.RunAsync($"{nameof(UnreadableFileAtStart_ClientStarts_FileReplaced)}-{variant}", output, options, async kiosk => {
				await kiosk.WaitForLogLineAsync("Failed to read pending statuses from file", 0);
				// Prvi upis (prvo očitavanje vrata pri startu) zameni nečitljiv sadržaj ispravnim.
				await WaitForStatusFileAsync(kiosk, _ => true, "ispravan fajl posle prvog upisa");
			});
		}

		/// <summary>Pozicije koje server poznaje; klijent i LockerSimulator imaju 48 (2 × 24).</summary>
		private const int ServerDoorCount = 42;
		private static readonly int[] __unknownDoors = [43, 44, 45, 46, 47, 48];
		private static readonly TimeSpan __lockerStatusTimeout = TimeSpan.FromSeconds(60);

		/// <summary>
		/// Incident 99129 (3.11.2026): server ne poznaje pozicije 43–48 i odbija njihovo stanje („ne postoji broj pozicije").
		/// Pri startu klijent javlja sva vrata zatvorena (PonistavanjeGreskePozicije), a kad kontroler prestane da odgovara,
		/// nepoznato stanje (PrijavaGreskePozicije „K“). Odbijeno stanje tih vrata se ne šalje ponovo i ne zadržava prijavu
		/// kvara paketomata, pa server saznaje da paketomat ne radi; kad moduli prorade, kvar se poništava.
		/// </summary>
		[SkippableFact]
		public Task DoorsUnknownToServer_ModulesOff_LockerErrorReported()
		{
			var options = new KioskOptions {
				ConfigureClient = c => c["Locker"]!["DoorWatcherFrequency"] = 3000,
				ConfigureServer = c => c["DoorCount"] = ServerDoorCount,
			};
			return KioskSession.RunAsync(nameof(DoorsUnknownToServer_ModulesOff_LockerErrorReported), output, options, async kiosk => {
				await WaitForStatusFileAsync(kiosk, s => __unknownDoors.All(door => FindDoor(s, "ServerDoorStatuses", door) != null && FindDoor(s, "PendingDoorStatuses", door) == null),
												"vrata 43–48 posle starta ne čekaju slanje");
				var startCalls = await kiosk.Server.GetBusinessCallsAsync(0);
				foreach (int door in __unknownDoors) {
					Assert.Single(startCalls, c => c.Endpoint == "PonistavanjeGreskePozicije" && c.RequestValue("BrojPozicije") == door.ToString()
												&& c.StatusCode == 400 && c.ResponseMessage!.Contains("ne postoji navedeni broj pozicije"));
				}

				var serverMark = await kiosk.Server.MarkAsync();
				var mark = kiosk.Log.Mark();
				await kiosk.Locker.SetModuleAsync(1, isOn: false);
				await kiosk.Locker.SetModuleAsync(2, isOn: false);
				kiosk.Trace("moduli 1 i 2 isključeni");
				await kiosk.WaitForLogLineAsync("Paketomat je u funkciji = [False]", mark, __lockerStatusTimeout);
				await WaitForCallAsync(kiosk, serverMark, c => c.Endpoint == "PrijavaGreskePaketomata" && c.StatusCode == 200, "PrijavaGreskePaketomata → 200");
				await AssertUnknownDoorsReportedOnceAsync(kiosk, serverMark, mark);

				serverMark = await kiosk.Server.MarkAsync();
				mark = kiosk.Log.Mark();
				await kiosk.Locker.SetModuleAsync(1, isOn: true);
				await kiosk.Locker.SetModuleAsync(2, isOn: true);
				kiosk.Trace("moduli 1 i 2 uključeni");
				await kiosk.WaitForLogLineAsync("Paketomat je u funkciji = [True]", mark, __lockerStatusTimeout);
				await WaitForCallAsync(kiosk, serverMark, c => c.Endpoint == "PonistavanjeGreskePaketomata" && c.StatusCode == 200, "PonistavanjeGreskePaketomata → 200");
				await kiosk.WaitForStateAsync(WorkflowState.Start, mark);
			});
		}

		/// <summary>
		/// Stanje iz incidenta 99129 (3.11.2026) pri startu: u ServerStatus.json čeka nepoznato stanje vrata 43–48 koje server ne
		/// poznaje, server vodi paketomat kao ispravan, a kontroler ne odgovara od starta. Prijava kvara paketomata mora stići
		/// na server (kiosk 1.2.22 ju je zadržavao zauvek, a vrata 43–48 slao u krug).
		/// </summary>
		[SkippableFact]
		public Task Restart_PendingDoorsUnknownToServer_ControllerDown_LockerErrorReported()
		{
			var options = new KioskOptions {
				ConfigureClient = c => c["Locker"]!["DoorWatcherFrequency"] = 3000,
				ConfigureServer = c => c["DoorCount"] = ServerDoorCount,
				PrepareClientDirectory = directory => File.WriteAllText(Path.Combine(directory, StatusFile), IncidentStatusFile()),
				BeforeClientStartAsync = async kiosk => {
					await kiosk.Locker.SetModuleAsync(1, isOn: false);
					await kiosk.Locker.SetModuleAsync(2, isOn: false);
					kiosk.Trace("moduli 1 i 2 isključeni pre starta klijenta");
				},
				StartsOutOfOrder = true,
			};
			return KioskSession.RunAsync(nameof(Restart_PendingDoorsUnknownToServer_ControllerDown_LockerErrorReported), output, options, async kiosk => {
				await WaitForCallAsync(kiosk, 0, c => c.Endpoint == "PrijavaGreskePaketomata" && c.StatusCode == 200, "PrijavaGreskePaketomata → 200");
				await AssertUnknownDoorsReportedOnceAsync(kiosk, 0, 0);
				var status = ReadStatusFile(kiosk)!;
				Assert.All(__unknownDoors, door => Assert.Null(FindDoor(status, "PendingDoorStatuses", door)));

				var serverMark = await kiosk.Server.MarkAsync();
				var mark = kiosk.Log.Mark();
				await kiosk.Locker.SetModuleAsync(1, isOn: true);
				await kiosk.Locker.SetModuleAsync(2, isOn: true);
				kiosk.Trace("moduli 1 i 2 uključeni");
				await kiosk.WaitForLogLineAsync("Paketomat je u funkciji = [True]", mark, __lockerStatusTimeout);
				await WaitForCallAsync(kiosk, serverMark, c => c.Endpoint == "PonistavanjeGreskePaketomata" && c.StatusCode == 200, "PonistavanjeGreskePaketomata → 200");
				await kiosk.WaitForStateAsync(WorkflowState.Start, mark);
				await kiosk.Ui.WaitForPageAsync("StartPage");
			});

			static string IncidentStatusFile()
			{
				static string Door(int door) => $$"""{"DoorNumber":{{door}},"Status":null,"IsInFailedState":false}""";
				return $$"""{"PendingIsLockerOperational":null,"PendingDoorStatuses":[{{string.Join(",", __unknownDoors.Select(Door))}}],"ServerIsLockerOperational":true,"ServerDoorStatuses":[{{string.Join(",", Enumerable.Range(1, ServerDoorCount).Select(Door))}}]}""";
			}
		}

		/// <summary>
		/// Posle prijave kvara paketomata: vrata 43–48 su nepoznatog stanja prijavljena tačno jednom (server ih odbija) i ne
		/// šalju se ponovo, a klijent u logu kaže da ih server ne poznaje.
		/// </summary>
		private static async Task AssertUnknownDoorsReportedOnceAsync(KioskSession kiosk, long serverMark, int logMark)
		{
			await Task.Delay(TimeSpan.FromSeconds(3));	// nekoliko prolaza petlje statusa (ProcessingDelayInMilliseconds 1000)
			var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
			foreach (int door in __unknownDoors) {
				var report = Assert.Single(calls, c => IsDoorError(c, door));
				Assert.Equal((400, "K"), (report.StatusCode, report.RequestValue("GreskaPozicije")));
				Assert.Contains("ne postoji broj pozicije", report.ResponseMessage);
				Assert.NotEmpty(kiosk.Log.LinesAfter(logMark, $"ReportDoorErrorState: server ne poznaje poziciju {door} "));
			}
		}

		private static Task<List<ServerCall>> WaitForCallAsync(KioskSession kiosk, long serverMark, Func<ServerCall, bool> match, string what)
			=> Poll.UntilAsync(async () => {
									var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
									return calls.Any(match) ? calls : null;
								},
								__lockerStatusTimeout, what, kiosk.EnsureClientAlive);

		private static bool IsDoorError(ServerCall call, int door)
			=> call.Endpoint == "PrijavaGreskePozicije" && call.RequestValue("BrojPozicije") == door.ToString();

		/// <summary>Čeka PrijavaGreskePozicije za vrata posle kursora sa zadatim statusom odgovora; vraća pozive posle kursora.</summary>
		private static Task<List<ServerCall>> WaitForDoorErrorCallAsync(KioskSession kiosk, long serverMark, int door, int statusCode)
			=> Poll.UntilAsync(async () => {
									var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
									return calls.Any(c => IsDoorError(c, door) && c.StatusCode == statusCode) ? calls : null;
								},
								__statusTimeout, $"PrijavaGreskePozicije {door} → {statusCode}", kiosk.EnsureClientAlive);

		private static bool IsFailed(JsonNode door) => door["IsInFailedState"]?.GetValue<bool>() == true;

		private static JsonNode? FindDoor(JsonNode status, string list, int door)
			=> status[list]?.AsArray().FirstOrDefault(d => d?["DoorNumber"]?.GetValue<int>() == door);

		/// <summary>
		/// Sadržaj ServerStatus.json, ili null ako fajla nema ili nije ispravan JSON. Čita se uz FileShare.Delete: klijent fajl
		/// menja preko File.Replace, koji bi otvoren fajl bez deljenja oborio.
		/// </summary>
		private static JsonNode? ReadStatusFile(KioskSession kiosk)
		{
			try {
				using var stream = new FileStream(Path.Combine(kiosk.ClientDirectory, StatusFile), FileMode.Open, FileAccess.Read,
													FileShare.ReadWrite | FileShare.Delete);
				return JsonNode.Parse(stream);
			}
			catch (Exception ex) when (ex is FileNotFoundException or System.Text.Json.JsonException) {
				return null;
			}
		}

		private static Task<JsonNode> WaitForStatusFileAsync(KioskSession kiosk, Func<JsonNode, bool> condition, string what)
			=> Poll.UntilAsync(() => Task.FromResult(ReadStatusFile(kiosk) is { } status && condition(status) ? status : null),
								__statusTimeout, $"{StatusFile}: {what}", kiosk.EnsureClientAlive);
	}
}
