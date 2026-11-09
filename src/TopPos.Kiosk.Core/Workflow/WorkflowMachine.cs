using Microsoft.Extensions.Logging;
using Stateless;

namespace TopPos.Client.WPF.Workflow
{
	/// <summary>
	/// Centralna definicija celog kiosk workflow-a — recipient pickup, courier pickup, courier deposit.
	/// Ovo je <b>jedini izvor istine</b> o tome šta sledi posle čega.
	///
	/// Side-effect logika (otvaranje vrata, server pozivi, audio, timer) <b>nije ovde</b> — ona je
	/// u <c>MainWindowViewModel.GoTo*</c> metodama koje su zakačene na OnEntry callback-ove (Faza 2).
	/// Ovaj fajl samo modeluje "iz stanja A trigerom T idemo u stanje B".
	///
	/// Parametarski trigeri (npr. <see cref="DepositBarcodeAccepted"/>) registruju se u
	/// konstruktoru kao <c>StateMachine.TriggerWithParameters</c> i pozivaju se preko
	/// <c>FireAsync(parameterized, args...)</c>.
	///
	/// Faza 1: OnEntry callback-ovi samo loguju ulazak — proverava se da li grafova ima smisla.
	/// Faza 2+: OnEntry će pozivati postojeće <c>GoTo*</c> metode preko injektovanog interfejsa.
	/// </summary>
	public class WorkflowMachine
	{
		private readonly StateMachine<WorkflowState, WorkflowTrigger> _machine;
		private readonly WorkflowContext _context;
		private readonly ILogger? _logger;
		private readonly Func<bool> _enableLockerBoxAvailabilityPage;
		private readonly Func<bool> _confirmPackagePickup;
		private readonly Func<bool> _isPenPosDeviceAvailable;

		// Red naređanih trigera (vidi FireSerializedAsync). Stateless-ov FiringMode.Queued koristi nezaštićen Queue i
		// zastavicu, a mašini se pristupa iz više niti — zato red vodimo sami, pod lock-om.
		private readonly object _fireGate = new();
		private readonly Queue<Func<Task>> _pendingFires = new();
		private bool _firing;

		// ─── Parametarski trigeri (registracija u konstruktoru) ─────────────────
		/// <summary>Trigger sa barkodom kao parametrom — courier scan koji vodi u manual login.</summary>
		public StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<string> CourierBarcodeScannedRequiringManualLogin { get; }

		/// <summary>Trigger sa barkodom kao parametrom — courier scan koji se proverava direktno (automatska prijava).</summary>
		public StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<string> CourierBarcodeScanned { get; }

		/// <summary>Trigger sa barkodom paketa — kurir skenirao paket za deposit, sledi provera na serveru.</summary>
		public StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<string> DepositBarcodeScanned { get; }

		/// <summary>Trigger sa barkodom paketa — pošiljalac skenirao paket, sledi provera na serveru.</summary>
		public StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<string> SenderBarcodeScanned { get; }

		/// <summary>Trigger sa (barkod, doorNumber) — server validirao barkod paketa za deposit.</summary>
		public StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<string, int> DepositBarcodeAccepted { get; }

		/// <summary>Trigger sa (barkod, doorNumber) — server validirao barkod paketa za sender deposit.</summary>
		public StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<string, int> SenderBarcodeAccepted { get; }

		/// <summary>Trigger sa ručno unetim kodom kurira — sledi provera na serveru (barkod je u kontekstu).</summary>
		public StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<string> CourierManualCodeEntered { get; }

		/// <summary>Trigger sa kodom za otključavanje — primalac uneo/skenirao validan kod, sledi unos telefona.</summary>
		public StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<string> RecipientCodeAccepted { get; }

		/// <summary>Trigger sa kodom za otključavanje — kod prihvaćen uz telefon iz konfiguracije, sledi provera na serveru.</summary>
		public StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<string> RecipientCodeAcceptedWithDefaultPhone { get; }

		/// <summary>Trigger sa telefonom kao parametrom — primalac potvrdio broj telefona, sledi provera na serveru.</summary>
		public StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<string> RecipientPhoneEntered { get; }

		/// <summary>Trigger sa ishodom neuspele potvrde ostavljanja (kurir) — za ekran neuspele potvrde.</summary>
		public StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<DepositConfirmFailure> DepositConfirmationFailed { get; }

		/// <summary>Trigger sa ishodom neuspele potvrde ostavljanja (pošiljalac) — za ekran neuspele potvrde.</summary>
		public StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<DepositConfirmFailure> SenderConfirmationFailed { get; }

		/// <summary>Trigger sa <c>sessionId</c> servisne sesije Agenta.</summary>
		public StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<string> RemoteServiceStarted { get; }

		/// <summary>Trigger sa zahtevom Agenta za preuzimanje (requestId, telefon, šifra) — sledi provera na serveru.</summary>
		public StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<RecipientRemotePickupRequest> RecipientRemotePickupStarted { get; }

		public WorkflowState State => _machine.State;

