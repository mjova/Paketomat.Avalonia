using TopPos.Agent.Contracts;
using TopPos.Kiosk.Workflow;

namespace TopPos.Kiosk.AgentCommunication
{
	/// <summary>
	/// Izlazni red jedne veze (plan §5, „Isti kanal za odgovore i događaje“). Odgovori se ne gube i ne spajaju; ograničen
	/// je njihov broj (prekoračenje = veza se zatvara). Neposlat statusni događaj se zamenjuje novijim snapshot-om na istom
	/// mestu u redu, pa red ostaje ograničen i kad je Agent spor. Redosled upisa je redosled slanja: odgovor na pretplatu
	/// je u redu pre prvog događaja te pretplate.
	///
	/// Upis je neblokirajući (sme da se zove pod lock-om <see cref="KioskStatusSource"/>); čita samo jedan pisac veze.
	/// </summary>
	public sealed class AgentOutboundQueue
	{
		private readonly object _lock = new();
		private readonly LinkedList<Entry> _entries = new();
		private readonly SemaphoreSlim _signal = new(0);
		private readonly int _maxPendingResponses;
		private int _pendingResponses;
		// Neposlat statusni događaj (još u redu) — novi se spaja u njega.
		private Entry? _pendingStatus;
		// Poslednji snapshot stavljen u red; heartbeat ne sme da pošalje stariju reviziju od njega.
		private KioskStatusSnapshot? _lastStatus;
		private bool _completed;

		public AgentOutboundQueue(int maxPendingResponses)
		{
			_maxPendingResponses = maxPendingResponses;
		}

		/// <summary>Stavka reda: odgovor ili statusni događaj (snapshot se pretvara u poruku tek pri slanju).</summary>
		public sealed class Entry
		{
			public AgentResponse? Response { get; init; }

			public KioskStatusSnapshot? Status { get; set; }

			public string? EventKind { get; set; }

			public string? SubscriptionId { get; set; }

			/// <summary>Ishod upisa odgovora (<c>true</c> = upisan u pipe); završava ga pisac veze ili <see cref="CompleteAndDropUnsent"/>.</summary>
			public TaskCompletionSource<bool>? Written { get; init; }
		}

		/// <summary>
		/// Dodaje odgovor; vraća false ako je red završen ili je dostignut limit neposlatih odgovora. <paramref name="written"/>
		/// (opciono) dobija ishod upisa tog odgovora.
		/// </summary>
		public bool TryEnqueueResponse(AgentResponse response, TaskCompletionSource<bool>? written = null)
		{
			lock (_lock) {
				if (_completed || _pendingResponses >= _maxPendingResponses) {
					return false;
				}
				_pendingResponses++;
				_entries.AddLast(new Entry { Response = response, Written = written });
			}
			_signal.Release();
			return true;
		}

		/// <summary>
		/// Pisac je završio (red ispražnjen, veza pukla ili zatvorena): ništa se više ne dodaje, a neposlati odgovori dobijaju
		/// ishod „nije upisano“.
		/// </summary>
		public void CompleteAndDropUnsent()
		{
			List<TaskCompletionSource<bool>> unsent;
			lock (_lock) {
				_completed = true;
				unsent = [.. _entries.Select(e => e.Written).OfType<TaskCompletionSource<bool>>()];
				_entries.Clear();
				_pendingStatus = null;
				_pendingResponses = 0;
			}
			_signal.Release();
			foreach (var written in unsent) {
				written.TrySetResult(false);
			}
		}

		/// <summary>Promena statusa (nova revizija) za pretplatu; spaja se sa neposlatim statusnim događajem.</summary>
		public void EnqueueStatusChanged(KioskStatusSnapshot status, string subscriptionId)
			=> EnqueueStatus(status, AgentEvents.StatusChanged, subscriptionId);

		/// <summary>
		/// Heartbeat: aktuelni snapshot bez nove revizije. Ako je u red već stavljena novija revizija, šalje se ona — heartbeat
		/// ne sme da vrati stariji status.
		/// </summary>
		public void EnqueueHeartbeat(KioskStatusSnapshot status, string subscriptionId)
			=> EnqueueStatus(status, AgentEvents.Heartbeat, subscriptionId);

		/// <summary>Posle ovoga se ništa ne dodaje; pisac pošalje ono što je ostalo i dobija <c>null</c>.</summary>
		public void Complete()
		{
			lock (_lock) {
				_completed = true;
			}
			_signal.Release();
		}

		/// <summary>Sledeća stavka; <c>null</c> kad je red završen i prazan.</summary>
		public async Task<Entry?> DequeueAsync(CancellationToken cancellationToken)
		{
			while (true) {
				lock (_lock) {
					var first = _entries.First;
					if (first != null) {
						_entries.RemoveFirst();
						var entry = first.Value;
						if (entry.Response != null) {
							_pendingResponses--;
						}
						if (entry == _pendingStatus) {
							_pendingStatus = null;
						}
						return entry;
					}
					if (_completed) {
						return null;
					}
				}
				await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
			}
		}

		private void EnqueueStatus(KioskStatusSnapshot status, string eventKind, string subscriptionId)
		{
			lock (_lock) {
				if (_completed) {
					return;
				}
				if (_lastStatus != null && _lastStatus.Revision > status.Revision) {
					status = _lastStatus;
				}
				_lastStatus = status;
				if (_pendingStatus != null) {
					// Spajanje: najnoviji snapshot na mestu neposlatog; promena ostaje promena i kad je novi heartbeat.
					_pendingStatus.Status = status;
					_pendingStatus.SubscriptionId = subscriptionId;
					if (eventKind == AgentEvents.StatusChanged) {
						_pendingStatus.EventKind = AgentEvents.StatusChanged;
					}
					return;
				}
				_pendingStatus = new Entry { Status = status, EventKind = eventKind, SubscriptionId = subscriptionId };
				_entries.AddLast(_pendingStatus);
			}
			_signal.Release();
		}

		/// <summary>Beleži snapshot iz odgovora na pretplatu, da kasniji heartbeat ne pošalje stariju reviziju.</summary>
		public void NoteStatusSent(KioskStatusSnapshot status)
		{
			lock (_lock) {
				if (_lastStatus == null || status.Revision >= _lastStatus.Revision) {
					_lastStatus = status;
				}
			}
		}
	}
}
