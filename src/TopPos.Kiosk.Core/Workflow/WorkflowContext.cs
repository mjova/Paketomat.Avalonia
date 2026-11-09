using Serilog;
using TopPos.Service;

namespace TopPos.Kiosk.Workflow
{
	/// <summary>
	/// Mutable kontekst koji prati workflow kroz stanja. Zamenjuje closure-state-ove i
	/// parameter-prosleđivanje koje je u trenutnom kodu rasuto kroz <c>GoTo*</c> metode.
	///
	/// Umesto <c>GoToRecipientPaymentPage(amount, unlockCode, phoneNumber, contextLogger)</c>,
	/// caller postavi <see cref="PaymentAmount"/>/<see cref="UnlockCode"/>/<see cref="RecipientPhoneNumber"/>
	/// na kontekst i fire-uje trigger; OnEntry handler za RecipientPayment čita iz konteksta.
	///
	/// <see cref="Reset"/> se zove pri svakom prelasku u <see cref="WorkflowState.Start"/> stanje.
	/// </summary>
	public class WorkflowContext
	{
		// ─── Logger ─────────────────────────────────────────────────────────────
		/// <summary>Kontekstualni logger, obogaćuje se property-jima tokom toka (UnlockCode, CourierId, PackageId).</summary>
		public ILogger Logger { get; set; } = Log.Logger;

		// ─── Recipient sesija ───────────────────────────────────────────────────
		public string? UnlockCode { get; set; }
		public string? RecipientPhoneNumber { get; set; }
		public decimal? PaymentAmount { get; set; }
		public int? RecipientPackageDoorNumber { get; set; }

		/// <summary><c>requestId</c> operacije Agenta kad je preuzimanje pokrenuo Agent (<c>RecipientPickup</c>); u lokalnom toku null.</summary>
		public string? RemotePickupRequestId { get; set; }

		/// <summary>Ishod preuzimanja za operaciju Agenta; postavlja se kad server dodeli vrata, u lokalnom toku null.</summary>
		public IRemotePickupProgress? RemotePickup { get; set; }

		// ─── Courier zajedničko ─────────────────────────────────────────────────
		public string? CourierCode { get; set; }

		/// <summary>Skenirani barkod kurira za ručnu prijavu (uz njega se šalje ručno uneti kod); čuva se i za ponovni unos.</summary>
		public string? CourierLoginBarcode { get; set; }

		// ─── Courier pickup ─────────────────────────────────────────────────────
		public List<BarkodPozicija>? RemainingExpiredPackages { get; set; }
		public BarkodPozicija? CurrentPickupPackage { get; set; }

		/// <summary>
		/// Da li je deposit prompt prikazan posle pickup-a (utiče na UI tekst). Ako je false,
		/// znači da se deposit pokrenuo bez prethodnog pickup-a (nije bilo isteklih paketa).
		/// </summary>
		public bool ExpiredPackagesPickedUp { get; set; }

		// ─── Courier deposit ────────────────────────────────────────────────────
		public string? DepositBarcode { get; set; }
		public int? DepositDoorNumber { get; set; }

		/// <summary>
		/// Briše sva polja na default. Zove se u OnEntry za <see cref="WorkflowState.Start"/> i
		/// na bilo kom Reset-u — workflow ne sme da curi state iz prethodne sesije.
		/// </summary>
		public void Reset()
		{
			Logger = Log.Logger;
			UnlockCode = null;
			RecipientPhoneNumber = null;
			PaymentAmount = null;
			RecipientPackageDoorNumber = null;
			RemotePickupRequestId = null;
			RemotePickup = null;
			CourierCode = null;
			CourierLoginBarcode = null;
			RemainingExpiredPackages = null;
			CurrentPickupPackage = null;
			ExpiredPackagesPickedUp = false;
			DepositBarcode = null;
			DepositDoorNumber = null;
		}
	}
}
