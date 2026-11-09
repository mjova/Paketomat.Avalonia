using Microsoft.Extensions.Logging;

namespace TopPos.Kiosk.Workflow
{
	/// <summary>Kako rezervacija počinje i završava.</summary>
	public enum KioskLeaseMode
	{
		/// <summary>Lokalna sesija toka: aktivna kad mašina napusti Start, završava se ulaskom u Start i čišćenjem.</summary>
		WorkflowSession,
		/// <summary>Operacija koja ne napušta Start (prozor statusa uređaja, Agent): eksplicitan početak i kraj.</summary>
		StandaloneOperation,
	}

	public enum KioskLeasePhase { Reserved, Active, Ending, Released }

	/// <summary>Jedna rezervacija kioska. Faze menja samo <see cref="KioskOperationCoordinator"/>, pod svojim lock-om.</summary>
	public sealed class KioskLease
	{
		internal KioskLease(long id, KioskLeaseMode mode, string role, string source)
		{
			Id = id;
			Mode = mode;
			Role = role;
			Source = source;
		}

		public long Id { get; }
		public KioskLeaseMode Mode { get; }
		/// <summary>Uloga u statusu dok rezervacija traje (Client, Courier, Service, Agent).</summary>
		public string Role { get; }
		/// <summary>Ulaz koji je rezervisao kiosk (za log).</summary>
		public string Source { get; }

		internal KioskLeasePhase Phase = KioskLeasePhase.Reserved;
		/// <summary>Kraj kroz ulazak u Start: oslobađanje čeka čišćenje iz Start OnEntry-ja (<see cref="KioskOperationCoordinator.CompleteCleanup"/>).</summary>
		internal bool CleanupRequired;
		internal Task? Cleanup;
		internal readonly List<Task> Work = [];
		internal bool FinishScheduled;

		public override string ToString() => $"#{Id} {Role} ({Source})";
	}

	/// <summary>
	/// Jedan vlasnik kioska (plan §6): lokalna sesija toka, lokalna servisna operacija ili Agent. Svaki početak ide kroz
	/// <see cref="TryBegin"/> — atomski sa redom trigera (<see cref="WorkflowMachine.TryReserveWhenIdle"/>). Rezervacija
	/// prolazi faze Reserved → Active → Ending → Released; kiosk je zauzet (status, <c>status.lock</c>) od rezervacije
	/// dok se ne završe čišćenje i praćeni poslovi. Nema oslobađanja po isteku roka — samo upozorenje u log.
	///
	/// Bezbedno za više niti; ne pretpostavlja UI nit. Redosled lock-ova: red mašine (<c>_fireGate</c>) → koordinator →
	/// <see cref="KioskStatusSource"/>. Koordinator pod svojim lock-om ne zove mašinu, UI ni tuđe callback-ove; čekanje
	/// završetka posla ide van lock-a i van pražnjenja reda mašine.
	/// </summary>
	public sealed class KioskOperationCoordinator
	{
		private readonly object _lock = new();
		private readonly KioskStatusSource _statusSource;
		private readonly ILogger? _logger;
		private readonly TimeSpan _slowEndingWarning;
		private long _lastId;
		private KioskLease? _owner;
		private bool _closed;
		private bool _ownerChangedPending;
		private TaskCompletionSource _free = NewSignal(completed: true);

		public KioskOperationCoordinator(KioskStatusSource statusSource, ILogger? logger, TimeSpan? slowEndingWarning = null)
		{
			_statusSource = statusSource;
			_logger = logger;
			_slowEndingWarning = slowEndingWarning ?? TimeSpan.FromSeconds(30);
		}

		/// <summary>
		/// Vlasnik se promenio (rezervacija ili oslobađanje). Zove se van lock-a koordinatora, na niti koja je promenu izazvala
		/// (i iz pražnjenja reda mašine); više promena može da se spoji u jedno obaveštenje, pa handler čita <see cref="Owner"/>.
		/// Handler mora biti kratak i ne sme da zove mašinu.
		/// </summary>
		public event Action? OwnerChanged;

