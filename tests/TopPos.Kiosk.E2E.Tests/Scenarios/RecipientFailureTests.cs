using TopPos.Kiosk.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Primalac (bez plaćanja) kad server ili ormarić ne rade kako treba (doc/plan-e2e-ui-testing.md, F6): spor server,
	/// greška pri potvrdi preuzimanja, blokirana vrata, isključen modul. Greške servera zadaje /_sim/faults
	/// ServerSimulator-a; vrata i module /_sim/doors i /_sim/modules LockerSimulator-a.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class RecipientFailureTests(ITestOutputHelper output)
	{
		/// <summary>
		/// Server odgovara na RRPP posle RequestTimeout-a klijenta (5 s) → klijent odustaje, poruka o neuspeloj
		/// autorizaciji → Start; vrata se ne otvaraju. Zahtev ipak stiže do servera (obrađuje se posle kašnjenja).
		/// </summary>
		[SkippableFact]
		public Task SlowServer_RequestTimesOut_ReturnsToStartWithoutOpeningDoor()
			=> KioskSession.RunAsync(nameof(SlowServer_RequestTimesOut_ReturnsToStartWithoutOpeningDoor), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage();
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.Server.ArmFaultAsync("PrimaocPraznjenjePozicije", delayMs: 8000);

				await kiosk.StartRecipientPickupAsync();
				var mark = await kiosk.EnterCodeAndPhoneAsync(package);
				await kiosk.WaitForStateAsync(WorkflowState.RecipientValidating, mark);
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(30));
				Assert.Equal(WorkflowState.RecipientValidating, toStart.From);
				Assert.Equal("RecipientValidationFailed", toStart.Trigger);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "RecipientRequestPackagePickup exception"));
				await kiosk.Ui.WaitForPageAsync("StartPage");

				// Server beleži poziv tek kad ga obradi (posle kašnjenja) — dokaz da je zahtev stigao i bio zadržan.
				var calls = await Poll.UntilAsync(async () => {
													var list = await kiosk.Server.GetBusinessCallsAsync(serverMark);
													return list.Any(c => c.Endpoint == "PrimaocPraznjenjePozicije") ? list : null;
												},
												TimeSpan.FromSeconds(15), "RRPP na serveru posle kašnjenja");
				Assert.Single(calls, c => c.Endpoint == "PrimaocPraznjenjePozicije");
				Assert.DoesNotContain(calls, c => c.Endpoint == "PrimaocPraznjenjePozicijePotvrda");
				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
			});

		/// <summary>
		/// Server odbija potvrdu preuzimanja (400) posle otvaranja vrata → klijent grešku samo loguje (bez ponavljanja),
		/// primalac normalno preuzima paket, zatvaranje vrata vraća na Start.
		/// </summary>
		[SkippableFact]
		public Task PickupConfirmationRejected_DoorStillOpens_ReturnsToStart()
			=> KioskSession.RunAsync(nameof(PickupConfirmationRejected_DoorStillOpens_ReturnsToStart), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage();
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.Server.ArmFaultAsync("PrimaocPraznjenjePozicijePotvrda", error: "E2E: potvrda odbijena");

				await kiosk.StartRecipientPickupAsync();
				var mark = await kiosk.EnterCodeAndPhoneAsync(package);
				await kiosk.WaitForStateAsync(WorkflowState.RecipientPackagePickup, mark);
				await kiosk.WaitForDoorOpenAsync(package.BrojPozicije, "RecipientPackagePickupPage");
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "slanje potvrde o preuzimanju paketa nije uspelo"));
				await kiosk.CloseDoorAsync(package.BrojPozicije, WorkflowState.Start);
				await kiosk.Ui.WaitForPageAsync("StartPage");

				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				var confirmation = Assert.Single(calls, c => c.Endpoint == "PrimaocPraznjenjePozicijePotvrda");
				Assert.Equal(400, confirmation.StatusCode);
				await kiosk.AssertOnlyDoorUnlockedAsync(unlockMark, package.BrojPozicije);
			});

		/// <summary>
		/// Vrata blokirana (kontroler potvrdi Unlock, ali vrata ostanu zaključana) → dva pokušaja otvaranja, poruka
		/// „Otvaranje vrata nije uspelo" → Start; server dobija prijavu greške pozicije „N" (zatvorena, a trebalo je
		/// otvoriti), a ne potvrdu preuzimanja.
		/// </summary>
		[SkippableFact]
		public Task BlockedDoor_TwoAttempts_ReportsDoorErrorAndReturnsToStart()
			=> KioskSession.RunAsync(nameof(BlockedDoor_TwoAttempts_ReportsDoorErrorAndReturnsToStart), output, BlockedDoorAsync);

		/// <summary>Telo testa BlockedDoor_TwoAttempts_ReportsDoorErrorAndReturnsToStart — deli ga i <see cref="SerialControllerTests"/> (isti tok, drugi kontroler).</summary>
		internal static async Task BlockedDoorAsync(KioskSession kiosk)
		{
			var package = kiosk.Data.RecipientPackage();
			await kiosk.Server.AddPackageAsync(package);
			var serverMark = await kiosk.Server.MarkAsync();
			var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
			await kiosk.Locker.BlockDoorAsync(package.BrojPozicije);
			kiosk.Trace($"vrata {package.BrojPozicije} blokirana");

			await kiosk.StartRecipientPickupAsync();
			var mark = await kiosk.EnterCodeAndPhoneAsync(package);
			await kiosk.WaitForStateAsync(WorkflowState.RecipientPackagePickup, mark);
			var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(30));
			Assert.Equal(WorkflowState.RecipientPackagePickup, toStart.From);
			Assert.Equal("RecipientPickupDoorActionFailed", toStart.Trigger);
			await kiosk.Ui.WaitForPageAsync("StartPage");

			var unlocks = await kiosk.Locker.GetUnlocksAsync(unlockMark);
			Assert.Equal(2, unlocks.Count);
			Assert.All(unlocks, u => {
				Assert.Equal(package.BrojPozicije, u.Door);
				Assert.False(u.Unlocked);
			});
			Assert.True((await kiosk.Locker.GetDoorAsync(package.BrojPozicije)).IsLocked);

			var calls = await WaitForDoorErrorAsync(kiosk, serverMark, package.BrojPozicije, expectedError: "N");
			Assert.DoesNotContain(calls, c => c.Endpoint == "PrimaocPraznjenjePozicijePotvrda");
		}

		/// <summary>
		/// Modul vrata isključen (kontroler ne odgovara) → dva neuspela pokušaja, poruka → Start; server dobija prijavu
		/// greške pozicije „K" (stanje nepoznato), a ne potvrdu preuzimanja. Vrata su u drugom modulu, pa prvi radi.
		/// </summary>
		[SkippableFact]
		public Task ModuleOff_NoControllerResponse_ReportsUnknownDoorAndReturnsToStart()
			=> KioskSession.RunAsync(nameof(ModuleOff_NoControllerResponse_ReportsUnknownDoorAndReturnsToStart), output, ModuleOffAsync);

		/// <summary>Telo testa ModuleOff_NoControllerResponse_ReportsUnknownDoorAndReturnsToStart — deli ga i <see cref="SerialControllerTests"/> (isti tok, drugi kontroler).</summary>
		internal static async Task ModuleOffAsync(KioskSession kiosk)
		{
			var package = kiosk.Data.RecipientPackage(module: 2);
			Assert.Equal(2, (await kiosk.Locker.GetDoorAsync(package.BrojPozicije)).Module);
			await kiosk.Server.AddPackageAsync(package);
			var serverMark = await kiosk.Server.MarkAsync();
			var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
			await kiosk.Locker.SetModuleAsync(2, isOn: false);
			kiosk.Trace("modul 2 isključen");

			await kiosk.StartRecipientPickupAsync();
			var mark = await kiosk.EnterCodeAndPhoneAsync(package);
			await kiosk.WaitForStateAsync(WorkflowState.RecipientPackagePickup, mark);
			var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(45));
			Assert.Equal(WorkflowState.RecipientPackagePickup, toStart.From);
			Assert.Equal("RecipientPickupDoorActionFailed", toStart.Trigger);
			await kiosk.Ui.WaitForPageAsync("StartPage");

			Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
			var calls = await WaitForDoorErrorAsync(kiosk, serverMark, package.BrojPozicije, expectedError: "K");
			Assert.DoesNotContain(calls, c => c.Endpoint == "PrimaocPraznjenjePozicijePotvrda");
		}

		/// <summary>
		/// Primalac ne zatvori vrata → praćenje vrata ističe posle OperationTimeout-a (20 s u e2e konfiguraciji) →
		/// RecipientPickupDoorActionFailed → Start; server je već dobio potvrdu preuzimanja (vrata su se otvorila), a zatim
		/// prijavu greške pozicije „O" (otvorena).
		/// </summary>
		[SkippableFact]
		public Task DoorLeftOpen_TrackingTimesOut_ReportsOpenDoorAndReturnsToStart()
			=> KioskSession.RunAsync(nameof(DoorLeftOpen_TrackingTimesOut_ReportsOpenDoorAndReturnsToStart), output, DoorLeftOpenAsync);

		/// <summary>Telo testa DoorLeftOpen_TrackingTimesOut_ReportsOpenDoorAndReturnsToStart — deli ga i <see cref="SerialControllerTests"/> (isti tok, drugi kontroler).</summary>
		internal static async Task DoorLeftOpenAsync(KioskSession kiosk)
		{
			var package = kiosk.Data.RecipientPackage();
			await kiosk.Server.AddPackageAsync(package);
			var serverMark = await kiosk.Server.MarkAsync();
			var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

			await kiosk.StartRecipientPickupAsync();
			var mark = await kiosk.EnterCodeAndPhoneAsync(package);
			await kiosk.WaitForStateAsync(WorkflowState.RecipientPackagePickup, mark);
			await kiosk.WaitForDoorOpenAsync(package.BrojPozicije, "RecipientPackagePickupPage");
			kiosk.Trace($"vrata {package.BrojPozicije} ostaju otvorena");

			var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(45));
			Assert.Equal(WorkflowState.RecipientPackagePickup, toStart.From);
			Assert.Equal("RecipientPickupDoorActionFailed", toStart.Trigger);
			await kiosk.Ui.WaitForPageAsync("StartPage");
			Assert.False((await kiosk.Locker.GetDoorAsync(package.BrojPozicije)).IsLocked);

			var calls = await WaitForDoorErrorAsync(kiosk, serverMark, package.BrojPozicije, expectedError: "O");
			Assert.Single(calls, c => c.Endpoint == "PrimaocPraznjenjePozicijePotvrda");
			await kiosk.AssertOnlyDoorUnlockedAsync(unlockMark, package.BrojPozicije);
		}

		/// <summary>
		/// Prijavu greške pozicije šalje petlja statusa (LockerStatusUpdater) nezavisno od toka — čeka se dok ne stigne.
		/// </summary>
		internal static Task<List<ServerCall>> WaitForDoorErrorAsync(KioskSession kiosk, long serverMark, int door, string expectedError)
			=> Poll.UntilAsync(async () => {
									var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
									return calls.Any(c => c.Endpoint == "PrijavaGreskePozicije"
															&& c.RequestValue("BrojPozicije") == door.ToString()
															&& c.RequestValue("GreskaPozicije") == expectedError)
										? calls
										: null;
								},
								TimeSpan.FromSeconds(20), $"PrijavaGreskePozicije {door} \"{expectedError}\"", kiosk.EnsureClientAlive);
	}
}
