using TopPos.Kiosk.Workflow;
using TopPos.Service;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Pomoćnici za Nivo-1 testove: konstrukcija <see cref="WorkflowMachine"/>-a (čista logika, bez UI/hardvera)
	/// i kratko dovođenje u traženo stanje preko trigera. OnEntry handleri (GoTo* render) NISU zakačeni u testu —
	/// machine izvršava samo svoje interne OnEntry akcije (LogEntry, _context.Reset, ExpiredPackagesPickedUp flag).
	/// </summary>
	internal static class Mk
	{
		public static WorkflowMachine Machine(WorkflowContext ctx, bool enableBox = false, bool confirmPickup = false, bool penPos = false)
			=> new(ctx, () => enableBox, () => confirmPickup, () => penPos, logger: null);

		/// <summary>Machine doveden u Start (Idle → Reset → Start).</summary>
		public static async Task<WorkflowMachine> Started(WorkflowContext ctx, bool enableBox = false, bool confirmPickup = false, bool penPos = false)
		{
			var m = Machine(ctx, enableBox, confirmPickup, penPos);
			await m.FireAsync(WorkflowTrigger.Reset);
			return m;
		}

		/// <summary>Automatska prijava kurira iz Start-a: CourierBarcodeScanned → CourierAuthenticating → CourierLoginSuccessful.</summary>
		public static async Task CourierLogin(WorkflowMachine m)
		{
			await m.FireAsync(m.CourierBarcodeScanned, "QR-123");
			await m.FireAsync(WorkflowTrigger.CourierLoginSuccessful);
		}

		/// <summary>Lista od n dummy isteklih paketa (sadržaj nebitan za guard testove — bitan je Count).</summary>
		public static List<BarkodPozicija> Packages(int n)
		{
			var list = new List<BarkodPozicija>();
			for (int i = 1; i <= n; i++) list.Add(new BarkodPozicija { Barkod = $"PKG{i}", BrojPozicije = i });
			return list;
		}
	}
}
