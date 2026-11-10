using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Isti tokovi kao kod NCU24L, sa kontrolerom vrata Serial (COM, raspored 4 × 8 kao app-configuration-Ananas.json;
	/// vault: backlog/E2E — serijski kontroler vrata). Tela scenarija su zajednička sa NCU24L testovima; ovde se razlikuju
	/// drajver, tajming ponavljanja i ishod kad kontroler ne odgovara. Traži treći com0com par (podrazumevano COM22↔COM23),
	/// bez njega se testovi preskaču.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class SerialControllerTests(ITestOutputHelper output)
	{
		[SkippableFact]
		public Task NoPayment_DoorOpens_ClosingDoorReturnsToStart()
			=> RunAsync(nameof(NoPayment_DoorOpens_ClosingDoorReturnsToStart), RecipientPickupTests.NoPaymentPickupAsync);

		[SkippableFact]
		public Task CourierDeposit_ScanOpenCloseConfirm_ServerStoresPosition()
			=> RunAsync(nameof(CourierDeposit_ScanOpenCloseConfirm_ServerStoresPosition), CourierTests.DepositAsync);

		[SkippableFact]
		public Task BlockedDoor_TwoAttempts_ReportsDoorErrorAndReturnsToStart()
			=> RunAsync(nameof(BlockedDoor_TwoAttempts_ReportsDoorErrorAndReturnsToStart), RecipientFailureTests.BlockedDoorAsync);

		[SkippableFact]
		public Task ModuleOff_NoControllerResponse_ReportsUnknownDoorAndReturnsToStart()
			=> RunAsync(nameof(ModuleOff_NoControllerResponse_ReportsUnknownDoorAndReturnsToStart), RecipientFailureTests.ModuleOffAsync);

		[SkippableFact]
		public Task DoorLeftOpen_TrackingTimesOut_ReportsOpenDoorAndReturnsToStart()
			=> RunAsync(nameof(DoorLeftOpen_TrackingTimesOut_ReportsOpenDoorAndReturnsToStart), RecipientFailureTests.DoorLeftOpenAsync);

		private Task RunAsync(string testName, Func<KioskSession, Task> scenario)
		{
			E2ESettings.RequireEnabled();
			E2ESettings.RequireLockerSerialPorts();
			return KioskSession.RunAsync($"Serial-{testName}", output, KioskOptions.SerialController(E2ESettings.Current), async kiosk => {
				kiosk.Trace($"kontroler Serial: klijent {kiosk.Settings.LockerClientPort}, simulator {kiosk.Settings.LockerSimulatorPort}");
				// Klijent je stvarno napravio serijski drajver (ne NCU24L iz zajedničkog šablona).
				Assert.NotEmpty(kiosk.Log.LinesAfter(0, "LockControllerClientSerial: inicijalizacija zavrsena"));
				await scenario(kiosk);
			});
		}
	}
}