		/// <summary>Trenutni vlasnik (null = slobodno).</summary>
		public KioskLease? Owner {
			get { lock (_lock) { return _owner; } }
		}

		public KioskLeasePhase PhaseOf(KioskLease lease)
		{
			lock (_lock) { return lease.Phase; }
		}

		/// <summary>Da li je rezervacija i dalje aktivna — uslov za zakasnele callback-ove (<see cref="WorkflowMachine.FireGuardedAsync(Func{bool}, WorkflowTrigger, Action?)"/>).</summary>
		public bool IsActive(KioskLease lease) => PhaseOf(lease) == KioskLeasePhase.Active;

		/// <summary>Da li je rezervacija i dalje u Reserved — uslov za triger koji počinje sesiju.</summary>
		public bool IsReserved(KioskLease lease) => PhaseOf(lease) == KioskLeasePhase.Reserved;

		/// <summary>
		/// Aktivna lokalna sesija toka, ili null. OnEntry je hvata za callback-ove koji se izvršavaju kasnije (skener, tracker,
		/// dugme posle odziva, rok ekrana): oni rade i okidaju trigere samo dok je ta sesija aktivna (<see cref="IsActive"/>).
		/// </summary>
		public KioskLease? ActiveSession {
			get {
				lock (_lock) {
					return _owner is { Mode: KioskLeaseMode.WorkflowSession, Phase: KioskLeasePhase.Active } session ? session : null;
				}
			}
		}

		/// <summary>
		/// Atomski prijem nove operacije: mašina u Start-u, ne radi, red prazan, kiosk bez vlasnika i nije u gašenju.
		/// Odmah prihvatanje (rezervacija u fazi Reserved) ili odbijanje (null), bez čekanja i bez reda.
		/// </summary>
		public KioskLease? TryBegin(WorkflowMachine machine, KioskLeaseMode mode, string role, string source)
		{
			KioskLease? lease = null;
			if (!machine.TryReserveWhenIdle(() => (lease = TryReserve(mode, role, source)) != null)) {
				_logger?.LogInformation("Koordinator: {Source} odbijen — kiosk je zauzet ili nije spreman (vlasnik {Owner}, stanje {State}).",
										source, Owner?.ToString() ?? "nema", machine.State);
				return null;
			}
			RaiseOwnerChanged();	// van _fireGate
			return lease;
		}

		// Poziva se pod _fireGate (WorkflowMachine.TryReserveWhenIdle): kratko, bez poziva mašine i UI-ja.
		private KioskLease? TryReserve(KioskLeaseMode mode, string role, string source)
		{
			lock (_lock) {
				if (_closed || _owner != null) {
					return null;
				}
				var lease = new KioskLease(++_lastId, mode, role, source);
				SetOwnerLocked(lease);
				_logger?.LogInformation("Koordinator: rezervacija {Lease}.", lease);
				return lease;
			}
		}

		/// <summary>
		/// Oslobađa rezervaciju koja još nije počela (triger odbijen, otkazan klik, izuzetak pre predaje trigera). Idempotentno;
		/// ne dira tuđu ni već aktivnu rezervaciju. Vraća da li je oslobođena.
		/// </summary>
		public bool ReleaseIfReserved(KioskLease lease)
		{
			lock (_lock) {
				if (_owner != lease || lease.Phase != KioskLeasePhase.Reserved) {
					return false;
				}
				_logger?.LogInformation("Koordinator: rezervacija {Lease} oslobođena pre početka.", lease);
				if (lease.Work.Count > 0) {
					BeginEndingLocked(lease, cleanupRequired: false);	// posao pokrenut i pre početka ipak mora da se završi
				}
				else {
					ReleaseLocked(lease);
				}
			}
			RaiseOwnerChanged();
			return true;
		}

