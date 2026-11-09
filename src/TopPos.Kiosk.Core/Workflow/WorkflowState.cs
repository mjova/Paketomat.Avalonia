namespace TopPos.Kiosk.Workflow
{
	/// <summary>
	/// Sva diskretna stanja workflow-a kioska. Svako stanje odgovara jednoj UI stranici
	/// (osim Idle koji je samo početno stanje pre prvog Reset trigger-a).
	/// </summary>
	public enum WorkflowState
	{
		Idle,

		Start,

		// Recipient (preuzimanje paketa)
		RecipientCodeInput,
		RecipientPhoneInput,

		/// <summary>
		/// Virtuelno stanje: provera koda i telefona na serveru (RRPP) je u toku; prethodni ekran ostaje prikazan, ali
		/// je neaktivan. OnEntry fire-uje <see cref="WorkflowTrigger.RecipientNoPaymentNeeded"/>,
		/// <see cref="WorkflowTrigger.RecipientPaymentRequired"/>, <see cref="WorkflowTrigger.RecipientPaymentNotPossible"/>
		/// ili <see cref="WorkflowTrigger.RecipientValidationFailed"/>. Postoji kao stanje da bi se Reset (Home, tajmer)
		/// koji stigne dok se čeka server naredio ispred zakasnelog odgovora — inače bi odgovor mogao da otvori vrata
		/// sledećem kupcu koji je u međuvremenu stigao do istog ekrana.
		/// </summary>
		RecipientValidating,
		RecipientPayment,
		RecipientFiscalReceipt,
		RecipientPackagePickup,

		// Recipient — preuzimanje na zahtev Agenta (RecipientPickup): podrška je unela šifru i telefon umesto primaoca.

		/// <summary>
		/// Virtuelno stanje: provera šifre i telefona iz zahteva Agenta na serveru (RRPP) je u toku; Start ekran ostaje
		/// prikazan, uz poruku. OnEntry fire-uje <see cref="WorkflowTrigger.RecipientNoPaymentNeeded"/>,
		/// <see cref="WorkflowTrigger.RecipientPaymentRequired"/> (bez kartice — Start) ili
		/// <see cref="WorkflowTrigger.RecipientValidationFailed"/>. Reset se ignoriše: korisnik ovde još ništa nije pokrenuo,
		/// poziv je ograničen rokom zahteva, a operacija Agenta mora da dobije ishod servera.
		/// </summary>
		RecipientRemoteValidating,

		/// <summary>
		/// Unos koda prisustva koji je operater diktirao korisniku: tako korisnik potvrđuje da je pred paketomatom. Tačan kod
		/// vodi u <see cref="RecipientPackagePickup"/>; „Odustani", istek roka, iscrpljeni pokušaji i Home (Reset) vode na
		/// Start bez otvaranja.
		/// </summary>
		RecipientRemoteConfirmation,

		// Courier - zajedničko (posle login-a)
		CourierManualLogin,

		/// <summary>
		/// Virtuelno stanje: provera ručno unetog koda kurira na serveru je u toku (ekran za unos ostaje prikazan, ali
		/// neaktivan). OnEntry fire-uje <see cref="WorkflowTrigger.CourierLoginSuccessful"/> ili
		/// <see cref="WorkflowTrigger.CourierLoginFailed"/> (nazad na unos koda). Postoji kao stanje da bi se Reset koji
		/// stigne dok se čeka server naredio ispred zakasnelog odgovora — inače bi uspeh za prethodnog kurira prijavio
		/// sledećeg koji je u međuvremenu stigao do istog ekrana.
		/// </summary>
		CourierManualAuthenticating,

		/// <summary>
		/// Virtuelno stanje: automatska prijava kurira skeniranim barkodom (bez manual login UI-ja) je u toku.
		/// OnEntry šalje barkod serveru i fire-uje <see cref="WorkflowTrigger.CourierLoginSuccessful"/> ili
		/// <see cref="WorkflowTrigger.CourierLoginFailed"/>. Postoji kao stanje da bi kiosk tokom čekanja servera bio
		/// zauzet (status.lock Busy=true, Role=Courier), a ulazi sa Start ekrana ignorisani.
		/// </summary>
		CourierAuthenticating,
		CourierLockerBoxAvailability,
		CourierPickupConfirmation,

		/// <summary>
		/// Virtuelno (loading) stanje. Posle uspešnog courier login-a, BoxAvailability complete-a, ili PickupConfirmed-a,
		/// ulazi se ovde da se fetch-uje lista isteklih paketa sa servera. OnEntry handler radi async fetch i fire-uje
		/// <see cref="WorkflowTrigger.HasExpiredPackages"/> ili <see cref="WorkflowTrigger.NoExpiredPackages"/>.
		/// Postoji kao stanje (a ne kao guard) jer fetch je async i graf treba da pokaže rutu jasno.
		/// </summary>
		CourierFetchingExpiredPackages,

		// Courier - pickup (preuzimanje isteklih paketa)
		CourierPickupPrompt,
		CourierPackagePickup,
		CourierPackagePickupBarcodeScan,

		// Courier - deposit (ostavljanje paketa)
		CourierDepositPrompt,
		CourierPackageDepositBarcodeScan,

		/// <summary>
		/// Virtuelno stanje: server dodeljuje vrata za skenirani barkod paketa (ekran za skeniranje ostaje prikazan).
		/// OnEntry fire-uje <see cref="WorkflowTrigger.DepositBarcodeAccepted"/> ili
		/// <see cref="WorkflowTrigger.DepositBarcodeRejected"/> (nazad na skeniranje). Postoji kao stanje da bi se Reset
		/// koji stigne dok se čeka server naredio ispred zakasnelog odgovora — inače bi odgovor za prethodni paket
		/// otvorio vrata sledećem koji je u međuvremenu stigao do istog ekrana.
		/// </summary>
		CourierDepositBarcodeValidating,
		CourierPackageDeposit,
		CourierDepositConfirmation,

		/// <summary>
		/// Server nije potvrdio ostavljanje (ni posle drugog pokušaja). Paket je u zatvorenim vratima, a server poziciju zauzima
		/// tek potvrdom — kurir ne nastavlja dok ne izabere: ponovo pošalji, uzmi paket nazad, ili (kad se ne zna da li je
		/// server primio potvrdu) ostavi paket. Ista stranica kao <see cref="SenderDepositConfirmationFailed"/>.
		/// </summary>
		CourierDepositConfirmationFailed,

		// Sender (pošiljalac ostavlja paket) — paralelan deposit tok, ulaz sa Start dugmeta "Ostavi paket".
		// Bez prompt ekrana (Start → odmah scan), bez confirmation ekrana (zatvaranje vrata = ostavljen paket)
		// i bez petlje za sledeći paket (svaki završetak → Start).
		SenderDepositBarcodeScan,

		/// <summary>
		/// Virtuelno stanje: server dodeljuje vrata za barkod koji je pošiljalac skenirao (ekran za skeniranje ostaje
		/// prikazan). OnEntry fire-uje <see cref="WorkflowTrigger.SenderBarcodeAccepted"/> ili
		/// <see cref="WorkflowTrigger.SenderBarcodeRejected"/> (nazad na skeniranje). Isti razlog kao
		/// <see cref="CourierDepositBarcodeValidating"/>: sledeći pošiljalac je samo jedan klik daleko.
		/// </summary>
		SenderDepositBarcodeValidating,
		SenderPackageDeposit,

		/// <summary>Kao <see cref="CourierDepositConfirmationFailed"/>, za pošiljaoca (svaki izlaz vodi na Start).</summary>
		SenderDepositConfirmationFailed,

		// Servis (tehničar) — status.lock Role=Service za sva Service* stanja.

		/// <summary>
		/// Virtuelno stanje: otvaranje servisnih vrata skeniranim servisnim kodom (Start ekran ostaje prikazan).
		/// OnEntry otvara vrata, kod neuspeha prikaže poruku i prijavi grešku serveru, pa fire-uje
		/// <see cref="WorkflowTrigger.ServiceDoorHandled"/>. Postoji kao stanje da bi kiosk tokom otvaranja bio
		/// zauzet (status.lock Busy=true, Role=Service), a ulazi sa Start ekrana ignorisani.
		/// </summary>
		ServiceDoorOpening,

		// Agent — status.lock Role=Agent (vlasnik kioska je Agent).

		/// <summary>
		/// Servisna sesija na zahtev Agenta (<c>BeginRemoteService</c>): prijava servisera kodom, pa otvaranje vrata sa ekrana
		/// ili iz portala. Izlazi se samo kroz <see cref="WorkflowTrigger.RemoteServiceEnded"/>: sesiju završava serviser,
		/// portal ili njen rok, pa se Reset (Home, rokovi stranica) ignoriše.
		/// </summary>
		RemoteService
	}
}