		public WorkflowMachine(
			WorkflowContext context,
			Func<bool> enableLockerBoxAvailabilityPage,
			Func<bool> confirmPackagePickup,
			Func<bool> isPenPosDeviceAvailable,
			ILogger? logger)
		{
			_context = context;
			_enableLockerBoxAvailabilityPage = enableLockerBoxAvailabilityPage;
			_confirmPackagePickup = confirmPackagePickup;
			_isPenPosDeviceAvailable = isPenPosDeviceAvailable;
			_logger = logger;

			// Immediate: Stateless ne vodi svoj red — sve ide kroz FireSerializedAsync, pa ugnježdeni trigeri iz OnEntry-ja
			// i dalje čekaju u (našem) redu, kao u Queued režimu.
			_machine = new StateMachine<WorkflowState, WorkflowTrigger>(WorkflowState.Idle, FiringMode.Immediate);

			CourierBarcodeScannedRequiringManualLogin = _machine.SetTriggerParameters<string>(WorkflowTrigger.CourierBarcodeScannedRequiringManualLogin);
			CourierBarcodeScanned = _machine.SetTriggerParameters<string>(WorkflowTrigger.CourierBarcodeScanned);
			DepositBarcodeScanned = _machine.SetTriggerParameters<string>(WorkflowTrigger.DepositBarcodeScanned);
			SenderBarcodeScanned = _machine.SetTriggerParameters<string>(WorkflowTrigger.SenderBarcodeScanned);
			DepositBarcodeAccepted = _machine.SetTriggerParameters<string, int>(WorkflowTrigger.DepositBarcodeAccepted);
			SenderBarcodeAccepted = _machine.SetTriggerParameters<string, int>(WorkflowTrigger.SenderBarcodeAccepted);
			RecipientCodeAccepted = _machine.SetTriggerParameters<string>(WorkflowTrigger.RecipientCodeAccepted);
			RecipientCodeAcceptedWithDefaultPhone = _machine.SetTriggerParameters<string>(WorkflowTrigger.RecipientCodeAcceptedWithDefaultPhone);
			RecipientPhoneEntered = _machine.SetTriggerParameters<string>(WorkflowTrigger.RecipientPhoneEntered);
			CourierManualCodeEntered = _machine.SetTriggerParameters<string>(WorkflowTrigger.CourierManualCodeEntered);
			DepositConfirmationFailed = _machine.SetTriggerParameters<DepositConfirmFailure>(WorkflowTrigger.DepositConfirmationFailed);
			SenderConfirmationFailed = _machine.SetTriggerParameters<DepositConfirmFailure>(WorkflowTrigger.SenderConfirmationFailed);
			RemoteServiceStarted = _machine.SetTriggerParameters<string>(WorkflowTrigger.RemoteServiceStarted);
			RecipientRemotePickupStarted = _machine.SetTriggerParameters<RecipientRemotePickupRequest>(WorkflowTrigger.RecipientRemotePickupStarted);

			_machine.OnUnhandledTrigger((state, trigger) =>
				_logger?.LogWarning("Workflow: ignored trigger {Trigger} from state {State} (nije dozvoljen iz tog stanja).", trigger, state));

			_machine.OnTransitioned(t =>
				_logger?.LogInformation("Workflow: {From} --[{Trigger}]--> {To}", t.Source, t.Trigger, t.Destination));

			Configure();
		}

		// ─── Public API koja se zove iz MainWindowViewModel-a ──────────────────
		public Task FireAsync(WorkflowTrigger trigger) => FireSerializedAsync(() => _machine.FireAsync(trigger));

		/// <summary>
		/// Fire samo ako je mašina trenutno u <paramref name="expectedState"/>; inače se trigger odbacuje (uz log).
		/// Za ulaze sa ekrana koji ostaje prikazan i u virtuelnim stanjima (Start ekran tokom CourierAuthenticating /
		/// CourierFetchingExpiredPackages / ServiceDoorOpening): bez provere se trigger naređa u red, a Reset naređan ispred njega
		/// (Home, povratak operativnosti) ga posle učini dozvoljenim — korisnik bi upao u tok koji je već bio odbijen.
		/// </summary>
		public Task FireIfInStateAsync(WorkflowState expectedState, WorkflowTrigger trigger)
		{
			var state = _machine.State;
			if (state != expectedState) {
				_logger?.LogWarning("Workflow: trigger {Trigger} odbačen — prihvata se samo u stanju {Expected}, trenutno {State}.", trigger, expectedState, state);
				return Task.CompletedTask;
			}
			return FireAsync(trigger);
		}

		public Task FireAsync<TArg>(StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<TArg> trigger, TArg arg)
			=> FireSerializedAsync(() => _machine.FireAsync(trigger, arg));

		public Task FireAsync<TArg0, TArg1>(StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<TArg0, TArg1> trigger, TArg0 arg0, TArg1 arg1)
			=> FireSerializedAsync(() => _machine.FireAsync(trigger, arg0, arg1));

		/// <summary>
		/// Atomski prijem nove operacije (lokalne sesije ili Agenta): pod istim lock-om kao red trigera proverava da je mašina
		/// u Start-u, da ne izvršava triger i da je red prazan, pa tek tada poziva <paramref name="reserve"/> (rezervacija
		/// u koordinatoru). Bez reda: odmah prihvatanje ili odbijanje. <paramref name="reserve"/> mora biti kratak i ne sme
		/// da zove mašinu ni UI (izvršava se pod lock-om reda; lock je reentrantan, pa takav poziv ne bi bio sprečen).
		/// „Mašina ne radi“ važi samo ako OnEntry handleri ne pokreću posao koji nastavlja posle prelaza (async void, odvojen
		/// Task); takav posao mora da prati koordinator.
		/// </summary>
		public bool TryReserveWhenIdle(Func<bool> reserve)
		{
			lock (_fireGate) {
				if (_firing || _pendingFires.Count > 0 || _machine.State != WorkflowState.Start) {
					return false;
				}
				return reserve();
			}
		}