		/// <summary>
		/// Prijem i početak operacije koja ne napušta Start (npr. komanda Agenta): <see cref="TryBegin"/> pa
		/// <see cref="TryActivate"/>. Vraća aktivnu rezervaciju ili null (kiosk zauzet, nije spreman, u gašenju).
		/// </summary>
		public KioskLease? TryBeginOperation(WorkflowMachine machine, string role, string source)
		{
			var lease = TryBegin(machine, KioskLeaseMode.StandaloneOperation, role, source);
			if (lease != null && !TryActivate(lease)) {
				ReleaseIfReserved(lease);
				return null;
			}
			return lease;
		}

		/// <summary>Početak operacije koja ne napušta Start (prozor statusa uređaja, Agent): Reserved → Active.</summary>
		public bool TryActivate(KioskLease lease)
		{
			lock (_lock) {
				if (_owner != lease || lease.Mode != KioskLeaseMode.StandaloneOperation || lease.Phase != KioskLeasePhase.Reserved) {
					return false;
				}
				lease.Phase = KioskLeasePhase.Active;
				_logger?.LogInformation("Koordinator: operacija {Lease} počela.", lease);
				return true;
			}
		}

		/// <summary>
		/// Kraj operacije koja ne napušta Start: → Ending, a Released kad se završe njeni praćeni poslovi. Idempotentno.
		/// </summary>
		public void End(KioskLease lease)
		{
			lock (_lock) {
				if (_owner != lease || lease.Mode != KioskLeaseMode.StandaloneOperation
					|| lease.Phase is KioskLeasePhase.Ending or KioskLeasePhase.Released) {
					return;
				}
				_logger?.LogInformation("Koordinator: operacija {Lease} se završava.", lease);
				BeginEndingLocked(lease, cleanupRequired: false);
			}
			RaiseOwnerChanged();
		}

		/// <summary>
		/// Prelaz mašine (iz <see cref="WorkflowMachine.OnTransitioned(Action{WorkflowState, WorkflowState})"/>, pre OnEntry-ja
		/// odredišta): izlazak iz Start-a aktivira rezervisanu sesiju, ulazak u Start je završava. Reentry Start → Start ne
		/// menja ništa (ni Reserved ni operaciju Agenta).
		/// </summary>
		public void OnWorkflowTransition(WorkflowState source, WorkflowState destination)
		{
			lock (_lock) {
				if (source == WorkflowState.Start && destination != WorkflowState.Start) {
					if (_owner is { Mode: KioskLeaseMode.WorkflowSession, Phase: KioskLeasePhase.Reserved } reserved) {
						reserved.Phase = KioskLeasePhase.Active;
						_logger?.LogInformation("Koordinator: sesija {Lease} počela ({Destination}).", reserved, destination);
					}
					else if (_owner == null) {
						// ulaz koji nije prošao kroz TryBegin — kiosk ipak mora biti zauzet do kraja sesije
						var implicitLease = new KioskLease(++_lastId, KioskLeaseMode.WorkflowSession, WorkflowStatusRules.ResolveRole(destination),
															$"prelaz u {destination} bez rezervacije") { Phase = KioskLeasePhase.Active };
						SetOwnerLocked(implicitLease);
						_logger?.LogWarning("Koordinator: mašina je napustila Start bez rezervacije ({Destination}); sesija {Lease} je zauzela kiosk.",
											destination, implicitLease);
					}
					else {
						_logger?.LogWarning("Koordinator: mašina je napustila Start ({Destination}) dok je vlasnik {Owner}.", destination, _owner);
					}
				}
				else if (destination == WorkflowState.Start && source != WorkflowState.Start) {
					if (_owner is { Mode: KioskLeaseMode.WorkflowSession, Phase: KioskLeasePhase.Active } active) {
						_logger?.LogInformation("Koordinator: sesija {Lease} se završava (iz {Source}).", active, source);
						BeginEndingLocked(active, cleanupRequired: true);
					}
				}
			}
			RaiseOwnerChanged();
		}

