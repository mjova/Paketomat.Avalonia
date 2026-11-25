using System.IO;
using System.Text.Json;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.AgentCommunication;
using TopPos.Kiosk.Workflow;
using Xunit;
using FakeController = TopPos.Kiosk.Tests.RemoteServiceSessionTests.FakeController;
using S = TopPos.Kiosk.Workflow.WorkflowState;
using T = TopPos.Kiosk.Workflow.WorkflowTrigger;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Komande servisne sesije (<see cref="AgentCommands.BeginRemoteService"/>, <see cref="AgentCommands.OpenDoors"/>,
	/// <see cref="AgentCommands.EndRemoteService"/>, <see cref="AgentCommands.GetCommandResult"/>): mašina, koordinator, izvor
	/// statusa, dnevnik i host workflow-a su povezani kao u aplikaciji; kontroler je lažan.
	/// </summary>
	public class RemoteServiceCommandsTests : IDisposable
	{
		// v4: šifra paketomata iz v2 TestAgentClient (klijent preko named pipe-a dolazi sa transportom u F2).
		private const string __lockerId = "112";
		private const string __code = "123456";
		private static readonly TimeSpan __wait = TimeSpan.FromSeconds(5);
		private static readonly AgentPeer __peer = new(1, "S-1-5-18", 4242);

		private sealed class ManualTime(DateTimeOffset now) : TimeProvider
		{
			public DateTimeOffset Now { get; set; } = now;

			public override DateTimeOffset GetUtcNow() => Now;
		}

		private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("toppos-remote-commands-");

		// Upis dnevnika posle odgovora (npr. zapis kraja otvaranja) može još da traje u pozadini.
		public void Dispose()
		{
			for (int attempt = 1; ; attempt++) {
				try {
					_dir.Delete(recursive: true);
					return;
				}
				catch (IOException) when (attempt < 50) {
					Thread.Sleep(50);
				}
			}
		}

		private string JournalFolder => Path.Combine(_dir.FullName, "AgentOperations");

		private sealed class Kiosk
		{
			public required WorkflowMachine M { get; init; }
			public required KioskStatusSource Status { get; init; }
			public required KioskOperationCoordinator C { get; init; }
			public required AgentOperationJournal Journal { get; init; }
			public required RemoteServiceManager Manager { get; init; }
			public required AgentCommandDispatcher Dispatcher { get; init; }
			public required FakeController Controller { get; init; }
			public required ManualTime Time { get; init; }
			public int ShutdownRequests;

			public AgentRequest Request<TPayload>(string command, TPayload payload, string? requestId = null) => new() {
				RequestId = requestId ?? Guid.NewGuid().ToString("N"),
				LockerId = __lockerId,
				ClientInstanceId = Status.ClientInstanceId,
				Command = command,
				IssuedAtUtc = Time.Now,
				ExpiresAtUtc = Time.Now.AddSeconds(AgentProtocol.MaxRequestTtlSeconds),
				Payload = JsonSerializer.SerializeToElement(payload, AgentJson.Options),
				Reason = "test",
			};

			public AgentRequest Begin(string code = __code, List<LayoutBox>? layout = null, int idle = 600, string? requestId = null)
				=> Request(AgentCommands.BeginRemoteService,
						   new BeginRemoteServicePayload { IdleTimeoutSeconds = idle, PresenceCode = code, Layout = layout ?? Layout(16) }, requestId);

			public AgentRequest Open(string sessionId, List<int>? doors = null, bool all = false, string? requestId = null)
				=> Request(AgentCommands.OpenDoors, new OpenDoorsPayload { SessionId = sessionId, DoorNumbers = doors, All = all }, requestId);

			public AgentRequest End(string sessionId) => Request(AgentCommands.EndRemoteService, new EndRemoteServicePayload { SessionId = sessionId });

			/// <summary>Upit bez ClientInstanceId i roka (radi i posle restarta kioska).</summary>
			public AgentRequest Result(string requestId, int? afterSequence = null) => new() {
				RequestId = Guid.NewGuid().ToString("N"),
				LockerId = __lockerId,
				Command = AgentCommands.GetCommandResult,
				Payload = JsonSerializer.SerializeToElement(new GetCommandResultPayload { RequestId = requestId, AfterSequence = afterSequence }, AgentJson.Options),
			};

			public AgentRequest Shutdown() => Request(AgentCommands.ShutdownApplication, new ShutdownApplicationPayload { Purpose = ShutdownPurposes.Restart });

			/// <summary>Odgovor kakav ide u pipe: odložen odgovor se čeka.</summary>
			public async Task<AgentCommandResult> Send(AgentRequest request)
			{
				var result = Dispatcher.Dispatch(request, __peer)!;
				return result.Pending is { } pending ? await pending.WaitAsync(__wait) : result;
			}

			public async Task<string> BeginActive()
			{
				var begin = Begin();
				Assert.Equal(AgentResultCodes.Completed, (await Send(begin)).ResultCode);
				Assert.Equal(PresenceCodeResult.Accepted, await Manager.Current!.SubmitPresenceCodeAsync(__code));
				return begin.RequestId;
			}
		}

		private static List<LayoutBox> Layout(int doors)
			=> Enumerable.Range(1, doors).Select(d => new LayoutBox { DoorNumber = d, Size = BoxSizes.M, Column = (d - 1) / 8 + 1, Row = (d - 1) % 8 + 1 }).ToList();

		private async Task<Kiosk> StartKiosk(bool started = true, string lifecycle = KioskLifecycles.Ready, bool? operational = true,
											 IReadOnlyList<int>? unavailableModules = null, Func<RemoteServiceSession, ValueTask>? showSession = null)
		{
			var time = new ManualTime(DateTimeOffset.UtcNow);
			var status = new KioskStatusSource(__lockerId, logger: null, time);
			var coordinator = new KioskOperationCoordinator(status, logger: null);
			var m = Mk.Machine(new WorkflowContext());
			m.OnTransitioned((source, destination) => coordinator.OnWorkflowTransition(source, destination));
			m.OnTransitioned(destination => status.SetWorkflowState(destination));
			m.OnEntry(S.Start, () => coordinator.CompleteCleanup(() => Task.CompletedTask));
			var journal = new AgentOperationJournal(JournalFolder, logger: null, time);
			journal.Initialize(RemoteServiceManager.Recoverers);
			var controller = new FakeController(8);
			var host = new WorkflowRemoteServiceHost(m, coordinator, () => unavailableModules ?? [], () => Task.CompletedTask, logger: null, showSession);
			var manager = new RemoteServiceManager(journal, status, controller, host,
												   new RemoteServiceSettings { LockerBoxCount = 16, DeadlineCheckInterval = null }, logger: null, time,
												   delay: _ => Task.CompletedTask);
			Kiosk? kiosk = null;
			var dispatcher = new AgentCommandDispatcher([
								new ShutdownApplicationHandler(status, source => coordinator.TryBeginOperation(m, WorkflowStatusRules.AgentRole, source),
																() => Interlocked.Increment(ref kiosk!.ShutdownRequests), logger: null),
								new BeginRemoteServiceHandler(manager),
								new OpenDoorsHandler(manager),
								new EndRemoteServiceHandler(manager),
							], logger: null, time, queries: [new GetCommandResultHandler(manager)]);
			kiosk = new Kiosk { M = m, Status = status, C = coordinator, Journal = journal, Manager = manager, Dispatcher = dispatcher, Controller = controller, Time = time };
			status.SetOperational(operational);
			if (started) {
				await m.FireAsync(T.Reset);
			}
			status.SetLifecycle(lifecycle);
			return kiosk;
		}

		private static T2 Read<T2>(AgentCommandResult result) where T2 : class
			=> result.Result!.Value.Deserialize<T2>(AgentJson.Options)!;

		private static async Task WaitUntilAsync(Func<bool> condition)
		{
			var deadline = DateTime.UtcNow + __wait;
			while (!condition()) {
				Assert.True(DateTime.UtcNow < deadline, "uslov nije ispunjen u roku");
				await Task.Delay(20);
			}
		}

		// ─── BeginRemoteService ────────────────────────────────────────────────────

		[Fact]
		public async Task Begin_on_free_Start_enters_RemoteService_as_Agent_and_awaits_technician()
		{
			var k = await StartKiosk(unavailableModules: [2]);
			var begin = k.Begin();

			var result = await k.Send(begin);

			Assert.Equal(AgentResultCodes.Completed, result.ResultCode);
			var session = Read<RemoteServiceResult>(result);
			Assert.Equal(begin.RequestId, session.SessionId);
			Assert.Equal(RemoteServicePhases.AwaitingTechnician, session.Phase);
			Assert.Equal([2], session.UnavailableModulesAtStart);
			Assert.Contains("nedostupni moduli: 2", result.Message);
			Assert.Equal(S.RemoteService, k.M.State);
			var status = k.Status.Current;
			Assert.True(status.Busy);
			Assert.Equal(WorkflowStatusRules.AgentRole, status.Role);
			Assert.Equal(begin.RequestId, status.ActiveOperationId);
			Assert.Equal(RemoteServiceSession.Activity, status.Activity);
			Assert.True(k.C.IsActive(k.C.Owner!));
			Assert.True(k.Journal.TryGet(begin.RequestId, out var record));
			Assert.DoesNotContain(__code, record.Fingerprint);
		}

		[Fact]
		public async Task Session_page_is_shown_before_Begin_is_Completed()
		{
			RemoteServiceSession? shown = null;
			var k = await StartKiosk(showSession: session => { shown = session; return ValueTask.CompletedTask; });
			var begin = k.Begin();

			var result = k.Dispatcher.Dispatch(begin, __peer)!;
			var completed = await result.Pending!.WaitAsync(__wait);

			Assert.Equal(AgentResultCodes.Completed, completed.ResultCode);
			Assert.Same(k.Manager.Current, shown);
			Assert.Equal(begin.RequestId, shown!.SessionId);
		}

		[Fact]
		public async Task Begin_does_not_check_operational_state()
		{
			var k = await StartKiosk(operational: false);

			Assert.Equal(AgentResultCodes.Completed, (await k.Send(k.Begin())).ResultCode);
		}

		[Fact]
		public async Task Begin_is_refused_when_kiosk_is_busy_not_ready_or_shutting_down()
		{
			var busy = await StartKiosk();
			var local = busy.C.TryBegin(busy.M, KioskLeaseMode.WorkflowSession, "Client", "test");
			Assert.Equal(AgentResultCodes.Busy, (await busy.Send(busy.Begin())).ResultCode);
			Assert.Same(local, busy.C.Owner);

			var idle = await StartKiosk(started: false);
			Assert.Equal(AgentResultCodes.NotReady, (await idle.Send(idle.Begin())).ResultCode);

			var shutting = await StartKiosk(lifecycle: KioskLifecycles.ShuttingDown);
			Assert.Equal(AgentResultCodes.ShuttingDown, (await shutting.Send(shutting.Begin())).ResultCode);
			Assert.Null(shutting.C.Owner);
		}

		[Theory]
		[InlineData(59, __code)]
		[InlineData(1801, __code)]
		[InlineData(600, "123")]
		[InlineData(600, "12345678901")]
		[InlineData(600, "12a456")]
		public async Task Begin_with_invalid_idle_timeout_or_code_is_InvalidRequest(int idle, string code)
		{
			var k = await StartKiosk();

			Assert.Equal(AgentResultCodes.InvalidRequest, (await k.Send(k.Begin(code, idle: idle))).ResultCode);
			Assert.Null(k.C.Owner);
		}

		[Fact]
		public async Task Begin_without_layout_is_InvalidRequest()
		{
			var k = await StartKiosk();
			var request = k.Request(AgentCommands.BeginRemoteService, new BeginRemoteServicePayload { IdleTimeoutSeconds = 600, PresenceCode = __code });

			Assert.Equal(AgentResultCodes.InvalidRequest, (await k.Send(request)).ResultCode);
		}

		public static TheoryData<List<LayoutBox>> MalformedLayouts() => new() {
			new List<LayoutBox>(),
			new List<LayoutBox> { new() { DoorNumber = 1, Size = "M", Column = 1, Row = 1 }, new() { DoorNumber = 1, Size = "S", Column = 1, Row = 2 } },
			new List<LayoutBox> { new() { DoorNumber = 0, Size = "M", Column = 1, Row = 1 } },
			new List<LayoutBox> { new() { DoorNumber = 1, Size = "XL", Column = 1, Row = 1 } },
			new List<LayoutBox> { new() { DoorNumber = 1, Size = "M", Column = 0, Row = 1 } },
		};

		[Theory]
		[MemberData(nameof(MalformedLayouts))]
		public async Task Begin_with_malformed_layout_is_InvalidDoor(List<LayoutBox> layout)
		{
			var k = await StartKiosk();

			Assert.Equal(AgentResultCodes.InvalidDoor, (await k.Send(k.Begin(layout: layout))).ResultCode);
			Assert.Null(k.C.Owner);
			Assert.Equal(S.Start, k.M.State);
		}

		[Fact]
		public async Task Doors_above_locker_box_count_are_dropped_from_layout_and_listed()
		{
			var k = await StartKiosk();

			var result = await k.Send(k.Begin(layout: Layout(20)));

			Assert.Equal(AgentResultCodes.Completed, result.ResultCode);
			Assert.Equal([17, 18, 19, 20], Read<RemoteServiceResult>(result).IgnoredDoors);
			Assert.Equal(16, k.Manager.Current!.Layout.Count);
		}

		[Fact]
		public async Task Repeated_Begin_returns_the_same_session_and_different_content_conflicts()
		{
			var k = await StartKiosk();
			var begin = k.Begin();
			await k.Send(begin);

			var repeated = await k.Send(begin);
			Assert.Equal(AgentResultCodes.Completed, repeated.ResultCode);
			Assert.Equal(begin.RequestId, Read<RemoteServiceResult>(repeated).SessionId);

			var conflict = k.Begin(code: "654321", requestId: begin.RequestId);
			Assert.Equal(AgentResultCodes.DuplicateConflict, (await k.Send(conflict)).ResultCode);
			Assert.Equal(begin.RequestId, k.Manager.Current!.SessionId);
		}

		// ─── OpenDoors ─────────────────────────────────────────────────────────────

		[Fact]
		public async Task OpenDoors_before_login_unknown_session_and_bad_door_are_refused()
		{
			var k = await StartKiosk();
			var begin = k.Begin();
			await k.Send(begin);

			Assert.Equal(AgentResultCodes.TechnicianNotPresent, (await k.Send(k.Open(begin.RequestId, [1]))).ResultCode);
			Assert.Equal(AgentResultCodes.NoServiceSession, (await k.Send(k.Open("nepoznata", [1]))).ResultCode);
			await k.Manager.Current!.SubmitPresenceCodeAsync(__code);
			Assert.Equal(AgentResultCodes.InvalidDoor, (await k.Send(k.Open(begin.RequestId, [17]))).ResultCode);
			Assert.Equal(AgentResultCodes.InvalidRequest, (await k.Send(k.Open(begin.RequestId, [1], all: true))).ResultCode);
			Assert.Empty(k.Controller.Opened);
		}

		[Fact]
		public async Task OpenDoors_is_InProgress_and_outcome_comes_through_GetCommandResult()
		{
			var k = await StartKiosk();
			var sessionId = await k.BeginActive();
			var open = k.Open(sessionId, [1, 2]);

			var accepted = await k.Send(open);

			Assert.Equal(AgentResultCodes.InProgress, accepted.ResultCode);
			await WaitUntilAsync(() => !k.Manager.Current!.IsOpening);
			var result = await k.Send(k.Result(open.RequestId));
			Assert.Equal(AgentResultCodes.Completed, result.ResultCode);
			var openings = Read<OpenDoorsResult>(result);
			Assert.True(openings.Finished);
			Assert.Equal([1, 2], openings.Openings.Select(o => o.DoorNumber));
			Assert.All(openings.Openings, o => Assert.Equal(DoorOpenSources.Portal, o.Source));

			var session = await k.Send(k.Result(sessionId));
			Assert.Equal(AgentResultCodes.InProgress, session.ResultCode);
			Assert.Equal(2, Read<RemoteServiceResult>(session).Openings.Count);
			Assert.Equal(Read<RemoteServiceResult>(session).LastSequence, k.Status.Current.OperationSequence);

			var later = await k.Send(k.Result(sessionId, afterSequence: openings.Openings[0].Sequence));
			Assert.Equal([2], Read<RemoteServiceResult>(later).Openings.Select(o => o.DoorNumber));
		}

		[Fact]
		public async Task Repeated_OpenDoors_does_not_open_again()
		{
			var k = await StartKiosk();
			var sessionId = await k.BeginActive();
			var open = k.Open(sessionId, [3]);
			await k.Send(open);
			await WaitUntilAsync(() => !k.Manager.Current!.IsOpening);

			var repeated = await k.Send(open);

			Assert.Equal(AgentResultCodes.Completed, repeated.ResultCode);
			Assert.Equal([3], k.Controller.Opened);
			var conflict = k.Open(sessionId, [4], requestId: open.RequestId);
			Assert.Equal(AgentResultCodes.DuplicateConflict, (await k.Send(conflict)).ResultCode);
		}

		// ─── EndRemoteService, gašenje i rezultat ──────────────────────────────────

		[Fact]
		public async Task End_returns_session_result_frees_the_kiosk_and_repeats_safely()
		{
			var k = await StartKiosk();
			var sessionId = await k.BeginActive();
			await k.Send(k.Open(sessionId, [1]));
			await WaitUntilAsync(() => !k.Manager.Current!.IsOpening);

			var end = await k.Send(k.End(sessionId));

			Assert.Equal(AgentResultCodes.Completed, end.ResultCode);
			var result = Read<RemoteServiceResult>(end);
			Assert.Equal(RemoteServicePhases.Ended, result.Phase);
			Assert.Equal(RemoteServiceEndModes.PortalConfirmed, result.EndMode);
			Assert.Equal([1, 2], k.Controller.CheckedModules);
			await WaitUntilAsync(() => k.C.Owner == null);
			Assert.Equal(S.Start, k.M.State);
			Assert.Null(k.Status.Current.ActiveOperationId);
			Assert.False(k.Status.Current.Busy);
			Assert.Null(k.Manager.Current);

			var again = await k.Send(k.End(sessionId));
			Assert.Equal(AgentResultCodes.Completed, again.ResultCode);
			Assert.Equal(RemoteServiceEndModes.PortalConfirmed, Read<RemoteServiceResult>(again).EndMode);
			Assert.Equal(AgentResultCodes.NoServiceSession, (await k.Send(k.Open(sessionId, [2]))).ResultCode);
			Assert.Equal(AgentResultCodes.Completed, (await k.Send(k.Result(sessionId))).ResultCode);
			Assert.Equal(AgentResultCodes.NoServiceSession, (await k.Send(k.End("nepoznata"))).ResultCode);
		}

		[Fact]
		public async Task End_before_login_is_PortalCancelled()
		{
			var k = await StartKiosk();
			var begin = k.Begin();
			await k.Send(begin);

			var end = await k.Send(k.End(begin.RequestId));

			Assert.Equal(RemoteServiceEndModes.PortalCancelled, Read<RemoteServiceResult>(end).EndMode);
			Assert.Empty(k.Controller.CheckedModules);
		}

		[Fact]
		public async Task End_while_Begin_is_starting_waits_for_the_start_and_frees_the_kiosk()
		{
			// Početak stoji na prikazu stranice; kraj iz portala („Odustani") stiže pre odgovora na Begin.
			var showing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var k = await StartKiosk(showSession: async _ => {
				showing.TrySetResult();
				await gate.Task;
			});
			var begin = k.Begin();
			var beginPending = k.Dispatcher.Dispatch(begin, __peer)!.Pending!;
			await showing.Task.WaitAsync(__wait);

			var endPending = k.Dispatcher.Dispatch(k.End(begin.RequestId), __peer)!.Pending!;
			await Task.Delay(200);
			Assert.False(endPending.IsCompleted, "kraj ne sme da prođe dok početak nije završen");
			gate.SetResult();

			Assert.Equal(AgentResultCodes.Completed, (await beginPending.WaitAsync(__wait)).ResultCode);
			var end = await endPending.WaitAsync(__wait);
			Assert.Equal(AgentResultCodes.Completed, end.ResultCode);
			Assert.Equal(RemoteServiceEndModes.PortalCancelled, Read<RemoteServiceResult>(end).EndMode);
			await WaitUntilAsync(() => k.C.Owner == null);
			Assert.Equal(S.Start, k.M.State);
			Assert.Null(k.Manager.Current);
			Assert.Equal(AgentResultCodes.Completed, (await k.Send(k.Begin())).ResultCode);
		}

		[Fact]
		public async Task Shutdown_and_local_inputs_are_refused_during_session()
		{
			var k = await StartKiosk();
			await k.BeginActive();

			Assert.Equal(AgentResultCodes.Busy, (await k.Send(k.Shutdown())).ResultCode);
			Assert.Equal(0, k.ShutdownRequests);
			Assert.Null(k.C.TryBegin(k.M, KioskLeaseMode.WorkflowSession, "Client", "test"));
			Assert.Equal(AgentResultCodes.Busy, (await k.Send(k.Begin())).ResultCode);
		}

		[Fact]
		public async Task GetCommandResult_for_unknown_id_is_ResultNotFound()
		{
			var k = await StartKiosk();

			Assert.Equal(AgentResultCodes.ResultNotFound, (await k.Send(k.Result("nepoznat"))).ResultCode);
		}

		[Fact]
		public async Task Session_interrupted_by_restart_is_ProcessRestart_and_kiosk_accepts_a_new_one()
		{
			var k = await StartKiosk();
			var sessionId = await k.BeginActive();

			var restarted = await StartKiosk();     // nov proces nad istim dnevnikom
			var result = await restarted.Send(restarted.Result(sessionId));

			Assert.Equal(AgentResultCodes.Completed, result.ResultCode);
			Assert.Equal(RemoteServiceEndModes.ProcessRestart, Read<RemoteServiceResult>(result).EndMode);
			Assert.Equal(AgentResultCodes.Completed, (await restarted.Send(restarted.End(sessionId))).ResultCode);
			Assert.Equal(AgentResultCodes.Completed, (await restarted.Send(restarted.Begin())).ResultCode);
		}

		// ─── Preko pravog pipe-a ───────────────────────────────────────────────────

		// v4: test preko named pipe-a (sesija preživi prekid veze; TestServer, klijent Agenta) dolazi sa transportom u F2.
	}
}