		/// <summary>
		/// Triger sa uslovom koji se proverava kad triger dođe na red (u <see cref="FireSerializedAsync"/>, dok nijedan
		/// drugi triger ne radi), a ne pri upisu u red — za početak sesije sa rezervacijom i za zakasnele callback-ove stare
		/// sesije. Ako uslov nije ispunjen, triger se ne izvršava, a poziva se <paramref name="onRejected"/> (i kad uslov
		/// baci izuzetak — izuzetak zatim ide kao izuzetak trigera, vidi <see cref="FireSerializedAsync"/>). Uslov mora biti
		/// bez sporednih efekata, a <paramref name="onRejected"/> kratak, idempotentan i bez izuzetaka; nijedan ne sme da
		/// čeka posledicu trigera iz ovog reda (izvršavaju se u pražnjenju reda, pa bi čekali sami sebe).
		/// Vraćeni Task znači samo „upisano ili izvršeno“, kao kod <see cref="FireAsync(WorkflowTrigger)"/>.
		/// </summary>
		public Task FireGuardedAsync(Func<bool> guard, WorkflowTrigger trigger, Action? onRejected = null)
			=> FireSerializedAsync(() => Allowed(guard, trigger, onRejected) ? _machine.FireAsync(trigger) : Task.CompletedTask);

		/// <inheritdoc cref="FireGuardedAsync(Func{bool}, WorkflowTrigger, Action?)"/>
		public Task FireGuardedAsync<TArg>(Func<bool> guard, StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<TArg> trigger, TArg arg, Action? onRejected = null)
			=> FireSerializedAsync(() => Allowed(guard, trigger.Trigger, onRejected) ? _machine.FireAsync(trigger, arg) : Task.CompletedTask);

		private bool Allowed(Func<bool> guard, WorkflowTrigger trigger, Action? onRejected)
		{
			bool allowed;
			try {
				allowed = guard();
			}
			catch {
				onRejected?.Invoke();	// npr. rezervacija se oslobađa i kad uslov ne može da se proveri
				throw;
			}
			if (!allowed) {
				_logger?.LogInformation("Workflow: trigger {Trigger} odbačen — uslov nije ispunjen kad je došao na red (stanje {State}).", trigger, _machine.State);
				onRejected?.Invoke();
			}
			return allowed;
		}

		/// <summary>
		/// Serijalizovano izvršavanje trigera, bezbedno za više niti. Ako mašina već izvršava triger (uključujući
		/// dugačak async OnEntry), novi triger ide u red i poziv se odmah završava; pozivalac koji je zatekao mašinu
		/// slobodnu izvršava svoj triger i zatim prazni red (FIFO) — isto ponašanje kao Stateless FiringMode.Queued.
		/// Upis u red i odluka „red je prazan, mašina je slobodna“ su pod istim lock-om, pa triger ne može da se izgubi
		/// niti da ostane u redu za sledeću sesiju. Ako triger baci izuzetak, red se ipak isprazni (npr. naređan Reset
		/// mora da se izvrši sada), a prvi izuzetak se prosleđuje pozivaocu.
		/// </summary>
		private async Task FireSerializedAsync(Func<Task> fire)
		{
			lock (_fireGate) {
				if (_firing) {
					_pendingFires.Enqueue(fire);
					return;
				}
				_firing = true;
			}

			Exception? firstException = null;
			while (true) {
				try {
					await fire().ConfigureAwait(false);
				}
				catch (Exception ex) {
					firstException ??= ex;
				}
				lock (_fireGate) {
					if (!_pendingFires.TryDequeue(out fire!)) {
						_firing = false;
						break;
					}
				}
			}
			if (firstException != null) {
				System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstException).Throw();
			}
		}

		/// <summary>
		/// Registruje dodatni OnEntryAsync handler za dato stanje. MainWindowViewModel ovo
		/// koristi da zakači postojeće <c>GoTo*</c> metode (koje vraćaju ValueTask) na ulazak
		/// u odgovarajuće stanje. Stateless dozvoljava više OnEntry handler-a po stanju, pa
		/// se ovo nadovezuje na postojeći LogEntry handler.
		/// </summary>
		public void OnEntry(WorkflowState state, Func<ValueTask> handler)
			=> _machine.Configure(state).OnEntryAsync(async () => await handler().ConfigureAwait(false));

		/// <summary>
		/// Varijanta za sync handler. Korisno kad caller-ov metod nije async.
		/// </summary>
		public void OnEntry(WorkflowState state, Action handler)
			=> _machine.Configure(state).OnEntry(handler);

		/// <summary>
		/// Registruje callback koji se zove na svakoj tranziciji, sa odredišnim stanjem.
		/// Okida se PRE OnEntry handler-a odredišta (Stateless redosled), pa se može koristiti
		/// da se UI pripremi pre dugotrajnih OnEntry side-effekata (npr. ugasiti Home dugme pre
		/// nego što plaćanje krene da await-uje POS). Stateless dozvoljava više OnTransitioned handler-a.
		/// </summary>
		public void OnTransitioned(Action<WorkflowState> onDestinationEntered)
			=> _machine.OnTransitioned(t => onDestinationEntered(t.Destination));

		/// <summary>Kao <see cref="OnTransitioned(Action{WorkflowState})"/>, sa izvornim stanjem (razlikuje reentry Start → Start).</summary>
		public void OnTransitioned(Action<WorkflowState, WorkflowState> onTransition)
			=> _machine.OnTransitioned(t => onTransition(t.Source, t.Destination));

		/// <summary>Kao <see cref="OnTransitioned(Action{WorkflowState, WorkflowState})"/>, sa trigerom (ishod koji zavisi od načina izlaska iz stanja).</summary>
		public void OnTransitioned(Action<WorkflowState, WorkflowTrigger, WorkflowState> onTransition)
			=> _machine.OnTransitioned(t => onTransition(t.Source, t.Trigger, t.Destination));

