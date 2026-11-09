using Microsoft.Extensions.Logging;
using TopPos.Agent.Contracts;

namespace TopPos.Client.WPF.Workflow
{
	/// <summary>Nepromenljiv snapshot statusa kioska; <see cref="KioskStatusSource"/> pravi nov pri svakoj promeni.</summary>
	public sealed record KioskStatusSnapshot(
		long Revision,
		DateTimeOffset ChangedAtUtc,
		WorkflowState WorkflowState,
		bool Busy,
		string Role,
		bool? Operational,
		string Lifecycle,
		bool CanAcceptOpenDoor,
		string? CannotAcceptReason,
		string? ActiveOperationId = null,
		string? Activity = null,
		int? OperationSequence = null);

	/// <summary>
	/// Jedini memorijski izvor statusa kioska (doc/notes/agent-client-komunikacija-i-komande-plan.md, §5): workflow stanje,
	/// operativnost i životni ciklus. Iz njega se izvode i pipe status za Agenta i <c>status.lock</c>
	/// (<see cref="StatusSemaphore"/>), pa pravilo „slobodan/zauzet“ postoji na jednom mestu
	/// (<see cref="WorkflowStatusRules"/>).
	///
	/// Svaka stvarna promena dobija novu reviziju i vreme promene; ulaz bez promene (npr. Reset iz Start-a u Start) ne
	/// pravi novu reviziju niti obaveštava pretplatnike. Pretplatnici se pozivaju pod lock-om izvora, redom revizija, pa
	/// početni snapshot pretplate i naredne promene ne mogu da se razminu — zato callback mora biti kratak i neblokirajući
	/// (npr. upis u red), bez poziva nazad u izvor.
	///
	/// Bezbedno za više niti; ne pretpostavlja UI nit (prelazi mašine se dešavaju i na pool nitima).
	/// </summary>
	public sealed class KioskStatusSource
	{
		private readonly object _lock = new();
		private readonly TimeProvider _timeProvider;
		private readonly ILogger? _logger;
		private readonly List<Subscription> _subscriptions = [];

		private WorkflowState _workflowState = WorkflowState.Idle;
		private bool? _operational;
		private string _lifecycle = KioskLifecycles.Initializing;
		private string? _ownerRole;     // null = kiosk nema vlasnika (KioskOperationCoordinator)
		private string? _operationId;   // aktivna operacija Agenta (npr. sessionId servisne sesije)
		private string? _activity;
		private int? _operationSequence;
		private KioskStatusSnapshot _current;

		public KioskStatusSource(string lockerId, ILogger? logger, TimeProvider? timeProvider = null)
		{
			LockerId = lockerId;
			ClientInstanceId = Guid.NewGuid().ToString("N");
			_logger = logger;
			_timeProvider = timeProvider ?? TimeProvider.System;
			_current = Build(revision: 1, _timeProvider.GetUtcNow());
		}

		public string LockerId { get; }

		/// <summary>Slučajni identitet ovog procesa (nov pri svakom startu); Agent po njemu prepoznaje restart kioska.</summary>
		public string ClientInstanceId { get; }

		public KioskStatusSnapshot Current {
			get { lock (_lock) { return _current; } }
		}

		/// <summary>Ulazak u stanje workflow-a (zove se iz <see cref="WorkflowMachine.OnTransitioned"/>).</summary>
		public void SetWorkflowState(WorkflowState state) => Update(() => _workflowState = state);

		/// <summary>
		/// Vlasnik kioska iz <see cref="KioskOperationCoordinator"/>: uloga dok vlasnik postoji (od rezervacije do kraja
		/// čišćenja), <c>null</c> kad ga nema. Dok vlasnik postoji kiosk je zauzet i na Start-u, sa ulogom vlasnika.
		/// </summary>
		public void SetOwner(string? role) => Update(() => _ownerRole = role);

		/// <summary>Operativnost hardvera: <c>true</c>, <c>false</c>, <c>null</c> = nepoznato.</summary>
		public void SetOperational(bool? operational) => Update(() => _operational = operational);

		/// <summary>
		/// Životni ciklus procesa (<see cref="KioskLifecycles"/>). Gašenje je konačno: posle <see cref="KioskLifecycles.ShuttingDown"/>
		/// kiosk se više ne prijavljuje kao spreman.
		/// </summary>
		public void SetLifecycle(string lifecycle) => Update(() => {
			if (_lifecycle != KioskLifecycles.ShuttingDown) {
				_lifecycle = lifecycle;
			}
		});

