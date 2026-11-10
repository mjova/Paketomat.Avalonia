using TopPos.Client.WPF.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Servisne funkcije i stanje kioska (storyboard T01–T03, Z01, Z02): servisna vrata na skeniran servisni kod, prozor
	/// statusa uređaja na dug pritisak Home-a, kiosk „nije u funkciji" kad nijedna vrata nisu zatvorena, i promena jezika
	/// sa kontaktom podrške u podnožju. E2E: ServiceDoorNumber 3, ServiceDoorCode "E2E-SERVICE-CODE".
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class KioskServiceTests(ITestOutputHelper output)
	{
		private const string ServiceCode = "E2E-SERVICE-CODE";
		private const int ServiceDoor = 3;

		/// <summary>
		/// T01: servisni kod na Start ekranu → servisna vrata se otključaju → odmah nazad na Start (zatvaranje se ne čeka);
		/// serveru se ne šalje ništa od poslovnih poziva.
		/// </summary>
		[SkippableFact]
		public Task ServiceCode_OpensServiceDoor_ReturnsToStart()
			=> KioskSession.RunAsync(nameof(ServiceCode_OpensServiceDoor_ReturnsToStart), output, async kiosk => {
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				var mark = kiosk.Log.Mark();

				var opening = await kiosk.ScanAsync(ServiceCode, WorkflowState.ServiceDoorOpening);
				Assert.Equal((WorkflowState.Start, "ServiceBarcodeScanned"), (opening.From, opening.Trigger));
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, opening.Line, TimeSpan.FromSeconds(15));
				Assert.Equal((WorkflowState.ServiceDoorOpening, "ServiceDoorHandled"), (toStart.From, toStart.Trigger));
				await kiosk.Ui.WaitForPageAsync("StartPage");

				var unlock = Assert.Single(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				Assert.Equal((ServiceDoor, true), (unlock.Door, unlock.Unlocked));
				Assert.False((await kiosk.Locker.GetDoorAsync(ServiceDoor)).IsLocked);
				Assert.Empty(kiosk.Log.LinesAfter(mark, "HandleDoorActionFailure"));
				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark),
										c => c.Endpoint is "KurirAutorizacija" or "PrimaocPraznjenjePozicije" or "KurirPunjenjePozicije");

				await kiosk.Locker.CloseDoorAsync(ServiceDoor);	// da otvorena servisna vrata ne odu serveru kao greška u ostatku testa
				kiosk.Trace($"vrata {ServiceDoor} zatvorena");
			});

		/// <summary>T02: servisna vrata se ne otvore (blokirana) → poruka → prijava greške „N" → Start.</summary>
		[SkippableFact]
		public Task ServiceDoorBlocked_ReportsDoorError_ReturnsToStart()
			=> KioskSession.RunAsync(nameof(ServiceDoorBlocked_ReportsDoorError_ReturnsToStart), output, async kiosk => {
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.Locker.BlockDoorAsync(ServiceDoor);
				kiosk.Trace($"vrata {ServiceDoor} blokirana");

				var opening = await kiosk.ScanAsync(ServiceCode, WorkflowState.ServiceDoorOpening);
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, opening.Line, TimeSpan.FromSeconds(15));
				Assert.Equal((WorkflowState.ServiceDoorOpening, "ServiceDoorHandled"), (toStart.From, toStart.Trigger));
				Assert.NotEmpty(kiosk.Log.LinesAfter(opening.Line, $"HandleDoorActionFailure: problem sa otvaranjem vrata {ServiceDoor}"));
				await kiosk.Ui.WaitForPageAsync("StartPage");

				var unlock = Assert.Single(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				Assert.Equal((ServiceDoor, false), (unlock.Door, unlock.Unlocked));
				await RecipientFailureTests.WaitForDoorErrorAsync(kiosk, serverMark, ServiceDoor, expectedError: "N");
			});

		/// <summary>
		/// T03: Home držan duže od 3 s → prozor statusa uređaja (modalni, iznad Start-a) → „Zatvori" → nazad na Start; kratak
		/// pritisak (GoToStartPageCommand) se pri tome ne okida.
		/// </summary>
		[SkippableFact]
		public Task HomeLongPress_ShowsDeviceStatus_CloseReturnsToStart()
			=> KioskSession.RunAsync(nameof(HomeLongPress_ShowsDeviceStatus_CloseReturnsToStart), output, async kiosk => {
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.PressWithMouseAsync("Header.Home", hold: TimeSpan.FromSeconds(3.5));
				await kiosk.WaitForLogLineAsync("MainWindowViewModel: executing DisplayDeviceStatusCommand", mark);
				await kiosk.Ui.WaitForAsync("DeviceStatus.Close");
				Assert.Empty(kiosk.Log.LinesAfter(mark, "executing GoToStartPageCommand"));
				Assert.All(kiosk.Log.TransitionsAfter(mark), t => Assert.Equal((WorkflowState.Start, "Reset"), (t.To, t.Trigger)));

				await kiosk.Ui.TapAsync("DeviceStatus.Close");
				await kiosk.Ui.WaitForHiddenAsync("DeviceStatus.Close");
				await kiosk.Ui.WaitForAsync("StartPage.RecipientPickUpPackage");	// glavni prozor ponovo prima unos
			});

		/// <summary>
		/// Z01: oba modula isključena (nijedna vrata nisu zatvorena) → kiosk „nije u funkciji": ceo prozor je onemogućen, a
		/// skeniranje kurirskog QR-a se ignoriše. Moduli ponovo uključeni → kiosk u funkciji → Start, prozor omogućen.
		/// DoorWatcher-ov prolaz je skraćen na 3 s da se promena vidi brzo.
		/// </summary>
		[SkippableFact]
		public Task OutOfOrder_AllModulesOff_UiDisabledAndScanIgnored_RecoversWhenModulesOn()
		{
			var options = new KioskOptions {
				ConfigureClient = c => c["Locker"]!["DoorWatcherFrequency"] = 3000,
			};
			return KioskSession.RunAsync(nameof(OutOfOrder_AllModulesOff_UiDisabledAndScanIgnored_RecoversWhenModulesOn), output, options, async kiosk => {
				var serverMark = await kiosk.Server.MarkAsync();
				var mark = kiosk.Log.Mark();
				await kiosk.Locker.SetModuleAsync(1, isOn: false);
				await kiosk.Locker.SetModuleAsync(2, isOn: false);
				kiosk.Trace("moduli 1 i 2 isključeni");
				await kiosk.WaitForLogLineAsync("Paketomat je u funkciji = [False]", mark, TimeSpan.FromSeconds(60));
				await Poll.UntilAsync(async () => !await kiosk.Ui.IsEnabledAsync("StartPage.RecipientPickUpPackage"),
										KioskUi.DefaultTimeout, "Start ekran onemogućen", kiosk.EnsureClientAlive);

				var scanMark = kiosk.Log.Mark();
				await kiosk.Locker.ScanBarcodeAsync(kiosk.Data.CourierQrCode);
				kiosk.Trace($"skeniran QR kurira {kiosk.Data.CourierQrCode} (kiosk nije u funkciji)");
				await kiosk.WaitForLogLineAsync("je ignorisan zato sto je paketomat u stanju greske", scanMark);
				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(scanMark), t => t.To == WorkflowState.CourierAuthenticating);

				var onMark = kiosk.Log.Mark();
				await kiosk.Locker.SetModuleAsync(1, isOn: true);
				await kiosk.Locker.SetModuleAsync(2, isOn: true);
				kiosk.Trace("moduli 1 i 2 uključeni");
				await kiosk.WaitForLogLineAsync("Paketomat je u funkciji = [True]", onMark, TimeSpan.FromSeconds(60));
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, onMark);
				Assert.Equal("Reset", toStart.Trigger);
				await kiosk.Ui.WaitForAsync("StartPage.RecipientPickUpPackage");

				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "KurirAutorizacija");
			});
		}

		/// <summary>
		/// Z02: promena jezika (Header.Language → Language.en) menja i kontakt podrške u podnožju (DailyExpress); jezik ostaje
		/// izabran i posle povratka na Start, a vraća se izborom srpskog.
		/// </summary>
		[SkippableFact]
		public Task LanguageSwitch_SupportContactFollowsLanguage()
			=> KioskSession.RunAsync(nameof(LanguageSwitch_SupportContactFollowsLanguage), output, async kiosk => {
				var serbian = await kiosk.Ui.WaitForTextAsync("Tehnička podrška: 011/405-1811");
				Assert.Contains(kiosk.Settings.LockerId, serbian);

				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("Header.Language");
				await kiosk.Ui.TapAsync("Language.en");
				await kiosk.WaitForLogLineAsync("LanguageManager: jezik [en] je aktivan", mark);
				var english = await kiosk.Ui.WaitForTextAsync("Support: 011/405-1811");
				Assert.Contains(kiosk.Settings.LockerId, english);
				await kiosk.Ui.WaitForHiddenAsync("Language.en");

				// Reset (Home) ne vraća jezik
				var homeMark = kiosk.Log.Mark();
				await kiosk.Ui.PressWithMouseAsync("Header.Home");
				await kiosk.WaitForStateAsync(WorkflowState.Start, homeMark);
				await kiosk.Ui.WaitForTextAsync("Support: 011/405-1811");

				mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("Header.Language");
				await kiosk.Ui.TapAsync("Language.sr");
				await kiosk.WaitForLogLineAsync("LanguageManager: jezik [sr] je aktivan", mark);
				await kiosk.Ui.WaitForTextAsync("Tehnička podrška: 011/405-1811");
			});
	}
}
