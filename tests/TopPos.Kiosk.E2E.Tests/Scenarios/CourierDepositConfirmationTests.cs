using TopPos.Kiosk.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Ekran eksplicitne potvrde ostavljanja (ConfirmPackageDeposition=true): vrata su zatvorena, pa istek vremena znači
	/// potvrdu (ranije Reset na Start bez potvrde — server bi vrata dodelio sledećem paketu). Potvrda koja čeka server se
	/// ne prekida: Otkaži tada nema dejstva.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class CourierDepositConfirmationTests(ITestOutputHelper output)
	{
		private const string Endpoint = "KurirPunjenjePozicijePotvrda";
		private const string ConfirmLine = "kurir je potvrdio ostavljanje";
		private const string TimeoutLine = "isteklo vreme za potvrdu ostavljanja";
		private const string CancelIgnoredLine = "kurir nije potvrdio ostavljanje (Otkazi) ignorisano";
		// OperationTimeout u E2E konfiguraciji klijenta je 20 s
		private static readonly TimeSpan __confirmationTimeout = TimeSpan.FromSeconds(40);

		/// <summary>Kurir ništa ne pritisne: istek vremena šalje potvrdu, server vodi paket u vratima, kiosk ide na skeniranje.</summary>
		[SkippableFact]
		public Task Timeout_SendsConfirmation_BackToScan()
			=> KioskSession.RunAsync(nameof(Timeout_SendsConfirmation_BackToScan), output, async kiosk => {
				var (package, door) = await DepositConfirmationFailedTests.CourierDepositUntilDoorOpenAsync(kiosk);
				var serverMark = await kiosk.Server.MarkAsync();
				var mark = kiosk.Log.Mark();
				await kiosk.CloseDoorAsync(door, WorkflowState.CourierDepositConfirmation);
				await kiosk.Ui.WaitForPageAsync("CourierDepositConfirmationPage");

				var confirmed = await kiosk.WaitForStateAsync(WorkflowState.CourierPackageDepositBarcodeScan, mark, __confirmationTimeout);
				Assert.Equal((WorkflowState.CourierDepositConfirmation, "DepositConfirmationConfirmed"), (confirmed.From, confirmed.Trigger));
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, TimeoutLine));
				await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");
				await CourierTests.LogoutFromScanAsync(kiosk);

				await AssertConfirmedAsync(kiosk, package, door, serverMark);
				Assert.Empty(kiosk.Log.IgnoredTriggersAfter(mark));
			});

		/// <summary>
		/// Potvrdi, pa Otkaži dok server kasni sa odgovorom: Otkaži se ignoriše, a posle odgovora kiosk ide na skeniranje
		/// potvrdom (ne otkazivanjem).
		/// </summary>
		[SkippableFact]
		public Task CancelWhileConfirmationPending_IsIgnored()
			=> KioskSession.RunAsync(nameof(CancelWhileConfirmationPending_IsIgnored), output, async kiosk => {
				var (package, door) = await DepositConfirmationFailedTests.CourierDepositUntilDoorOpenAsync(kiosk);
				await kiosk.CloseDoorAsync(door, WorkflowState.CourierDepositConfirmation);
				await kiosk.Ui.WaitForPageAsync("CourierDepositConfirmationPage");

				await kiosk.Server.ArmFaultAsync(Endpoint, delayMs: 3000);	// ispod RequestTimeout-a (5 s): odgovor stiže, ne ide u red
				kiosk.Trace($"server kasni sa odgovorom na {Endpoint} (3 s)");
				var serverMark = await kiosk.Server.MarkAsync();
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("CourierDepositConfirmationPage.Confirm");
				await kiosk.WaitForLogLineAsync(ConfirmLine, mark);
				await kiosk.Ui.TapAsync("CourierDepositConfirmationPage.Cancel");
				await kiosk.WaitForLogLineAsync(CancelIgnoredLine, mark);

				var confirmed = await kiosk.WaitForStateAsync(WorkflowState.CourierPackageDepositBarcodeScan, mark);
				Assert.Equal((WorkflowState.CourierDepositConfirmation, "DepositConfirmationConfirmed"), (confirmed.From, confirmed.Trigger));
				Assert.True(kiosk.Log.FirstLineAfter(mark, CancelIgnoredLine) < confirmed.Line, "Otkaži je stigao tek posle odgovora servera.");
				Assert.Single(kiosk.Log.TransitionsAfter(mark));
				await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");
				await CourierTests.LogoutFromScanAsync(kiosk);

				await AssertConfirmedAsync(kiosk, package, door, serverMark);
				Assert.Empty(kiosk.Log.IgnoredTriggersAfter(mark));
			});

		private async Task AssertConfirmedAsync(KioskSession kiosk, SimPackage package, int door, long serverMark)
		{
			var confirm = Assert.Single(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == Endpoint);
			Assert.Equal(200, confirm.StatusCode);
			Assert.Equal((package.Barkod, door.ToString()), (confirm.RequestValue("Barkod"), confirm.RequestValue("BrojPozicije")));
			Assert.Equal(door, Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod).BrojPozicije);
			output.WriteLine($"{Endpoint}: 200, paket u vratima {door}");
		}
	}
}