		/// <summary>
		/// Početak aktivne operacije Agenta: <see cref="KioskStatus.ActiveOperationId"/>, <see cref="KioskStatus.Activity"/> i
		/// redni broj upisa 0. Nova operacija zamenjuje prethodnu.
		/// </summary>
		public void SetOperation(string operationId, string activity) => Update(() => {
			_operationId = operationId;
			_activity = activity;
			_operationSequence = 0;
		});

		/// <summary>
		/// Redni broj poslednjeg upisa u dnevnik operacije (<see cref="KioskStatus.OperationSequence"/>). Važi samo za tekuću
		/// operaciju: zakasneo poziv za drugu (završenu) operaciju se ignoriše, kao i broj manji od trenutnog.
		/// </summary>
		public void SetOperationSequence(string operationId, int sequence) => Update(() => {
			if (_operationId == operationId && sequence > (_operationSequence ?? 0)) {
				_operationSequence = sequence;
			}
		});

		/// <summary>Kraj operacije; zakasneo poziv za drugu operaciju se ignoriše.</summary>
		public void ClearOperation(string operationId) => Update(() => {
			if (_operationId == operationId) {
				_operationId = null;
				_activity = null;
				_operationSequence = null;
			}
		});

		/// <summary>
		/// Atomski registruje pretplatu: <paramref name="onInitial"/> dobija trenutni snapshot, a <paramref name="onChanged"/>
		/// svaku sledeću reviziju, bez rupe između. Oba callback-a se zovu pod lock-om izvora i moraju biti neblokirajući.
		/// Dispose prekida pretplatu; posle povratka iz Dispose-a callback više nije pozvan.
		/// </summary>
		public IDisposable Subscribe(Action<KioskStatusSnapshot> onChanged, Action<KioskStatusSnapshot>? onInitial = null)
		{
			var subscription = new Subscription(this, onChanged);
			lock (_lock) {
				onInitial?.Invoke(_current);
				_subscriptions.Add(subscription);
			}
			return subscription;
		}

		/// <summary>Snapshot u obliku ugovora; <see cref="KioskStatus.ObservedAtUtc"/> je trenutak poziva.</summary>
		public KioskStatus ToContract(KioskStatusSnapshot snapshot) => new() {
			LockerId = LockerId,
			ClientInstanceId = ClientInstanceId,
			StatusRevision = snapshot.Revision,
			ChangedAtUtc = snapshot.ChangedAtUtc,
			ObservedAtUtc = _timeProvider.GetUtcNow(),
			Busy = snapshot.Busy,
			Role = snapshot.Role,
			WorkflowState = snapshot.WorkflowState.ToString(),
			Operational = snapshot.Operational,
			Lifecycle = snapshot.Lifecycle,
			CanAcceptOpenDoor = snapshot.CanAcceptOpenDoor,
			CannotAcceptReason = snapshot.CannotAcceptReason,
			ActiveOperationId = snapshot.ActiveOperationId,
			Activity = snapshot.Activity,
			OperationSequence = snapshot.OperationSequence,
		};

		private void Update(Action apply)
		{
			lock (_lock) {
				apply();
				var candidate = Build(_current.Revision, _current.ChangedAtUtc);
				if (candidate == _current) {
					return;
				}
				_current = candidate with { Revision = _current.Revision + 1, ChangedAtUtc = _timeProvider.GetUtcNow() };
				// Kopija liste: pretplatnik sme da se odjavi i iz svog callback-a.
				foreach (var subscription in _subscriptions.ToArray()) {
					try {
						subscription.OnChanged(_current);
					}
					catch (Exception ex) {
						_logger?.LogError(ex, "KioskStatusSource: pretplatnik je bacio izuzetak za reviziju {Revision}.", _current.Revision);
					}
				}
			}
		}

		// MORA se pozivati dok se drži _lock.
		private KioskStatusSnapshot Build(long revision, DateTimeOffset changedAtUtc)
		{
			bool busy = _ownerRole != null || !WorkflowStatusRules.IsFree(_workflowState);
			var (canAccept, reason) = WorkflowStatusRules.EvaluateOpenDoor(_lifecycle, _operational, _workflowState, openDoorSupported: false,
																			hasOwner: _ownerRole != null);
			return new KioskStatusSnapshot(revision, changedAtUtc, _workflowState, busy,
											_ownerRole ?? (busy ? WorkflowStatusRules.ResolveRole(_workflowState) : ""),
											_operational, _lifecycle, canAccept, reason,
											_operationId, _activity, _operationSequence);
		}

		private sealed class Subscription(KioskStatusSource owner, Action<KioskStatusSnapshot> onChanged) : IDisposable
		{
			public Action<KioskStatusSnapshot> OnChanged { get; } = onChanged;

			public void Dispose()
			{
				lock (owner._lock) {
					owner._subscriptions.Remove(this);
				}
			}
		}
	}
}