		/// <summary>
		/// Čišćenje posle ulaska u Start (iz Start OnEntry-ja): sesija u Ending se oslobađa kad se <paramref name="cleanup"/> i
		/// praćeni poslovi završe. Ne čeka ništa — poziva se iz pražnjenja reda mašine (zaustavljanje trackera čiji callback
		/// je upravo poslao Reset bi tu čekalo samo sebe). Bez sesije u Ending (Reset reentry, Idle → Start) ne radi ništa.
		/// </summary>
		public void CompleteCleanup(Task cleanup) => HandOverCleanup(cleanup);

		/// <summary>
		/// Kao <see cref="CompleteCleanup(Task)"/>, ali pokreće čišćenje samo: i sinhroni izuzetak iz <paramref name="cleanup"/>
		/// se predaje kao završen (neuspeo) posao, pa sesija ne ostaje u Ending zato što čišćenje nije ni predato.
		/// </summary>
		public void CompleteCleanup(Func<Task> cleanup)
		{
			Task task;
			try {
				task = cleanup();
			}
			catch (Exception ex) {
				task = Task.FromException(ex);
			}
			HandOverCleanup(task);
		}

		private void HandOverCleanup(Task cleanup)
		{
			lock (_lock) {
				if (_owner is not { Phase: KioskLeasePhase.Ending, CleanupRequired: true, Cleanup: null } ending) {
					// nema sesije koja ga čeka (Reset reentry, Idle → Start): greška čišćenja ipak u log, ne kao neposmatran Task
					cleanup.ContinueWith(t => _logger?.LogError(t.Exception, "Koordinator: čišćenje van sesije završeno greškom."),
										CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
					return;
				}
				ending.Cleanup = cleanup;
				TryFinishLocked(ending);
			}
			RaiseOwnerChanged();
		}

		/// <summary>
		/// Pokreće posao koji mora da se završi pre oslobađanja kiosk-a (npr. povrat paketa: otvaranje i čekanje zatvaranja).
		/// Prijava je pre pokretanja; posle početka završavanja (Ending) novi posao se ne prima — vraća null i posao se ne
		/// pokreće.
		/// </summary>
		public Task? TryRun(KioskLease lease, Func<Task> work)
		{
			var done = NewSignal(completed: false);
			lock (_lock) {
				if (_owner != lease || lease.Phase is not (KioskLeasePhase.Reserved or KioskLeasePhase.Active)) {
					_logger?.LogInformation("Koordinator: posao za {Lease} nije pokrenut — rezervacija se završava ili je završena.", lease);
					return null;
				}
				lease.Work.Add(done.Task);
			}
			Task task;
			try {
				task = work();
			}
			catch (Exception ex) {
				done.TrySetResult();
				return Task.FromException(ex);
			}
			task.ContinueWith(_ => done.TrySetResult(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
			return task;
		}

		/// <summary>
		/// Već pokrenut posao koji mora da se završi pre oslobađanja kiosk-a (npr. upis ishoda operacije Agenta pri povratku na
		/// Start). Za razliku od <see cref="TryRun"/>, prima se i u Ending, dok oslobađanje nije zakazano: prelaz u Start
		/// počinje završavanje, a čišćenje iz Start OnEntry-ja stiže tek posle svih handler-a prelaza. Vraća false kad je
		/// kasno (tuđa ili oslobođena rezervacija, oslobađanje zakazano); posao tada ne drži kiosk.
		/// </summary>
		public bool TryHoldRelease(KioskLease lease, Task work)
		{
			lock (_lock) {
				if (_owner != lease || lease.Phase == KioskLeasePhase.Released || lease.FinishScheduled) {
					_logger?.LogWarning("Koordinator: posao ne drži {Lease} — rezervacija je oslobođena ili se oslobađa.", lease);
					return false;
				}
				lease.Work.Add(work);
			}
			return true;
		}

		/// <summary>Čeka da kiosk ostane bez vlasnika (npr. prozor statusa posle Reset-a). Vraća false ako rok istekne.</summary>
		public async Task<bool> WaitUntilFreeAsync(TimeSpan timeout)
		{
			Task free;
			lock (_lock) { free = _free.Task; }
			try {
				await free.WaitAsync(timeout).ConfigureAwait(false);
				return true;
			}
			catch (TimeoutException) {
				return false;
			}
		}

		/// <summary>Gašenje: posle ovoga nijedna nova rezervacija. Tekući vlasnik se završava svojim tokom.</summary>
		public void Close()
		{
			lock (_lock) {
				if (_closed) {
					return;
				}
				_closed = true;
				_logger?.LogInformation("Koordinator: zatvoren — nove operacije se ne primaju (vlasnik {Owner}).", _owner?.ToString() ?? "nema");
			}
		}

		// MORA se pozivati pod _lock.
		private void BeginEndingLocked(KioskLease lease, bool cleanupRequired)
		{
			lease.Phase = KioskLeasePhase.Ending;
			lease.CleanupRequired = cleanupRequired;
			_ = WarnIfSlowAsync(lease);
			TryFinishLocked(lease);
		}

		// MORA se pozivati pod _lock. Oslobađa odmah ako je sve gotovo, inače posle završetka — van lock-a i van reda mašine.
		private void TryFinishLocked(KioskLease lease)
		{
			if (lease.FinishScheduled || (lease.CleanupRequired && lease.Cleanup == null)) {
				return;
			}
			var pending = lease.Cleanup == null ? lease.Work.ToArray() : [.. lease.Work, lease.Cleanup];
			if (pending.All(t => t.IsCompleted)) {
				LogFaults(lease, pending);
				ReleaseLocked(lease);
				return;
			}
			lease.FinishScheduled = true;
			Task.WhenAll(pending).ContinueWith(all => {
				_ = all.Exception;      // posmatran: pojedinačne greške loguje LogFaults, zbirni Task ne sme ostati neposmatran
				LogFaults(lease, pending);
				lock (_lock) {
					if (_owner == lease && lease.Phase == KioskLeasePhase.Ending) {
						ReleaseLocked(lease);
					}
				}
				RaiseOwnerChanged();
			}, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
		}

		private void LogFaults(KioskLease lease, Task[] tasks)
		{
			foreach (var task in tasks.Where(t => t.IsFaulted)) {
				_logger?.LogError(task.Exception, "Koordinator: posao ili čišćenje rezervacije {Lease} završeni greškom.", lease);
			}
		}

		// MORA se pozivati pod _lock.
		private void ReleaseLocked(KioskLease lease)
		{
			lease.Phase = KioskLeasePhase.Released;
			_logger?.LogInformation("Koordinator: kiosk slobodan (kraj {Lease}).", lease);
			SetOwnerLocked(null);
		}

		private void RaiseOwnerChanged()
		{
			lock (_lock) {
				if (!_ownerChangedPending) {
					return;
				}
				_ownerChangedPending = false;
			}
			try {
				OwnerChanged?.Invoke();
			}
			catch (Exception ex) {
				_logger?.LogError(ex, "Koordinator: obaveštenje o promeni vlasnika bacilo je izuzetak.");
			}
		}

		// MORA se pozivati pod _lock (redosled koordinator → izvor statusa).
		private void SetOwnerLocked(KioskLease? owner)
		{
			_owner = owner;
			_ownerChangedPending = true;
			if (owner == null) {
				_free.TrySetResult();
			}
			else if (_free.Task.IsCompleted) {
				_free = NewSignal(completed: false);
			}
			_statusSource.SetOwner(owner?.Role);
		}

		private async Task WarnIfSlowAsync(KioskLease lease)
		{
			await Task.Delay(_slowEndingWarning).ConfigureAwait(false);
			lock (_lock) {
				if (lease.Phase != KioskLeasePhase.Ending) {
					return;
				}
			}
			_logger?.LogWarning("Koordinator: rezervacija {Lease} se završava duže od {Seconds} s — kiosk ostaje zauzet dok se posao ne završi.",
								lease, _slowEndingWarning.TotalSeconds);
		}

		private static TaskCompletionSource NewSignal(bool completed)
		{
			var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			if (completed) {
				signal.TrySetResult();
			}
			return signal;
		}
	}
}
