using TopPos.Kiosk.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Jedan vlasnik kioska (KioskOperationCoordinator, faza 2 kanala ka Agentu): dok kiosk ima vlasnika (prozor statusa,
	/// servisna vrata, sesija u toku), barkod sa skenera (COM, pozadinska nit) se odbija odmah i ne pokreće sesiju ni kasnije;
	/// DoorWatcher je pauziran; sesija se završava svojim tokom, a kiosk postaje slobodan tek posle nje.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class KioskOwnershipTests(ITestOutputHelper output)
	{
		private const string ServiceCode = "E2E-SERVICE-CODE";
		private const int ServiceDoor = 3;
		private const string BusyScanLine = "je ignorisan — kiosk je zauzet";
		private const string WatcherPausedLine = "DoorWatcher: Pause: Zaustavljanje provere statusa.";
		private const string WatcherResumedLine = "DoorWatcher: Resume: Nastavak provere statusa";
		private const string WatcherPollLine = "provera statusa modula";
		private const string ReleasedLine = "Koordinator: kiosk slobodan";
		private const string ScannerCheckCancelledLine = "Barcode scanner status check was cancelled.";
		private const string PosCheckCancelledLine = "POS terminal status check was cancelled.";

		/// <summary>
		/// Prozor statusa uređaja (uloga Service) je otvoren → QR kurira se odbija, bez prijave. Posle „Zatvori" kiosk je
		/// slobodan i isti QR prijavljuje kurira.
		/// </summary>
		[RealMouseFact]	// v4
		public Task StatusWindowOpen_CourierScanIgnored_LoginWorksAfterClose()
			=> KioskSession.RunAsync(nameof(StatusWindowOpen_CourierScanIgnored_LoginWorksAfterClose), output, async kiosk => {
				var serverMark = await kiosk.Server.MarkAsync();
				await kiosk.Ui.PressWithMouseAsync("Header.Home", hold: TimeSpan.FromSeconds(3.5));
				await kiosk.Ui.WaitForAsync("DeviceStatus.Close");
				// Prozor proverava skener komandom preko istog COM-a (2 s pauza + odgovor do 3 s); barkod poslat tada bi bio
				// pročitan kao odgovor, a ne kao skeniranje.
				await Task.Delay(TimeSpan.FromSeconds(6));

				var mark = kiosk.Log.Mark();
				await kiosk.Locker.ScanBarcodeAsync(kiosk.Data.CourierQrCode);
				kiosk.Trace($"skeniran QR kurira {kiosk.Data.CourierQrCode} (prozor statusa otvoren)");
				await kiosk.WaitForLogLineAsync(BusyScanLine, mark);
				Assert.True(await IsBusyAsync(kiosk), "status.lock: kiosk mora biti zauzet dok je prozor statusa otvoren");

				await kiosk.Ui.TapAsync("DeviceStatus.Close");
				await kiosk.Ui.WaitForHiddenAsync("DeviceStatus.Close");
				Assert.Empty(kiosk.Log.TransitionsAfter(mark));
				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "KurirAutorizacija");

				await CourierTests.OpenDepositScanAsync(kiosk);
				await CourierTests.LogoutFromScanAsync(kiosk);
			});

		/// <summary>
		/// Prozor statusa zatvoren odmah, pre provera uređaja (pauza 2 s): provere skenera i POS-a se otkazuju, a kiosk je
		/// slobodan tek posle njih. Skener je tada ponovo u čitanju barkodova, pa QR odmah prijavljuje kurira.
		/// </summary>
		[RealMouseFact]	// v4
		public Task StatusWindowClosedEarly_ChecksCancelledBeforeRelease_LoginWorks()
			=> KioskSession.RunAsync(nameof(StatusWindowClosedEarly_ChecksCancelledBeforeRelease_LoginWorks), output, async kiosk => {
				await kiosk.Ui.PressWithMouseAsync("Header.Home", hold: TimeSpan.FromSeconds(3.5));
				await kiosk.Ui.WaitForAsync("DeviceStatus.Close");
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("DeviceStatus.Close");
				await kiosk.Ui.WaitForHiddenAsync("DeviceStatus.Close");
				kiosk.Trace("prozor statusa zatvoren pre provera uređaja");

				await kiosk.WaitForLogLineAsync(ReleasedLine, mark);
				var released = kiosk.Log.FirstLineAfter(mark, ReleasedLine);
				foreach (var cancelled in new[] { ScannerCheckCancelledLine, PosCheckCancelledLine }) {
					var line = kiosk.Log.FirstLineAfter(mark, cancelled);
					Assert.True(line != null && line < released, $"„{cancelled}\" mora biti u logu pre oslobađanja kioska");
				}

				await CourierTests.OpenDepositScanAsync(kiosk);
				await CourierTests.LogoutFromScanAsync(kiosk);
			});

		/// <summary>
		/// Servisna vrata blokirana (poruka 5 s produžava otvaranje) → QR kurira skeniran tokom otvaranja se odbija i ne
		/// prijavljuje kurira ni posle povratka na Start. DoorWatcher (prolaz 1 s) je pauziran od rezervacije do kraja
		/// otvaranja, pa nastavlja.
		/// </summary>
		[SkippableFact]
		public Task ServiceDoorOpening_CourierScanIgnored_NoSessionAfter_DoorWatcherPaused()
		{
			var options = new KioskOptions {
				ConfigureClient = c => {
					c["General"]!["CourierMessageDisplayTimeInSeconds"] = 5;
					c["Locker"]!["DoorWatcherFrequency"] = 1000;
				},
			};
			return KioskSession.RunAsync(nameof(ServiceDoorOpening_CourierScanIgnored_NoSessionAfter_DoorWatcherPaused), output, options, async kiosk => {
				var serverMark = await kiosk.Server.MarkAsync();
				var mark = kiosk.Log.Mark();
				await kiosk.WaitForLogLineAsync(WatcherPollLine, mark, TimeSpan.FromSeconds(30));	// DoorWatcher radi na slobodnom Start-u
				await kiosk.Locker.BlockDoorAsync(ServiceDoor);
				kiosk.Trace($"vrata {ServiceDoor} blokirana");

				mark = kiosk.Log.Mark();
				var opening = await kiosk.ScanAsync(ServiceCode, WorkflowState.ServiceDoorOpening);
				var scanMark = kiosk.Log.Mark();
				await kiosk.Locker.ScanBarcodeAsync(kiosk.Data.CourierQrCode);
				kiosk.Trace($"skeniran QR kurira {kiosk.Data.CourierQrCode} (servisna vrata se otvaraju)");
				await kiosk.WaitForLogLineAsync(BusyScanLine, scanMark);

				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, opening.Line, TimeSpan.FromSeconds(30));
				Assert.Equal((WorkflowState.ServiceDoorOpening, "ServiceDoorHandled"), (toStart.From, toStart.Trigger));
				Assert.True(kiosk.Log.FirstLineAfter(scanMark, BusyScanLine) < toStart.Line, "QR mora biti odbijen dok se servisna vrata otvaraju");
				await kiosk.Ui.WaitForPageAsync("StartPage");

				var paused = kiosk.Log.FirstLineAfter(mark, WatcherPausedLine);
				Assert.True(paused < opening.Line, "DoorWatcher mora biti pauziran pre otvaranja servisnih vrata");
				var poll = kiosk.Log.FirstLineAfter(paused!.Value, WatcherPollLine);
				Assert.True(poll == null || poll > toStart.Line, "DoorWatcher ne sme da proverava vrata dok se servisna vrata otvaraju");
				await kiosk.WaitForLogLineAsync(WatcherResumedLine, toStart.Line);

				// Skener je barkod odbio odmah — ne sme da bude pročitan kasnije i prihvaćen kao nov posle povratka na Start.
				await Task.Delay(TimeSpan.FromSeconds(3));
				Assert.Equal(new[] { toStart }, kiosk.Log.TransitionsAfter(opening.Line + 1));
				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "KurirAutorizacija");
			});
		}

		/// <summary>
		/// Pošiljalac, potvrda ne prođe (503) → „Preuzmi paket" → vrata otvorena → oba modula isključena pa uključena: tok
		/// ostaje, praćenje vrata nastavlja, DoorWatcher ne javlja promenu statusa, kiosk je zauzet. Start i slobodan kiosk tek
		/// po zatvaranju vrata. Sledeća neuspela potvrda ponovo nudi „Preuzmi paket". OperationTimeout (rok praćenja) 40 s.
		/// </summary>
		[SkippableFact]
		public Task SenderReturnPackage_ModulesOffAndOn_TrackingContinues_FreeOnlyAfterClose()
		{
			var options = new KioskOptions {
				ConfigureClient = c => {
					c["General"]!["OperationTimeout"] = 40000;
					c["Locker"]!["DoorWatcherFrequency"] = 1000;
				},
			};
			return KioskSession.RunAsync(nameof(SenderReturnPackage_ModulesOffAndOn_TrackingContinues_FreeOnlyAfterClose), output, options, async kiosk => {
				var (_, door) = await DepositConfirmationFailedTests.SenderDepositUntilDoorOpenAsync(kiosk);
				await DepositConfirmationFailedTests.ArmServerDownAsync(kiosk);
				await kiosk.CloseDoorAsync(door, WorkflowState.SenderDepositConfirmationFailed, TimeSpan.FromSeconds(30));
				await DepositConfirmationFailedTests.AssertButtonsAsync(kiosk, retry: true, returnPackage: true, leave: false);

				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync(DepositConfirmationFailedTests.ReturnPackage);
				Assert.Equal(door, (await kiosk.WaitForUnlockAsync(unlockMark)).Door);
				await kiosk.WaitForLogLineAsync(DepositConfirmationFailedTests.ReopenedLine, mark);
				await kiosk.WaitForDoorOpenAsync(door, DepositConfirmationFailedTests.Page);

				await kiosk.Locker.SetModuleAsync(1, isOn: false);
				await kiosk.Locker.SetModuleAsync(2, isOn: false);
				kiosk.Trace("moduli 1 i 2 isključeni");
				await Task.Delay(TimeSpan.FromSeconds(5));	// nekoliko neuspelih provera praćenja (2 s po proveri)
				Assert.Empty(kiosk.Log.TransitionsAfter(mark));
				Assert.True(await IsBusyAsync(kiosk), "status.lock: kiosk mora biti zauzet dok se prate vrata");

				await kiosk.Locker.SetModuleAsync(1, isOn: true);
				await kiosk.Locker.SetModuleAsync(2, isOn: true);
				kiosk.Trace("moduli 1 i 2 uključeni");
				Assert.Empty(kiosk.Log.TransitionsAfter(mark));
				Assert.True(await IsBusyAsync(kiosk), "status.lock: kiosk mora biti zauzet dok su vrata otvorena");

				var toStart = await kiosk.CloseDoorAsync(door, WorkflowState.Start);
				Assert.Equal((WorkflowState.SenderDepositConfirmationFailed, "SenderPackageReturned"), (toStart.From, toStart.Trigger));
				Assert.Empty(kiosk.Log.LinesAfter(mark, "Paketomat je u funkciji"));	// DoorWatcher pauziran, status se nije menjao
				await kiosk.Ui.WaitForPageAsync("StartPage");
				await Poll.UntilAsync(async () => !await IsBusyAsync(kiosk), TimeSpan.FromSeconds(10),
										"status.lock: kiosk slobodan posle zatvaranja vrata", kiosk.EnsureClientAlive);
				kiosk.Trace("kiosk slobodan");

				// Sledeće ostavljanje sa neuspelom potvrdom ponovo nudi „Preuzmi paket".
				var (_, nextDoor) = await DepositConfirmationFailedTests.SenderDepositUntilDoorOpenAsync(kiosk);
				await kiosk.CloseDoorAsync(nextDoor, WorkflowState.SenderDepositConfirmationFailed, TimeSpan.FromSeconds(30));
				await DepositConfirmationFailedTests.AssertButtonsAsync(kiosk, retry: true, returnPackage: true, leave: false);
				var next = await DepositConfirmationFailedTests.ReturnPackageAsync(kiosk, nextDoor, WorkflowState.Start);
				Assert.Equal("SenderPackageReturned", next.Trigger);
				await kiosk.Ui.WaitForPageAsync("StartPage");
				await kiosk.Server.ClearFaultsAsync();
			});
		}

		/// <summary>Busy iz status.lock klijenta (fajl se kratko ne može pročitati dok ga klijent zamenjuje).</summary>
		private static async Task<bool> IsBusyAsync(KioskSession kiosk)
		{
			var status = await Poll.UntilAsync(kiosk.ReadStatus, TimeSpan.FromSeconds(5), "status.lock", kiosk.EnsureClientAlive);
			return status["Busy"]!.GetValue<bool>();
		}
	}
}
