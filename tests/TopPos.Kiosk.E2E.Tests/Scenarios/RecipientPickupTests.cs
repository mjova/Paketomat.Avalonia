using TopPos.Kiosk.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>Primalac preuzima paket (doc/plan-e2e-ui-testing.md, F5 scenariji 1–5).</summary>
	[Collection(E2ECollection.Name)]
	public sealed class RecipientPickupTests(ITestOutputHelper output)
	{
		/// <summary>
		/// Scenario 1: paket bez otkupa → kod + telefon → vrata se otvaraju → primalac zatvara vrata → Start.
		/// Server dobija RRPP pa potvrdu preuzimanja; otključana su samo vrata paketa.
		/// </summary>
		[SkippableFact]
		public Task NoPayment_DoorOpens_ClosingDoorReturnsToStart()
			=> KioskSession.RunAsync(nameof(NoPayment_DoorOpens_ClosingDoorReturnsToStart), output, NoPaymentPickupAsync);

		/// <summary>Telo testa NoPayment_DoorOpens_ClosingDoorReturnsToStart — deli ga i <see cref="SerialControllerTests"/> (isti tok, drugi kontroler).</summary>
		internal static async Task NoPaymentPickupAsync(KioskSession kiosk)
		{
			var package = kiosk.Data.RecipientPackage(amount: 0);
			await kiosk.Server.AddPackageAsync(package);
			var serverMark = await kiosk.Server.MarkAsync();
			var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

			await kiosk.StartRecipientPickupAsync();
			var mark = await kiosk.EnterCodeAndPhoneAsync(package);
			await kiosk.WaitForStateAsync(WorkflowState.RecipientPackagePickup, mark);
			await kiosk.WaitForDoorOpenAsync(package.BrojPozicije, "RecipientPackagePickupPage");

			var toStart = await kiosk.CloseDoorAsync(package.BrojPozicije, WorkflowState.Start);
			Assert.Equal(WorkflowState.RecipientPackagePickup, toStart.From);
			Assert.Equal("RecipientPickupComplete", toStart.Trigger);
			await kiosk.Ui.WaitForPageAsync("StartPage");

			// Server: RRPP sa kodom i telefonom, pa potvrda preuzimanja — tim redom.
			var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
			var rrpp = Assert.Single(calls, c => c.Endpoint == "PrimaocPraznjenjePozicije");
			Assert.Equal(package.KodZaOtvaranje.ToString(), rrpp.Query["KodZaOtvaranje"]);
			Assert.Equal(package.TelefonPrimaoca, rrpp.Query["TelefonPrimaoca"]);
			Assert.Equal(kiosk.Settings.LockerId, rrpp.Query["SifraBenzinskeStanice"]);
			var confirm = Assert.Single(calls, c => c.Endpoint == "PrimaocPraznjenjePozicijePotvrda");
			Assert.True(confirm.Seq > rrpp.Seq, $"Potvrda preuzimanja mora doći posle RRPP-a: {string.Join(", ", calls)}");
			Assert.DoesNotContain(calls, c => c.IsUnexpectedError);
			Assert.True(Assert.Single(await kiosk.Server.GetPackagesAsync()).Preuzet);

			await kiosk.AssertOnlyDoorUnlockedAsync(unlockMark, package.BrojPozicije);
		}

		/// <summary>
		/// Scenario 5: nepostojeći kod → server odbija RRPP (400) → poruka ~2 s → Start; nijedna vrata se ne otvaraju,
		/// paket na serveru ostaje netaknut.
		/// </summary>
		[SkippableFact]
		public Task WrongCode_ServerRejects_ReturnsToStartWithoutOpeningDoor()
			=> KioskSession.RunAsync(nameof(WrongCode_ServerRejects_ReturnsToStartWithoutOpeningDoor), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: 0);
				await kiosk.Server.AddPackageAsync(package);
				var wrong = package with { KodZaOtvaranje = package.KodZaOtvaranje == 9999999 ? 1000000 : package.KodZaOtvaranje + 1 };
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				await kiosk.StartRecipientPickupAsync();
				var mark = await kiosk.EnterCodeAndPhoneAsync(wrong);
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark);
				Assert.Equal(WorkflowState.RecipientValidating, toStart.From);
				Assert.Equal("RecipientValidationFailed", toStart.Trigger);
				await kiosk.Ui.WaitForPageAsync("StartPage");

				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				var rrpp = Assert.Single(calls, c => c.Endpoint == "PrimaocPraznjenjePozicije");
				Assert.Equal(400, rrpp.StatusCode);
				Assert.Equal(wrong.KodZaOtvaranje.ToString(), rrpp.Query["KodZaOtvaranje"]);
				Assert.DoesNotContain(calls, c => c.Endpoint == "PrimaocPraznjenjePozicijePotvrda");
				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				Assert.Contains(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod);
			});

		/// <summary>
		/// Scenario 9: ekran za kod bez unosa → posle OperationTimeout (20 s u e2e konfiguraciji) Reset → Start.
		/// </summary>
		[SkippableFact]
		public Task CodeInput_NoActivity_TimesOutToStart()
			=> KioskSession.RunAsync(nameof(CodeInput_NoActivity_TimesOutToStart), output, async kiosk => {
				await kiosk.StartRecipientPickupAsync();
				var mark = kiosk.Log.Mark();
				var started = DateTime.UtcNow;

				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(40));
				var elapsed = DateTime.UtcNow - started;
				Assert.Equal(WorkflowState.RecipientCodeInput, toStart.From);
				Assert.Equal("Reset", toStart.Trigger);
				Assert.InRange(elapsed, TimeSpan.FromSeconds(17), TimeSpan.FromSeconds(30));
				await kiosk.Ui.WaitForPageAsync("StartPage");
			});

		/// <summary>
		/// P1 (bffac40; plan „E2E — ručna prijava kurira i zakasneli odgovor", scenario 4): Home dok server proverava kod i
		/// telefon (odgovor kasni 3 s, uspešan) → kiosk čeka odgovor, pa Reset → Start; zakasneli ishod je ignorisan —
		/// bez otkupa RecipientNoPaymentNeeded (vrata se ne otvaraju), sa otkupom RecipientPaymentRequired (POS ne dobija
		/// prodaju). Pre ispravke odgovor za prethodnog kupca je mogao da otvori vrata sledećem.
		/// </summary>
		[SkippableTheory]
		[InlineData(0)]
		[InlineData(250)]
		public Task HomeWhileValidating_LateOutcomeIgnored_NoDoorOpens(int amount)
			=> KioskSession.RunAsync($"{nameof(HomeWhileValidating_LateOutcomeIgnored_NoDoorOpens)}-{amount}", output, async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: amount);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				var serverDelay = TimeSpan.FromSeconds(3);	// zakasneli uspeh: kraće od RequestTimeout-a (5 s)
				await kiosk.Server.ArmFaultAsync("PrimaocPraznjenjePozicije", delayMs: (int)serverDelay.TotalMilliseconds);

				await kiosk.StartRecipientPickupAsync();
				var homeButton = await kiosk.Ui.GetClickablePointAsync("Header.Home");	// unapred: posle potvrde telefona klik bez UIA poziva
				var mark = await kiosk.EnterCodeAndPhoneAsync(package);
				var validating = await kiosk.WaitForStateAsync(WorkflowState.RecipientValidating, mark);
				Assert.Equal("RecipientPhoneEntered", validating.Trigger);

				var homeMark = kiosk.Log.Mark();
				await kiosk.Ui.ClickAtAsync(homeButton, "Header.Home");
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, homeMark, TimeSpan.FromSeconds(10));
				Assert.Equal(WorkflowState.RecipientValidating, toStart.From);
				Assert.Equal("Reset", toStart.Trigger);
				kiosk.AssertResetWaitedForServer(validating, homeMark, toStart, serverDelay);
				await kiosk.Ui.WaitForPageAsync("StartPage");
				var lateOutcome = amount == 0 ? "RecipientNoPaymentNeeded" : "RecipientPaymentRequired";
				await kiosk.AssertIgnoredTriggersAsync(homeMark, (lateOutcome, WorkflowState.Start));

				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(mark), t => t.To is WorkflowState.RecipientPackagePickup or WorkflowState.RecipientPayment);
				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				Assert.Equal(200, Assert.Single(calls, c => c.Endpoint == "PrimaocPraznjenjePozicije").StatusCode);
				Assert.DoesNotContain(calls, c => c.Endpoint is "PrimaocPraznjenjePozicijePotvrda" or "PotvrdaPlacanja");
				Assert.Contains(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod);
				if (amount > 0) {
					var pos = await kiosk.Locker.GetPosAsync();
					Assert.Equal("Idle", pos.Phase);
					Assert.NotEqual("01", pos.TransactionType);	// nijedna prodaja (poslednja je provera prisutnosti pri startu klijenta)
				}
			});
	}
}
