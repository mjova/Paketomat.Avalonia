using TopPos.Client.WPF.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Unos primaoca (storyboard K02, K14): telefon iz konfiguracije umesto ekrana za telefon, lokalna provera prekratkog ili
	/// neispravnog koda (bez poziva serveru) i Home na ekranima za unos. MinimumUnlockCodeLength 6 važi i za kod i za telefon.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class RecipientInputTests(ITestOutputHelper output)
	{
		private const string DefaultPhone = "0601234567";

		/// <summary>
		/// K02: General.DefaultRecipientPhoneNumber postavljen → posle koda nema ekrana za telefon; server dobija telefon iz
		/// konfiguracije (RRPP i potvrda preuzimanja) → vrata → zatvaranje → Start.
		/// </summary>
		[SkippableFact]
		public Task DefaultPhone_SkipsPhonePage_SendsConfiguredPhone()
		{
			var options = new KioskOptions {
				ConfigureClient = c => c["General"]!["DefaultRecipientPhoneNumber"] = DefaultPhone,
			};
			return KioskSession.RunAsync(nameof(DefaultPhone_SkipsPhonePage_SendsConfiguredPhone), output, options, async kiosk => {
				var package = kiosk.Data.RecipientPackage() with { TelefonPrimaoca = DefaultPhone };
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				await kiosk.StartRecipientPickupAsync();
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TypeOnKeypadAsync(package.KodZaOtvaranje.ToString());
				var validating = await kiosk.WaitForStateAsync(WorkflowState.RecipientValidating, mark);
				Assert.Equal((WorkflowState.RecipientCodeInput, "RecipientCodeAcceptedWithDefaultPhone"), (validating.From, validating.Trigger));
				var toPickup = await kiosk.WaitForStateAsync(WorkflowState.RecipientPackagePickup, mark);
				Assert.Equal("RecipientNoPaymentNeeded", toPickup.Trigger);
				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(mark), t => t.To == WorkflowState.RecipientPhoneInput);
				await kiosk.WaitForDoorOpenAsync(package.BrojPozicije, "RecipientPackagePickupPage");
				await kiosk.CloseDoorAsync(package.BrojPozicije, WorkflowState.Start);

				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				Assert.Equal(DefaultPhone, Assert.Single(calls, c => c.Endpoint == "PrimaocPraznjenjePozicije").Query["TelefonPrimaoca"]);
				Assert.Equal(DefaultPhone, Assert.Single(calls, c => c.Endpoint == "PrimaocPraznjenjePozicijePotvrda").RequestValue("TelefonPrimaoca"));
				await kiosk.AssertOnlyDoorUnlockedAsync(unlockMark, package.BrojPozicije);
			});
		}

		/// <summary>
		/// K14: kod kraći od minimuma ne može da se potvrdi (Potvrdi onemogućen), šesta cifra ga omogućava; isto za telefon.
		/// Serveru se ništa ne šalje dok se ne potvrdi ispravna dužina.
		/// </summary>
		[SkippableFact]
		public Task ShortCodeAndPhone_ConfirmDisabledUntilMinimumLength()
			=> KioskSession.RunAsync(nameof(ShortCodeAndPhone_ConfirmDisabledUntilMinimumLength), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage();
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var code = package.KodZaOtvaranje.ToString();

				await kiosk.StartRecipientPickupAsync();
				await kiosk.Ui.TypeOnKeypadAsync(code[..5], confirm: false);
				Assert.False(await kiosk.Ui.IsEnabledAsync("Keypad.Confirm"), "Potvrdi mora biti onemogućen za kod od 5 cifara");
				await kiosk.Ui.TypeOnKeypadAsync(code[5..], confirm: false);
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("Keypad.Confirm");
				await kiosk.WaitForStateAsync(WorkflowState.RecipientPhoneInput, mark);
				await kiosk.Ui.WaitForPageAsync("RecipientPhoneInputPage");

				await kiosk.Ui.TypeOnKeypadAsync(package.TelefonPrimaoca[..5], confirm: false);
				Assert.False(await kiosk.Ui.IsEnabledAsync("Keypad.Confirm"), "Potvrdi mora biti onemogućen za telefon od 5 cifara");
				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "PrimaocPraznjenjePozicije");

				await kiosk.Ui.TypeOnKeypadAsync(package.TelefonPrimaoca[5..]);
				await kiosk.WaitForStateAsync(WorkflowState.RecipientPackagePickup, mark);
				await kiosk.WaitForDoorOpenAsync(package.BrojPozicije, "RecipientPackagePickupPage");
				await kiosk.CloseDoorAsync(package.BrojPozicije, WorkflowState.Start);
			});

		/// <summary>
		/// K14: skeniran kod koji ne prolazi lokalnu proveru (prekratak, nije broj) → poruka, ostaje se na unosu koda bez poziva
		/// serveru; ispravan skeniran kod posle toga vodi na unos telefona.
		/// </summary>
		[SkippableTheory]
		[InlineData("prekratak", "12345")]
		[InlineData("nije-broj", "ABC1234")]
		public Task ScannedInvalidCode_MessageStaysOnCodeInput(string variant, string invalidCode)
			=> KioskSession.RunAsync($"{nameof(ScannedInvalidCode_MessageStaysOnCodeInput)}-{variant}", output, async kiosk => {
				var package = kiosk.Data.RecipientPackage();
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				await kiosk.StartRecipientPickupAsync();

				var mark = kiosk.Log.Mark();
				await kiosk.Locker.ScanBarcodeAsync(invalidCode);
				kiosk.Trace($"skeniran neispravan kod {invalidCode}");
				await kiosk.WaitForLogLineAsync($"skenirani barkod: '{invalidCode}'", mark);
				await Task.Delay(TimeSpan.FromSeconds(3));	// namerno: poruka traje 2 s, a prelaza ne sme biti
				Assert.Empty(kiosk.Log.TransitionsAfter(mark));
				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "PrimaocPraznjenjePozicije");

				var validMark = kiosk.Log.Mark();
				await kiosk.Locker.ScanBarcodeAsync(package.KodZaOtvaranje.ToString());
				kiosk.Trace($"skeniran kod {package.KodZaOtvaranje}");
				var accepted = await kiosk.WaitForStateAsync(WorkflowState.RecipientPhoneInput, validMark);
				Assert.Equal("RecipientCodeAccepted", accepted.Trigger);
				await kiosk.Ui.WaitForPageAsync("RecipientPhoneInputPage");
			});

		/// <summary>K14: Home na unosu koda ili telefona → Reset → Start, bez poziva serveru.</summary>
		[SkippableTheory]
		[InlineData(false)]
		[InlineData(true)]
		public Task HomeOnInput_ReturnsToStart(bool onPhone)
			=> KioskSession.RunAsync($"{nameof(HomeOnInput_ReturnsToStart)}-{(onPhone ? "telefon" : "kod")}", output, async kiosk => {
				var package = kiosk.Data.RecipientPackage();
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				await kiosk.StartRecipientPickupAsync();
				var state = WorkflowState.RecipientCodeInput;
				if (onPhone) {
					var codeMark = kiosk.Log.Mark();
					await kiosk.Ui.TypeOnKeypadAsync(package.KodZaOtvaranje.ToString());
					await kiosk.WaitForStateAsync(WorkflowState.RecipientPhoneInput, codeMark);
					await kiosk.Ui.WaitForPageAsync("RecipientPhoneInputPage");
					await kiosk.Ui.TypeOnKeypadAsync(package.TelefonPrimaoca[..3], confirm: false);
					state = WorkflowState.RecipientPhoneInput;
				}

				var mark = kiosk.Log.Mark();
				await kiosk.Ui.PressWithMouseAsync("Header.Home");
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark);
				Assert.Equal((state, "Reset"), (toStart.From, toStart.Trigger));
				await kiosk.Ui.WaitForPageAsync("StartPage");
				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "PrimaocPraznjenjePozicije");
			});
	}
}
