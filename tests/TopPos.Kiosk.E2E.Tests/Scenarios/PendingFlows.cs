using TopPos.E2E.Tests.Infrastructure;
using TopPos.Kiosk.Workflow;

namespace TopPos.E2E.Tests.Scenarios
{
	// v4: zamene za pomoćne metode v2 scenarija čiji tokovi još nisu u v4 kiosku (F3). Testovi koji ih koriste imaju
	// [PendingFlow] i ne pokreću se; ovde su samo da se preneti fajlovi kompajliraju bez izmena. Kad stigne pravi fajl
	// (npr. CourierTests.cs iz v2), odgovarajuća klasa se briše odavde.

	internal static class CourierTests
	{
		internal static Task DepositAsync(KioskSession kiosk) => throw Pending();

		internal static Task OpenDepositScanAsync(KioskSession kiosk) => throw Pending();

		internal static Task LoginAsync(KioskSession kiosk, WorkflowState expect) => throw Pending();

		private static InvalidOperationException Pending() => new("Tok kurira još nije u v4 kiosku (F3).");
	}

	internal static class PendingKioskSession
	{
		/// <summary>Kraj Windows sesije (WM_QUERYENDSESSION) — v4 ga još ne obrađuje (F7); v2 WindowsSessionEnd nije prenet.</summary>
		internal static Task<IReadOnlyList<bool>> EndWindowsSessionAsync(this KioskSession kiosk)
			=> throw new InvalidOperationException("Kraj Windows sesije još nije u v4 kiosku (F7).");
	}

	internal static class ExpiredPickupTests
	{
		internal static Task<(SimPackage Package, long ServerMark)> PickUpUntilScanAsync(KioskSession kiosk)
			=> throw new InvalidOperationException("Preuzimanje isteklih paketa još nije u v4 kiosku (F3).");

		internal static void AssertStillOnServer(List<SimPackage> packages, SimPackage package)
			=> throw new InvalidOperationException("Preuzimanje isteklih paketa još nije u v4 kiosku (F3).");
	}
}