		/// <summary>
		/// Stanja nepovratne procedure u kojima je Home ugašen: od plaćanja, odnosno dodele vrata, do potvrde serveru da je
		/// paket izdat, preuzet ili ostavljen. Home tu ne sme da prekine tok — otkup bi ostavio kupca bez paketa, a paket
		/// ostavljen bez potvrde server bi dodelio sledećem. Iz ovih stanja izlazi se kroz sam tok (zatvaranje vrata, potvrda,
		/// kvar vrata, istek čekanja). Uz njih i stanja koja ignorišu Reset, jer ih vodi neko drugi (servisna sesija i provera
		/// zahteva Agenta za preuzimanje).
		/// </summary>
		public static bool IsHomeDisabled(WorkflowState state)
			=> state is WorkflowState.RecipientPayment or WorkflowState.RecipientFiscalReceipt or WorkflowState.RecipientPackagePickup
					or WorkflowState.CourierPackagePickup or WorkflowState.CourierPackagePickupBarcodeScan
					or WorkflowState.CourierPackageDeposit or WorkflowState.CourierDepositConfirmation or WorkflowState.CourierDepositConfirmationFailed
					or WorkflowState.SenderPackageDeposit or WorkflowState.SenderDepositConfirmationFailed
					or WorkflowState.RemoteService or WorkflowState.RecipientRemoteValidating;

		/// <summary>
		/// Stanja čija stranica zauzima ceo prozor: bez zaglavlja (logo, jezik, Home), koraka i podnožja. Servisni meni Agenta
		/// tako dobija i prostor koji bi zauzeli koraci i podnožje.
		/// </summary>
		public static bool IsFullScreen(WorkflowState state)
			=> state is WorkflowState.RemoteService;

		/// <summary>
		/// Registruje OnEntry handler koji se okida samo kada se u <paramref name="state"/> ušlo
		/// preko parametrizovanog trigera <paramref name="trigger"/>, i prosleđuje mu argument trigera.
		/// Koristi se za stanja gde podatak (npr. skenirani barkod) putuje uz trigger, a ne kroz kontekst.
		/// Nadovezuje se na postojeći LogEntry OnEntry handler (Stateless dozvoljava više handler-a po stanju).
		/// </summary>
		public void OnEntryFrom<TArg>(
			WorkflowState state,
			StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<TArg> trigger,
			Func<TArg, ValueTask> handler)
			=> _machine.Configure(state).OnEntryFromAsync(trigger, async arg => await handler(arg).ConfigureAwait(false));

		/// <summary>Dvo-argumentna varijanta <see cref="OnEntryFrom{TArg}"/> — npr. DepositBarcodeAccepted(barcode, doorNumber).</summary>
		public void OnEntryFrom<TArg0, TArg1>(
			WorkflowState state,
			StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<TArg0, TArg1> trigger,
			Func<TArg0, TArg1, ValueTask> handler)
			=> _machine.Configure(state).OnEntryFromAsync(trigger, async (arg0, arg1) => await handler(arg0, arg1).ConfigureAwait(false));

		// ─── Konfiguracija celog grafa ─────────────────────────────────────────
		private void Configure()
		{
			ConfigureIdleAndStart();
			ConfigureRecipientFlow();
			ConfigureCourierLoginAndRouting();
			ConfigureCourierPickupFlow();
			ConfigureCourierDepositFlow();
			ConfigureSenderDepositFlow();
			ConfigureServiceFlow();
			ConfigureRemoteServiceFlow();
		}

		private void ConfigureIdleAndStart()
		{
			// Idle → Start: jedino što se dešava na app startup-u.
			_machine.Configure(WorkflowState.Idle)
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start);

