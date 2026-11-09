namespace TopPos.Client.WPF.Workflow
{
	/// <summary>
	/// Svi mogući događaji koji menjaju stanje workflow-a. Imenovani po onome što se desilo
	/// (npr. CodeAccepted), ne po sledećoj stranici (NextPage). Triger ne zna gde će ga state
	/// machine odvesti — to je odgovornost konfiguracije u <see cref="WorkflowMachine"/>.
	///
	/// Trigeri sa parametrima (npr. CourierBarcodeScanned, DepositBarcodeAccepted) registruju se
	/// kao <c>StateMachine.SetTriggerParameters&lt;T&gt;</c> u WorkflowMachine konstruktoru i pozivaju
	/// se preko <c>FireAsync(parameterizedTrigger, args...)</c>.
	/// </summary>
	public enum WorkflowTrigger
	{
		/// <summary>Globalni reset — vraća workflow na Start stranicu. Dozvoljen iz svakog stanja.</summary>
		Reset,

		// ─── Iz Start stranice ──────────────────────────────────────────────────
		/// <summary>Skeniran je barkod koji zahteva courier manual login. Parametar: barkod (string).</summary>
		CourierBarcodeScannedRequiringManualLogin,
		/// <summary>Skeniran je kurirski barkod koji se proverava direktno na serveru (bez manual login-a). Parametar: barkod (string).</summary>
		CourierBarcodeScanned,
		/// <summary>Courier se uspešno prijavio (manual ili automatski). Routing odlučuje gde dalje.</summary>
		CourierLoginSuccessful,
		/// <summary>Server je odbio prijavu kurira ili nije odgovorio — automatska prijava: Start; ručna: nazad na unos koda.</summary>
		CourierLoginFailed,
		/// <summary>Kurir je ručno uneo kod (uz skenirani barkod) — ide u proveru na serveru. Parametar: kod (string).</summary>
		CourierManualCodeEntered,
		/// <summary>Recipient je kliknuo "preuzmi paket".</summary>
		RecipientPickupRequested,

		// ─── Routing posle courier login-a ──────────────────────────────────────
		// Routing logika u OnEntry-ju virtualnog stanja odlučuje koji od ovih trigera da fire-uje.
		/// <summary>Konfiguracija nalaže da se prikaže box availability ekran.</summary>
		RouteToBoxAvailability,
		/// <summary>Konfiguracija nalaže explicit pickup confirmation.</summary>
		RouteToPickupConfirmation,
		/// <summary>Direktan put — ide u package handling routing.</summary>
		RouteToPackageHandling,

		// ─── Routing posle fetcha isteklih paketa ───────────────────────────────
		/// <summary>Postoji bar jedan istekao paket za pickup.</summary>
		HasExpiredPackages,
		/// <summary>Nema isteklih paketa — odmah u deposit prompt.</summary>
		NoExpiredPackages,

		// ─── CourierLockerBoxAvailability ───────────────────────────────────────
		/// <summary>Korisnik je kliknuo "Završi" na box availability ekranu.</summary>
		BoxAvailabilityCompleted,

		// ─── CourierPickupConfirmation ──────────────────────────────────────────
		/// <summary>Korisnik je potvrdio pickup.</summary>
		PickupConfirmed,
		/// <summary>Korisnik je odustao od pickup-a — ide u deposit prompt.</summary>
		PickupConfirmationCancelled,

		// ─── CourierPickupPrompt ────────────────────────────────────────────────
		/// <summary>Korisnik je odustao od pickup-a iz prompt-a — ide u Start.</summary>
		PickupPromptCancelled,
		/// <summary>Korisnik je kliknuo "preuzmi" na promp-tu paketa.</summary>
		PickupPromptPickupClicked,

		// ─── CourierPackagePickup ───────────────────────────────────────────────
		/// <summary>Vrata su uspešno otvorena i zatvorena posle preuzimanja.</summary>
		PickupDoorClosedAfterOpen,
		/// <summary>Vrata se nisu otvorila ili je inače pukla door action.</summary>
		PickupDoorActionFailed,

		// ─── CourierPackagePickupBarcodeScan ────────────────────────────────────
		/// <summary>Barkod je potvrđen ili je broj pokušaja iscrpljen — vraćamo se u prompt loop.</summary>
		PickupBarcodeResolved,
		/// <summary>Timeout barkod skenera — Reset.</summary>
		PickupBarcodeScanTimeout,

		// ─── CourierDepositPrompt ───────────────────────────────────────────────
		/// <summary>Korisnik je odustao od deposit-a — Reset.</summary>
		DepositPromptCancelled,
		/// <summary>Korisnik je kliknuo "ostavi paket".</summary>
		DepositPromptDepositClicked,

		// ─── CourierPackageDepositBarcodeScan ───────────────────────────────────
		/// <summary>Skeniran barkod paketa — ide u proveru na serveru (dodela vrata). Parametar: barkod (string).</summary>
		DepositBarcodeScanned,
		/// <summary>Korisnik je kliknuo Cancel na deposit barcode scan (i tokom provere) — Reset.</summary>
		DepositBarcodeScanCancelled,
		/// <summary>Timeout barkod skenera u deposit-u — Reset.</summary>
		DepositBarcodeScanTimeout,

		// ─── CourierDepositBarcodeValidating (odgovor servera) ──────────────────
		/// <summary>Server vratio doorNumber — ide u package deposit. Parametri: barkod, doorNumber.</summary>
		DepositBarcodeAccepted,
		/// <summary>Server odbio barkod ili nije odgovorio — poruka, pa nazad na skeniranje.</summary>
		DepositBarcodeRejected,

		// ─── CourierPackageDeposit ──────────────────────────────────────────────
		/// <summary>Vrata deposit pozicije se nisu otvorila — vraćamo na barcode scan (kurir može da pokuša ponovo / drugi paket).</summary>
		DepositDoorOpenFailed,
		/// <summary>Vrata zatvorena, server je potvrdio ostavljanje (automatska potvrda) — vraćamo na barcode scan za sledeći paket.</summary>
		DepositDoorClosedAutoConfirm,
		/// <summary>Server nije potvrdio ostavljanje ni posle drugog pokušaja (automatska ili eksplicitna potvrda) — ekran sa izborom.</summary>
		DepositConfirmationFailed,
		/// <summary>Vrata zatvorena, eksplicitna potvrda potrebna — ide u confirmation ekran.</summary>
		DepositDoorClosedExplicitConfirm,
		/// <summary>Korisnik je tražio veći box dok su vrata još otvorena.</summary>
		DepositChangeBoxRequested,
		/// <summary>Korisnik je odustao dok su vrata još otvorena — Reset posle zatvaranja vrata.</summary>
		DepositCancelledLogout,

		// ─── CourierDepositConfirmation ─────────────────────────────────────────
		/// <summary>Korisnik je potvrdio deposit i server ga je potvrdio — vraća se na barcode scan za sledeći.</summary>
		DepositConfirmationConfirmed,
		/// <summary>Korisnik je otkazao confirmation — vraća se na barcode scan za sledeći.</summary>
		DepositConfirmationCancelled,

		// ─── CourierDepositConfirmationFailed ───────────────────────────────────
		/// <summary>„Ponovi slanje" — server je potvrdio ostavljanje; barcode scan za sledeći paket.</summary>
		DepositConfirmationRetrySucceeded,
		/// <summary>„Prekini i preuzmi paket" — kurir je uzeo paket nazad i zatvorio vrata (ili se vrata nisu otvorila); barcode scan.</summary>
		DepositPackageReturned,
		/// <summary>„Ostavi paket i nastavi" (ne zna se da li je server primio potvrdu) — paket ostaje u vratima; barcode scan.</summary>
		DepositLeftUnconfirmed,
		/// <summary>Kurir nije izabrao ništa u roku — paket ostaje u vratima, potvrda se ne šalje; Start.</summary>
		DepositConfirmationFailedTimeout,

		// ─── RecipientCodeInput ─────────────────────────────────────────────────
		/// <summary>Recipient je uneo unlock kod, sledi unos broja telefona.</summary>
		RecipientCodeAccepted,
		/// <summary>Recipient je uneo unlock kod a u konfiguraciji je default phone — preskače phone input, ide u proveru.</summary>
		RecipientCodeAcceptedWithDefaultPhone,

		// ─── RecipientPhoneInput ────────────────────────────────────────────────
		/// <summary>Recipient je potvrdio broj telefona — ide u proveru na serveru. Parametar: telefon (string).</summary>
		RecipientPhoneEntered,

		// ─── RecipientValidating (odgovor servera na RRPP) ──────────────────────
		/// <summary>Server je validirao recipient (telefon + kod), iznos = 0 — odmah pickup.</summary>
		RecipientNoPaymentNeeded,
		/// <summary>Server je validirao recipient, iznos > 0, terminal dostupan — ide na payment.</summary>
		RecipientPaymentRequired,
		/// <summary>Iznos > 0 ali terminal nije instaliran — Reset uz info poruku.</summary>
		RecipientPaymentNotPossible,
		/// <summary>Server validacija recipient-a nije uspela — Reset uz error poruku.</summary>
		RecipientValidationFailed,

		// ─── RecipientPayment ───────────────────────────────────────────────────
		/// <summary>POS uspešan, server potvrdio. Machine dinamički bira FiscalReceipt vs Pickup po PenPOS konfiguraciji.</summary>
		RecipientPaymentApproved,
		/// <summary>POS odbio ili greška — Reset.</summary>
		RecipientPaymentDeclined,

		// ─── RecipientFiscalReceipt ─────────────────────────────────────────────
		/// <summary>Korisnik je kliknuo "Završi" na fiscal receipt ekranu.</summary>
		RecipientFiscalReceiptCompleted,

		// ─── RecipientPackagePickup ─────────────────────────────────────────────
		/// <summary>Recipient je preuzeo paket i zatvorio vrata — Start.</summary>
		RecipientPickupComplete,
		/// <summary>Otvaranje vrata recipient-u nije uspelo ili vrata ne javljaju zatvaranje — Start (posle prijave greške pozicije).</summary>
		RecipientPickupDoorActionFailed,

		// ─── Recipient — preuzimanje na zahtev Agenta ───────────────────────────
		// Ishodi provere na serveru su isti trigeri kao u lokalnom toku (RecipientNoPaymentNeeded, RecipientPaymentRequired,
		// RecipientValidationFailed); odredišta određuje RecipientRemoteValidating.
		/// <summary>Agent je zatražio preuzimanje (<c>RecipientPickup</c>); parametar je <see cref="RecipientRemotePickupRequest"/>.</summary>
		RecipientRemotePickupStarted,
		/// <summary>Korisnik je na stranici potvrde uneo tačan kod prisustva — otvaranje vrata.</summary>
		RecipientRemoteAccepted,
		/// <summary>Korisnik je na stranici potvrde kliknuo „Odustani" — Start, ništa se ne otvara.</summary>
		RecipientRemoteDeclined,
		/// <summary>Korisnik nije uneo tačan kod prisustva u roku — Start, ništa se ne otvara.</summary>
		RecipientRemoteNoAnswer,
		/// <summary>Kod prisustva je pogrešno unet i poslednji pokušaj je iskorišćen — Start, ništa se ne otvara.</summary>
		RecipientRemoteCodeFailed,

		// ─── Sender deposit (pošiljalac ostavlja paket) ─────────────────────────
		/// <summary>Korisnik je kliknuo "Ostavi paket" na Start ekranu — ide pravo na skeniranje barkoda.</summary>
		SenderDepositRequested,
		/// <summary>Pošiljalac je skenirao barkod paketa — ide u proveru na serveru (dodela vrata). Parametar: barkod (string).</summary>
		SenderBarcodeScanned,
		/// <summary>Server vratio doorNumber — ide u package deposit. Parametri: barkod, doorNumber.</summary>
		SenderBarcodeAccepted,
		/// <summary>Server odbio barkod ili nije odgovorio — poruka, pa nazad na skeniranje.</summary>
		SenderBarcodeRejected,
		/// <summary>Korisnik je kliknuo Cancel na sender barcode scan (i tokom provere) — Reset.</summary>
		SenderBarcodeScanCancelled,
		/// <summary>Timeout barkod skenera u sender deposit-u — Reset.</summary>
		SenderBarcodeScanTimeout,
		/// <summary>Vrata sender deposit pozicije se nisu otvorila — vraćamo na barcode scan (ponovni pokušaj).</summary>
		SenderDoorOpenFailed,
		/// <summary>Pošiljalac je tražio veći box — posle zatvaranja vrata ponovo skenira paket.</summary>
		SenderChangeBoxRequested,
		/// <summary>Vrata zatvorena = paket ostavljen, server je potvrdio (šalje se inline) — pravo na Start (nema confirmation ekrana).</summary>
		SenderDoorClosedConfirm,
		/// <summary>Server nije potvrdio ostavljanje ni posle drugog pokušaja — ekran sa izborom.</summary>
		SenderConfirmationFailed,
		/// <summary>„Ponovi slanje" — server je potvrdio ostavljanje; Start.</summary>
		SenderConfirmationRetrySucceeded,
		/// <summary>„Prekini i preuzmi paket" — pošiljalac je uzeo paket nazad i zatvorio vrata (ili se vrata nisu otvorila); Start.</summary>
		SenderPackageReturned,
		/// <summary>„Ostavi paket i nastavi" (ne zna se da li je server primio potvrdu) — paket ostaje u vratima; Start.</summary>
		SenderLeftUnconfirmed,
		/// <summary>Pošiljalac nije izabrao ništa u roku — paket ostaje u vratima, potvrda se ne šalje; Start.</summary>
		SenderConfirmationFailedTimeout,
		/// <summary>Pošiljalac je odustao dok su vrata još otvorena — Reset posle zatvaranja vrata.</summary>
		SenderCancelled,

		// ─── Servis (tehničar) ──────────────────────────────────────────────────
		/// <summary>Skeniran je servisni kod na Start ekranu — otvaranje servisnih vrata.</summary>
		ServiceBarcodeScanned,
		/// <summary>Otvaranje servisnih vrata je završeno (uspeh ili već prijavljen neuspeh) — povratak na Start.</summary>
		ServiceDoorHandled,

		// ─── Agent — servisna sesija ────────────────────────────────────────────
		/// <summary>Agent je pokrenuo servisnu sesiju (<c>BeginRemoteService</c>; parametar je <c>sessionId</c>).</summary>
		RemoteServiceStarted,
		/// <summary>Servisna sesija je završena (potvrda na ekranu ili u portalu, istek roka, neuspela prijava) — Start.</summary>
		RemoteServiceEnded
	}
}