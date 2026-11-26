using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.AgentCommunication;
using TopPos.Kiosk.Workflow;
using Xunit;
using S = TopPos.Kiosk.Workflow.WorkflowState;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Jedan memorijski izvor statusa (plan §5): revizija raste samo na stvarnu promenu, pretplata je atomska u odnosu na
	/// promene, a status.lock i spremnost za OpenDoor se izvode iz istog izvora.
	/// </summary>
	public class KioskStatusSourceTests
	{
		[Fact]
		public void Initial_snapshot_is_idle_free_unknown_and_initializing()
		{
			var source = new KioskStatusSource("112", logger: null);

			var s = source.Current;
			Assert.Equal(1, s.Revision);
			Assert.Equal(S.Idle, s.WorkflowState);
			Assert.False(s.Busy);
			Assert.Equal("", s.Role);
			Assert.Null(s.Operational);
			Assert.Equal(KioskLifecycles.Initializing, s.Lifecycle);
			Assert.False(s.CanAcceptOpenDoor);
			Assert.Equal(CannotAcceptReasons.NotReady, s.CannotAcceptReason);
			Assert.Equal(32, source.ClientInstanceId.Length);
			Assert.Null(s.ActiveOperationId);
			Assert.Null(s.Activity);
			Assert.Null(s.OperationSequence);
		}

		[Fact]
		public void Operation_id_activity_and_sequence_reach_the_contract_and_each_change_is_a_revision()
		{
			var source = new KioskStatusSource("112", logger: null);
			var seen = new List<KioskStatusSnapshot>();
			source.Subscribe(seen.Add);

			source.SetOperation("s1", "RemoteService");
			source.SetOperationSequence("s1", 1);
			source.SetOperationSequence("s1", 3);

			var status = source.ToContract(source.Current);
			Assert.Equal("s1", status.ActiveOperationId);
			Assert.Equal("RemoteService", status.Activity);
			Assert.Equal(3, status.OperationSequence);
			Assert.Equal([0, 1, 3], seen.Select(s => s.OperationSequence!.Value));

			source.ClearOperation("s1");
			var cleared = source.ToContract(source.Current);
			Assert.Null(cleared.ActiveOperationId);
			Assert.Null(cleared.Activity);
			Assert.Null(cleared.OperationSequence);
		}

		[Fact]
		public void Late_sequence_or_clear_for_another_operation_and_lower_sequence_are_ignored()
		{
			var source = new KioskStatusSource("112", logger: null);
			source.SetOperation("s2", "RemoteService");
			source.SetOperationSequence("s2", 5);
			long revision = source.Current.Revision;

			source.SetOperationSequence("s1", 9);   // zakasneo poziv završene operacije
			source.SetOperationSequence("s2", 4);   // manji broj
			source.ClearOperation("s1");

			Assert.Equal(revision, source.Current.Revision);
			Assert.Equal("s2", source.Current.ActiveOperationId);
			Assert.Equal(5, source.Current.OperationSequence);
		}

		[Fact]
		public void Revision_grows_only_on_real_change()
		{
			var source = new KioskStatusSource("112", logger: null);
			var seen = new List<long>();
			source.Subscribe(s => seen.Add(s.Revision));

			source.SetWorkflowState(S.Start);
			source.SetWorkflowState(S.Start);              // Reset iz Start-a u Start: bez promene
			source.SetOperational(true);
			source.SetOperational(true);
			source.SetWorkflowState(S.CourierAuthenticating);

			Assert.Equal([2, 3, 4], seen);
			Assert.Equal(4, source.Current.Revision);
		}

		[Fact]
		public void Busy_and_role_follow_workflow_state()
		{
			var source = new KioskStatusSource("112", logger: null);

			source.SetWorkflowState(S.ServiceDoorOpening);
			Assert.True(source.Current.Busy);
			Assert.Equal("Service", source.Current.Role);

			source.SetWorkflowState(S.RecipientPayment);
			Assert.Equal("Client", source.Current.Role);

			source.SetWorkflowState(S.Start);
			Assert.False(source.Current.Busy);
			Assert.Equal("", source.Current.Role);
		}

		[Fact]
		public void Owner_makes_Start_busy_with_owner_role()
		{
			var source = new KioskStatusSource("112", logger: null);
			source.SetLifecycle(KioskLifecycles.Ready);
			source.SetOperational(true);
			source.SetWorkflowState(S.Start);
			Assert.Equal(CannotAcceptReasons.NotSupported, source.Current.CannotAcceptReason);

			source.SetOwner(WorkflowStatusRules.AgentRole);
			Assert.True(source.Current.Busy);
			Assert.Equal("Agent", source.Current.Role);
			Assert.Equal(CannotAcceptReasons.Busy, source.Current.CannotAcceptReason);

			source.SetWorkflowState(S.CourierAuthenticating);
			Assert.Equal("Agent", source.Current.Role);     // uloga vlasnika ima prednost nad stanjem

			source.SetWorkflowState(S.Start);
			source.SetOwner(null);
			Assert.False(source.Current.Busy);
			Assert.Equal("", source.Current.Role);
			Assert.Equal(CannotAcceptReasons.NotSupported, source.Current.CannotAcceptReason);
		}

		[Fact]
		public void Shutting_down_is_final()
		{
			var source = new KioskStatusSource("112", logger: null);
			source.SetLifecycle(KioskLifecycles.Ready);
			source.SetLifecycle(KioskLifecycles.ShuttingDown);
			source.SetLifecycle(KioskLifecycles.Ready);

			Assert.Equal(KioskLifecycles.ShuttingDown, source.Current.Lifecycle);
			Assert.Equal(CannotAcceptReasons.ShuttingDown, source.Current.CannotAcceptReason);
		}

		/// <summary>U ovoj verziji kiosk ne prima fizičke komande: ni potpuno slobodan, spreman i ispravan kiosk ne nudi OpenDoor.</summary>
		[Fact]
		public void Free_ready_operational_kiosk_still_cannot_accept_open_door_in_this_version()
		{
			var source = new KioskStatusSource("112", logger: null);
			source.SetLifecycle(KioskLifecycles.Ready);
			source.SetOperational(true);
			source.SetWorkflowState(S.Start);

			Assert.False(source.Current.CanAcceptOpenDoor);
			Assert.Equal(CannotAcceptReasons.NotSupported, source.Current.CannotAcceptReason);
		}

		[Theory]
		[InlineData(KioskLifecycles.ShuttingDown, true, S.Start, CannotAcceptReasons.ShuttingDown)]
		[InlineData(KioskLifecycles.RecoveryRequired, true, S.Start, CannotAcceptReasons.RecoveryRequired)]
		[InlineData(KioskLifecycles.Initializing, true, S.Start, CannotAcceptReasons.NotReady)]
		[InlineData(KioskLifecycles.Ready, null, S.Start, CannotAcceptReasons.NotOperational)]
		[InlineData(KioskLifecycles.Ready, false, S.Start, CannotAcceptReasons.NotOperational)]
		[InlineData(KioskLifecycles.Ready, true, S.Idle, CannotAcceptReasons.NotReady)]
		[InlineData(KioskLifecycles.Ready, true, S.CourierAuthenticating, CannotAcceptReasons.Busy)]
		[InlineData(KioskLifecycles.Ready, true, S.RecipientPayment, CannotAcceptReasons.Busy)]
		[InlineData(KioskLifecycles.Ready, true, S.ServiceDoorOpening, CannotAcceptReasons.Busy)]
		public void Open_door_readiness_reason(string lifecycle, bool? operational, S state, string expected)
		{
			var (canAccept, reason) = WorkflowStatusRules.EvaluateOpenDoor(lifecycle, operational, state, openDoorSupported: true);

			Assert.False(canAccept);
			Assert.Equal(expected, reason);
		}

		[Fact]
		public void Open_door_is_offered_only_when_supported_and_everything_is_ready()
		{
			Assert.Equal((true, (string?) null), WorkflowStatusRules.EvaluateOpenDoor(KioskLifecycles.Ready, true, S.Start, openDoorSupported: true));
		}

		/// <summary>
		/// Pretplata usred promena sa druge niti: početni snapshot i naredne revizije su neprekidan niz — nijedna promena
		/// nije izgubljena između očitavanja i registracije, i nijedna nije stigla dvaput.
		/// </summary>
		[Fact]
		public async Task Subscribe_during_concurrent_changes_has_no_gap_and_no_duplicate()
		{
			for (int iteration = 0; iteration < 200; iteration++) {
				var source = new KioskStatusSource("112", logger: null);
				using var go = new Barrier(2);
				var writer = Task.Run(() => {
					go.SignalAndWait();
					for (int k = 0; k < 200; k++) {
						source.SetOperational(k % 2 == 0);
					}
				});

				var revisions = new ConcurrentQueue<long>();
				go.SignalAndWait();
				using var subscription = source.Subscribe(onChanged: s => revisions.Enqueue(s.Revision), onInitial: s => revisions.Enqueue(s.Revision));
				await writer;

				var list = revisions.ToArray();
				for (int i = 1; i < list.Length; i++) {
					Assert.Equal(list[i - 1] + 1, list[i]);
				}
				Assert.Equal(source.Current.Revision, list[^1]);
			}
		}

		[Fact]
		public void Disposed_subscription_gets_no_more_changes()
		{
			var source = new KioskStatusSource("112", logger: null);
			int calls = 0;
			var subscription = source.Subscribe(_ => calls++);
			source.SetWorkflowState(S.Start);
			subscription.Dispose();
			source.SetWorkflowState(S.RecipientCodeInput);

			Assert.Equal(1, calls);
		}

		[Fact]
		public void Contract_snapshot_carries_identity_and_observation_time()
		{
			var source = new KioskStatusSource("112", logger: null);
			source.SetWorkflowState(S.CourierManualLogin);

			var contract = source.ToContract(source.Current);

			Assert.Equal("112", contract.LockerId);
			Assert.Equal(source.ClientInstanceId, contract.ClientInstanceId);
			Assert.Equal(2, contract.StatusRevision);
			Assert.Equal("CourierManualLogin", contract.WorkflowState);
			Assert.Equal("Courier", contract.Role);
			Assert.True(contract.ObservedAtUtc >= contract.ChangedAtUtc);
		}

		/// <summary>status.lock je izvedeni zapis istog izvora: zauzetost, uloga i operativnost u fajlu prate izvor.</summary>
		[Fact]
		public void Status_lock_is_derived_from_the_source()
		{
			var dir = Directory.CreateTempSubdirectory("toppos-status-");
			try {
				var semaphore = new StatusSemaphore(dir.FullName, logger: null);
				semaphore.InitializeSession();
				var source = new KioskStatusSource("112", logger: null);
				source.Subscribe(s => semaphore.Update(s.Busy, s.WorkflowState, s.Operational));

				source.SetOperational(true);
				source.SetWorkflowState(S.CourierAuthenticating);
				Assert.True(semaphore.WaitForPendingWrites(TimeSpan.FromSeconds(5)));

				using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "status.lock")));
				var root = doc.RootElement;
				Assert.True(root.GetProperty("Busy").GetBoolean());
				Assert.Equal("Courier", root.GetProperty("Role").GetString());
				Assert.Equal("CourierAuthenticating", root.GetProperty("State").GetString());
				Assert.True(root.GetProperty("Operational").GetBoolean());
				Assert.False(root.GetProperty("CleanShutdown").GetBoolean());
			}
			finally {
				dir.Delete(recursive: true);
			}
		}
	}

	/// <summary>Izlazni red veze: odgovori se ne gube, statusni događaji se spajaju, heartbeat ne vraća stariju reviziju.</summary>
	public class AgentOutboundQueueTests
	{
		private static KioskStatusSnapshot Snapshot(long revision)
			=> new(revision, DateTimeOffset.UtcNow, S.Start, false, "", true, KioskLifecycles.Ready, false, CannotAcceptReasons.NotSupported);

		private static AgentResponse Response(string id) => new() { RequestId = id };

		[Fact]
		public async Task Responses_keep_order_and_status_events_coalesce_in_place()
		{
			var queue = new AgentOutboundQueue(maxPendingResponses: 10);
			queue.TryEnqueueResponse(Response("subscribe"));
			queue.EnqueueStatusChanged(Snapshot(2), "sub");
			queue.TryEnqueueResponse(Response("get"));
			queue.EnqueueStatusChanged(Snapshot(3), "sub");
			queue.EnqueueHeartbeat(Snapshot(3), "sub");

			var first = await queue.DequeueAsync(CancellationToken.None);
			var second = await queue.DequeueAsync(CancellationToken.None);
			var third = await queue.DequeueAsync(CancellationToken.None);

			Assert.Equal("subscribe", first!.Response!.RequestId);
			Assert.Equal(3, second!.Status!.Revision);
			Assert.Equal(AgentEvents.StatusChanged, second.EventKind);   // spojen heartbeat ne briše promenu
			Assert.Equal("get", third!.Response!.RequestId);
		}

		[Fact]
		public async Task Heartbeat_never_sends_older_revision_than_already_queued()
		{
			var queue = new AgentOutboundQueue(maxPendingResponses: 10);
			queue.EnqueueStatusChanged(Snapshot(5), "sub");
			var changed = await queue.DequeueAsync(CancellationToken.None);

			queue.EnqueueHeartbeat(Snapshot(4), "sub");
			var heartbeat = await queue.DequeueAsync(CancellationToken.None);

			Assert.Equal(5, changed!.Status!.Revision);
			Assert.Equal(AgentEvents.Heartbeat, heartbeat!.EventKind);
			Assert.Equal(5, heartbeat.Status!.Revision);
		}

		[Fact]
		public void Pending_response_limit_is_enforced()
		{
			var queue = new AgentOutboundQueue(maxPendingResponses: 2);

			Assert.True(queue.TryEnqueueResponse(Response("1")));
			Assert.True(queue.TryEnqueueResponse(Response("2")));
			Assert.False(queue.TryEnqueueResponse(Response("3")));
		}

		[Fact]
		public async Task Completed_queue_drains_then_returns_null()
		{
			var queue = new AgentOutboundQueue(maxPendingResponses: 10);
			queue.TryEnqueueResponse(Response("1"));
			queue.Complete();

			Assert.False(queue.TryEnqueueResponse(Response("2")));
			Assert.NotNull(await queue.DequeueAsync(CancellationToken.None));
			Assert.Null(await queue.DequeueAsync(CancellationToken.None));
		}
	}
}