			_machine.Configure(WorkflowState.Start)
				.OnEntry(() => { _context.Reset(); LogEntry("Start"); })
				.PermitReentry(WorkflowTrigger.Reset)
				.Permit(WorkflowTrigger.CourierBarcodeScannedRequiringManualLogin, WorkflowState.CourierManualLogin)
				// Direktan login (bez manual login UI-ja) — provera barkoda na serveru ide u CourierAuthenticating.
				.Permit(WorkflowTrigger.CourierBarcodeScanned, WorkflowState.CourierAuthenticating)
				.Permit(WorkflowTrigger.RecipientPickupRequested, WorkflowState.RecipientCodeInput)
				// Sender (pošiljalac) klikne "Ostavi paket" — ide pravo na skeniranje barkoda (bez prompt-a).
				.Permit(WorkflowTrigger.SenderDepositRequested, WorkflowState.SenderDepositBarcodeScan)
				.Permit(WorkflowTrigger.ServiceBarcodeScanned, WorkflowState.ServiceDoorOpening)
				.Permit(WorkflowTrigger.RemoteServiceStarted, WorkflowState.RemoteService)
				.Permit(WorkflowTrigger.RecipientRemotePickupStarted, WorkflowState.RecipientRemoteValidating);
		}

		// ─── Recipient flow ────────────────────────────────────────────────────
		private void ConfigureRecipientFlow()
		{
			_machine.Configure(WorkflowState.RecipientCodeInput)
				.OnEntry(() => LogEntry("RecipientCodeInput"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.RecipientCodeAccepted, WorkflowState.RecipientPhoneInput)
				// Default phone (telefon već u kontekstu): bez ekrana za telefon, pravo u proveru na serveru.
				.Permit(WorkflowTrigger.RecipientCodeAcceptedWithDefaultPhone, WorkflowState.RecipientValidating);

			_machine.Configure(WorkflowState.RecipientPhoneInput)
				.OnEntry(() => LogEntry("RecipientPhoneInput"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.RecipientPhoneEntered, WorkflowState.RecipientValidating);

			// Virtuelno stanje provere na serveru (RRPP u OnEntry). Ishodi provere namerno NISU dozvoljeni iz
			// RecipientPhoneInput: Reset koji stigne dok OnEntry čeka server (Home, tajmer) Stateless naređa ispred
			// ishoda, pa mašina prvo ode na Start, a zakasneli ishod se tamo ignoriše — ni u tuđem RecipientPhoneInput
			// ne bi bio dozvoljen. Ranije je RRPP čekan u handleru tastature dok je mašina mirovala u RecipientPhoneInput,
			// pa je odgovor za prethodnog kupca mogao da otvori vrata sledećem. Reset je dozvoljen (ne Ignore): kupac koji
			// je pritisnuo Home ne dobija otvorena vrata posle odgovora.
			_machine.Configure(WorkflowState.RecipientValidating)
				.OnEntry(() => LogEntry("RecipientValidating"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.RecipientNoPaymentNeeded, WorkflowState.RecipientPackagePickup)
				.Permit(WorkflowTrigger.RecipientPaymentRequired, WorkflowState.RecipientPayment)
				.Permit(WorkflowTrigger.RecipientPaymentNotPossible, WorkflowState.Start)
				.Permit(WorkflowTrigger.RecipientValidationFailed, WorkflowState.Start);

			_machine.Configure(WorkflowState.RecipientPayment)
				.OnEntry(() => LogEntry("RecipientPayment"))
				// Reset (Home dugme) se IGNORIŠE dok je plaćanje u toku. OnEntry (GoToRecipientPaymentPage)
				// await-uje POS Sale i do ~150 s; za to vreme Stateless naređuje sve Reset-trigere u red.
				// Da je Reset dozvoljen, prvi naređani Home-pritisak bi posle naplate pretekao
				// RecipientPaymentApproved (koji se fire-uje na kraju OnEntry) → mašina ode na Start, a
				// korisnik OSTANE BEZ PAKETA IAKO JE PLATIO. Karticna transakcija se ne sme napuštati na pola;
				// izlaz iz ovog stanja ide isključivo preko Approved/Declined (Declined pokriva i otkaz/grešku POS-a).
				.Ignore(WorkflowTrigger.Reset)
				.PermitIf(WorkflowTrigger.RecipientPaymentApproved, WorkflowState.RecipientFiscalReceipt, () => _isPenPosDeviceAvailable(), "IsPenPOSDeviceAvailable")
				.PermitIf(WorkflowTrigger.RecipientPaymentApproved, WorkflowState.RecipientPackagePickup, () => !_isPenPosDeviceAvailable(), "not IsPenPOSDeviceAvailable")
				.Permit(WorkflowTrigger.RecipientPaymentDeclined, WorkflowState.Start);

			_machine.Configure(WorkflowState.RecipientFiscalReceipt)
				.OnEntry(() => LogEntry("RecipientFiscalReceipt"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.RecipientFiscalReceiptCompleted, WorkflowState.RecipientPackagePickup);

			_machine.Configure(WorkflowState.RecipientPackagePickup)
				.OnEntry(() => LogEntry("RecipientPackagePickup"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.RecipientPickupComplete, WorkflowState.Start)
				.Permit(WorkflowTrigger.RecipientPickupDoorActionFailed, WorkflowState.Start);

			// Preuzimanje na zahtev Agenta (RecipientPickup). Podaci zahteva idu u kontekst pre OnEntry handlera provere (oni
			// se registruju posle konfiguracije grafa), kao telefon u lokalnom toku. Reset se ignoriše dok traje provera: korisnik
			// još ništa nije pokrenuo, poziv je ograničen rokom zahteva, a operacija Agenta mora da dobije ishod servera. Iznos
			// za uplatu vodi na Start bez kartice (podrška evidentira uplatu i šalje novu komandu).
			_machine.Configure(WorkflowState.RecipientRemoteValidating)
				.OnEntryFrom(RecipientRemotePickupStarted, request => {
					_context.RemotePickupRequestId = request.RequestId;
					_context.RecipientPhoneNumber = request.Phone;
					_context.UnlockCode = request.Code;
					LogEntry("RecipientRemoteValidating");
				})
				.Ignore(WorkflowTrigger.Reset)
				.Permit(WorkflowTrigger.RecipientNoPaymentNeeded, WorkflowState.RecipientRemoteConfirmation)
				.Permit(WorkflowTrigger.RecipientPaymentRequired, WorkflowState.Start)
				.Permit(WorkflowTrigger.RecipientValidationFailed, WorkflowState.Start);

			// Korisnik pred uređajem unosi kod prisustva. Home (Reset) znači „Odustani". Posle tačnog koda isto otvaranje, potvrda
			// serveru i praćenje vrata kao u lokalnom toku; broj vrata je u kontekstu.
			_machine.Configure(WorkflowState.RecipientRemoteConfirmation)
				.OnEntry(() => LogEntry("RecipientRemoteConfirmation"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.RecipientRemoteAccepted, WorkflowState.RecipientPackagePickup)
				.Permit(WorkflowTrigger.RecipientRemoteDeclined, WorkflowState.Start)
				.Permit(WorkflowTrigger.RecipientRemoteNoAnswer, WorkflowState.Start)
				.Permit(WorkflowTrigger.RecipientRemoteCodeFailed, WorkflowState.Start);
		}

		// ─── Courier login routing ─────────────────────────────────────────────
		private void ConfigureCourierLoginAndRouting()
		{
			_machine.Configure(WorkflowState.CourierManualLogin)
				.OnEntry(() => LogEntry("CourierManualLogin"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.CourierManualCodeEntered, WorkflowState.CourierManualAuthenticating);

			// Virtuelno stanje ručne prijave (provera na serveru u OnEntry). Ishodi namerno NISU dozvoljeni iz
			// CourierManualLogin: Reset koji stigne dok OnEntry čeka server Stateless naređa ispred ishoda, pa mašina prvo
			// ode na Start, a zakasneli ishod se tamo ignoriše — ni na ekranu sledećeg kurira ne bi bio dozvoljen. Neuspeh
			// vraća na unos koda (kurir ponovo kuca kod, bez ponovnog skeniranja); routing posle uspeha isti kao iz
			// CourierAuthenticating.
			_machine.Configure(WorkflowState.CourierManualAuthenticating)
				.OnEntry(() => LogEntry("CourierManualAuthenticating"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.PermitIf(WorkflowTrigger.CourierLoginSuccessful, WorkflowState.CourierLockerBoxAvailability, () => _enableLockerBoxAvailabilityPage(), "EnableLockerBoxAvailabilityPage")
				.PermitIf(WorkflowTrigger.CourierLoginSuccessful, WorkflowState.CourierPickupConfirmation, () => !_enableLockerBoxAvailabilityPage() && _confirmPackagePickup(), "not EnableLockerBoxAvailabilityPage, ConfirmPackagePickup")
				.PermitIf(WorkflowTrigger.CourierLoginSuccessful, WorkflowState.CourierFetchingExpiredPackages, () => !_enableLockerBoxAvailabilityPage() && !_confirmPackagePickup(), "not EnableLockerBoxAvailabilityPage, not ConfirmPackagePickup")
				.Permit(WorkflowTrigger.CourierLoginFailed, WorkflowState.CourierManualLogin);

			// Virtuelno stanje automatske prijave: OnEntry čeka server, a kiosk je za to vreme zauzet (status.lock Busy,
			// Role=Courier); klikovi sa Start ekrana (koji ostaje prikazan) su ovde nedozvoljeni trigeri i ignorišu se.
			// Routing posle uspeha identičan kao iz CourierManualLogin. CourierLoginSuccessful namerno NIJE dozvoljen iz
			// Start-a: Reset koji stigne dok OnEntry čeka server Stateless naređa ispred uspeha, pa mašina prvo ode na
			// Start, a zakasneli uspeh se tamo ignoriše — kurir ne upada u tok posle reseta.
			_machine.Configure(WorkflowState.CourierAuthenticating)
				.OnEntry(() => LogEntry("CourierAuthenticating"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.PermitIf(WorkflowTrigger.CourierLoginSuccessful, WorkflowState.CourierLockerBoxAvailability, () => _enableLockerBoxAvailabilityPage(), "EnableLockerBoxAvailabilityPage")
				.PermitIf(WorkflowTrigger.CourierLoginSuccessful, WorkflowState.CourierPickupConfirmation, () => !_enableLockerBoxAvailabilityPage() && _confirmPackagePickup(), "not EnableLockerBoxAvailabilityPage, ConfirmPackagePickup")
				.PermitIf(WorkflowTrigger.CourierLoginSuccessful, WorkflowState.CourierFetchingExpiredPackages, () => !_enableLockerBoxAvailabilityPage() && !_confirmPackagePickup(), "not EnableLockerBoxAvailabilityPage, not ConfirmPackagePickup")
				.Permit(WorkflowTrigger.CourierLoginFailed, WorkflowState.Start);

			_machine.Configure(WorkflowState.CourierLockerBoxAvailability)
				.OnEntry(() => LogEntry("CourierLockerBoxAvailability"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.BoxAvailabilityCompleted, WorkflowState.CourierFetchingExpiredPackages);

			_machine.Configure(WorkflowState.CourierPickupConfirmation)
				.OnEntry(() => LogEntry("CourierPickupConfirmation"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.PickupConfirmed, WorkflowState.CourierFetchingExpiredPackages)
				.Permit(WorkflowTrigger.PickupConfirmationCancelled, WorkflowState.CourierDepositPrompt);

			// Virtuelno loading stanje. OnEntry treba da pozove server (Phase 3+ će dodati
			// stvarni fetch handler u MainWindowViewModel-u); ovde samo deklarativno definišemo izlaze.
			_machine.Configure(WorkflowState.CourierFetchingExpiredPackages)
				.OnEntry(() => LogEntry("CourierFetchingExpiredPackages"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.HasExpiredPackages, WorkflowState.CourierPickupPrompt)
				.Permit(WorkflowTrigger.NoExpiredPackages, WorkflowState.CourierDepositPrompt);
		}

		// ─── Courier pickup flow ───────────────────────────────────────────────
		private void ConfigureCourierPickupFlow()
		{
			_machine.Configure(WorkflowState.CourierPickupPrompt)
				.OnEntry(() => LogEntry("CourierPickupPrompt"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.PickupPromptCancelled, WorkflowState.Start)
				.Permit(WorkflowTrigger.PickupPromptPickupClicked, WorkflowState.CourierPackagePickup);

			_machine.Configure(WorkflowState.CourierPackagePickup)
				.OnEntry(() => LogEntry("CourierPackagePickup"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.PickupDoorClosedAfterOpen, WorkflowState.CourierPackagePickupBarcodeScan)
				.PermitIf(WorkflowTrigger.PickupDoorActionFailed, WorkflowState.CourierPickupPrompt, HasMoreExpiredPackages, "HasMoreExpiredPackages")
				.PermitIf(WorkflowTrigger.PickupDoorActionFailed, WorkflowState.CourierDepositPrompt, NoMoreExpiredPackages, "NoMoreExpiredPackages");

			_machine.Configure(WorkflowState.CourierPackagePickupBarcodeScan)
				.OnEntry(() => LogEntry("CourierPackagePickupBarcodeScan"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.PermitIf(WorkflowTrigger.PickupBarcodeResolved, WorkflowState.CourierPickupPrompt, HasMoreExpiredPackages, "HasMoreExpiredPackages")
				.PermitIf(WorkflowTrigger.PickupBarcodeResolved, WorkflowState.CourierDepositPrompt, NoMoreExpiredPackages, "NoMoreExpiredPackages")
				.Permit(WorkflowTrigger.PickupBarcodeScanTimeout, WorkflowState.Start);
		}

		// ─── Courier deposit flow ──────────────────────────────────────────────
		private void ConfigureCourierDepositFlow()
		{
			_machine.Configure(WorkflowState.CourierDepositPrompt)
				.OnEntry(() => LogEntry("CourierDepositPrompt"))
				// ExpiredPackagesPickedUp utiče na UI tekst (jesu li paketi već pokupljeni ili ne):
				//  - True: ulaz iz pickup-a (cancel confirmation, ili pickup loop završen)
				//  - False: ulaz direktno (nije bilo isteklih paketa za pickup)
				.OnEntryFrom(WorkflowTrigger.PickupConfirmationCancelled, () => _context.ExpiredPackagesPickedUp = true)
				.OnEntryFrom(WorkflowTrigger.PickupBarcodeResolved, () => _context.ExpiredPackagesPickedUp = true)
				.OnEntryFrom(WorkflowTrigger.PickupDoorActionFailed, () => _context.ExpiredPackagesPickedUp = true)
				.OnEntryFrom(WorkflowTrigger.NoExpiredPackages, () => _context.ExpiredPackagesPickedUp = false)
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.DepositPromptCancelled, WorkflowState.Start)
				.Permit(WorkflowTrigger.DepositPromptDepositClicked, WorkflowState.CourierPackageDepositBarcodeScan);

			_machine.Configure(WorkflowState.CourierPackageDepositBarcodeScan)
				.OnEntry(() => LogEntry("CourierPackageDepositBarcodeScan"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.DepositBarcodeScanned, WorkflowState.CourierDepositBarcodeValidating)
				.Permit(WorkflowTrigger.DepositBarcodeScanCancelled, WorkflowState.Start)
				.Permit(WorkflowTrigger.DepositBarcodeScanTimeout, WorkflowState.Start);

			// Virtuelno stanje provere barkoda na serveru (dodela vrata u OnEntry). Ishodi namerno NISU dozvoljeni iz
			// CourierPackageDepositBarcodeScan: Reset (Home) ili Odustani koji stignu dok OnEntry čeka server Stateless naređa
			// ispred ishoda, pa mašina prvo ode na Start, a zakasneli ishod se tamo ignoriše — ni na ekranu za skeniranje
			// sledećeg kurira ne bi bio dozvoljen. Ranije je poziv čekan u tracker-u skenera dok je mašina mirovala na ekranu
			// za skeniranje, pa je odgovor za prethodni paket mogao da otvori vrata sledećem. Odbijen barkod vraća na skeniranje.
			_machine.Configure(WorkflowState.CourierDepositBarcodeValidating)
				.OnEntry(() => LogEntry("CourierDepositBarcodeValidating"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.DepositBarcodeScanCancelled, WorkflowState.Start)
				.Permit(WorkflowTrigger.DepositBarcodeAccepted, WorkflowState.CourierPackageDeposit)
				.Permit(WorkflowTrigger.DepositBarcodeRejected, WorkflowState.CourierPackageDepositBarcodeScan);

			_machine.Configure(WorkflowState.CourierPackageDeposit)
				.OnEntry(() => LogEntry("CourierPackageDeposit"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.DepositDoorOpenFailed, WorkflowState.CourierPackageDepositBarcodeScan)
				.Permit(WorkflowTrigger.DepositDoorClosedAutoConfirm, WorkflowState.CourierPackageDepositBarcodeScan)
				.Permit(WorkflowTrigger.DepositConfirmationFailed, WorkflowState.CourierDepositConfirmationFailed)
				.Permit(WorkflowTrigger.DepositDoorClosedExplicitConfirm, WorkflowState.CourierDepositConfirmation)
				// Posle promene veličine sanduka kurir ponovo skenira paket (dobija novu, veću poziciju) — ide na barcode scan, ne reentry.
				.Permit(WorkflowTrigger.DepositChangeBoxRequested, WorkflowState.CourierPackageDepositBarcodeScan)
				.Permit(WorkflowTrigger.DepositCancelledLogout, WorkflowState.Start);

			_machine.Configure(WorkflowState.CourierDepositConfirmation)
				.OnEntry(() => LogEntry("CourierDepositConfirmation"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.DepositConfirmationConfirmed, WorkflowState.CourierPackageDepositBarcodeScan)
				.Permit(WorkflowTrigger.DepositConfirmationFailed, WorkflowState.CourierDepositConfirmationFailed)
				.Permit(WorkflowTrigger.DepositConfirmationCancelled, WorkflowState.CourierPackageDepositBarcodeScan);

			// Server nije potvrdio ostavljanje: kurir bira, a samo istek roka (kurir je otišao) vodi na Start. Reset dolazi samo
			// iz toka (Home je ugašen) — kad vrata posle „Preuzmi" ostanu otvorena.
			_machine.Configure(WorkflowState.CourierDepositConfirmationFailed)
				.OnEntry(() => LogEntry("CourierDepositConfirmationFailed"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.DepositConfirmationRetrySucceeded, WorkflowState.CourierPackageDepositBarcodeScan)
				.Permit(WorkflowTrigger.DepositPackageReturned, WorkflowState.CourierPackageDepositBarcodeScan)
				.Permit(WorkflowTrigger.DepositLeftUnconfirmed, WorkflowState.CourierPackageDepositBarcodeScan)
				.Permit(WorkflowTrigger.DepositConfirmationFailedTimeout, WorkflowState.Start);
		}

		// ─── Sender deposit flow ───────────────────────────────────────────────
		// Paralela courier depositu, ali: ulaz sa Start dugmeta (ne iz login/pickup), bez prompt ekrana,
		// bez petlje za sledeći paket (svaki završetak → Start). Bez confirmation ekrana: zatvaranje vrata
		// se računa kao ostavljen paket — potvrda se šalje serveru inline u activatoru pa pravo na Start.
		private void ConfigureSenderDepositFlow()
		{
			_machine.Configure(WorkflowState.SenderDepositBarcodeScan)
				.OnEntry(() => LogEntry("SenderDepositBarcodeScan"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.SenderBarcodeScanned, WorkflowState.SenderDepositBarcodeValidating)
				.Permit(WorkflowTrigger.SenderBarcodeScanCancelled, WorkflowState.Start)
				.Permit(WorkflowTrigger.SenderBarcodeScanTimeout, WorkflowState.Start);

			// Virtuelno stanje provere barkoda na serveru — isti razlog kao CourierDepositBarcodeValidating; ovde je rizik
			// veći jer je sledeći pošiljalac samo jedan klik ("Ostavi paket") daleko od ekrana za skeniranje.
			_machine.Configure(WorkflowState.SenderDepositBarcodeValidating)
				.OnEntry(() => LogEntry("SenderDepositBarcodeValidating"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.SenderBarcodeScanCancelled, WorkflowState.Start)
				.Permit(WorkflowTrigger.SenderBarcodeAccepted, WorkflowState.SenderPackageDeposit)
				.Permit(WorkflowTrigger.SenderBarcodeRejected, WorkflowState.SenderDepositBarcodeScan);

			_machine.Configure(WorkflowState.SenderPackageDeposit)
				.OnEntry(() => LogEntry("SenderPackageDeposit"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.SenderDoorOpenFailed, WorkflowState.SenderDepositBarcodeScan)
				// Posle promene veličine sanduka pošiljalac ponovo skenira paket (dobija novu poziciju).
				.Permit(WorkflowTrigger.SenderChangeBoxRequested, WorkflowState.SenderDepositBarcodeScan)
				// Zatvaranje vrata = paket ostavljen. Potvrda serveru se šalje inline u activatoru, pa pravo na Start.
				.Permit(WorkflowTrigger.SenderDoorClosedConfirm, WorkflowState.Start)
				.Permit(WorkflowTrigger.SenderConfirmationFailed, WorkflowState.SenderDepositConfirmationFailed)
				.Permit(WorkflowTrigger.SenderCancelled, WorkflowState.Start);

			// Isto kao CourierDepositConfirmationFailed, ali svaki izlaz vodi na Start.
			_machine.Configure(WorkflowState.SenderDepositConfirmationFailed)
				.OnEntry(() => LogEntry("SenderDepositConfirmationFailed"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.SenderConfirmationRetrySucceeded, WorkflowState.Start)
				.Permit(WorkflowTrigger.SenderPackageReturned, WorkflowState.Start)
				.Permit(WorkflowTrigger.SenderLeftUnconfirmed, WorkflowState.Start)
				.Permit(WorkflowTrigger.SenderConfirmationFailedTimeout, WorkflowState.Start);
		}

		// ─── Servis (tehničar) ─────────────────────────────────────────────────
		// ServiceDoorHandled je dozvoljen samo ovde — završetak otvaranja ne sme da prekine tok koji je u
		// međuvremenu (npr. posle Reset-a) počeo; u svakom drugom stanju se samo loguje kao unhandled.
		private void ConfigureServiceFlow()
		{
			_machine.Configure(WorkflowState.ServiceDoorOpening)
				.OnEntry(() => LogEntry("ServiceDoorOpening"))
				.Permit(WorkflowTrigger.Reset, WorkflowState.Start)
				.Permit(WorkflowTrigger.ServiceDoorHandled, WorkflowState.Start);
		}

		// ─── Agent — servisna sesija ───────────────────────────────────────────
		// Reset se ignoriše: Home je ugašen, a ni jedan legitiman izvor Reset-a (rokovi stranica, operativnost) ne sme da
		// prekine sesiju dok serviser radi sa otvorenim vratima. Sesiju završava samo RemoteServiceEnded, koji je
		// dozvoljen samo ovde (zakasneo kraj stare sesije ne dira tok koji je počeo posle nje).
		private void ConfigureRemoteServiceFlow()
		{
			_machine.Configure(WorkflowState.RemoteService)
				.OnEntry(() => LogEntry("RemoteService"))
				.Ignore(WorkflowTrigger.Reset)
				.Permit(WorkflowTrigger.RemoteServiceEnded, WorkflowState.Start);
		}

		// ─── Guards za PermitIf grane ──────────────────────────────────────────
		// Pickup loop završava kad nema više isteklih paketa u kontekstu. PermitIf čita ove
		// guarde — tako svaka grana ima eksplicitnu destinaciju i graf vidi obe.

		private bool HasMoreExpiredPackages() => _context.RemainingExpiredPackages?.Count > 0;
		private bool NoMoreExpiredPackages() => !HasMoreExpiredPackages();

		// ─── Helpers ───────────────────────────────────────────────────────────
		private void LogEntry(string state) => _logger?.LogDebug("Workflow OnEntry: {State}", state);

		/// <summary>
		/// Generiše DOT graf trenutne konfiguracije — koristi za vizualni pregled flow-a.
		/// Ulepej rezultat u npr. https://dreampuf.github.io/GraphvizOnline da vidiš graf.
		/// </summary>
		public string ToDotGraph() => Stateless.Graph.UmlDotGraph.Format(_machine.GetInfo());
	}
}