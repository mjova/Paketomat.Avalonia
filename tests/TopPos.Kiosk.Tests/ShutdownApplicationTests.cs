using System.Text.Json;
using Microsoft.Extensions.Logging;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.AgentCommunication;
using TopPos.Kiosk.Workflow;
using Xunit;
using S = TopPos.Kiosk.Workflow.WorkflowState;
using T = TopPos.Kiosk.Workflow.WorkflowTrigger;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Komanda Agenta ShutdownApplication (doc/notes/Info/Ugovor pipe komandi/ShutdownApplication.md): prihvata se samo kad
	/// koordinator atomski dodeli vlasništvo Agentu, inače odmah odbijanje bez promene toka; gašenje programa tek posle upisa
	/// odgovora. Mašina, koordinator i izvor statusa su povezani kao u aplikaciji; gašenje programa je brojač (u aplikaciji
	/// <c>App.Shutdown</c> → <c>OnExit</c>, koji ova komanda ne menja).
	/// </summary>
	public class ShutdownApplicationTests
	{
		// v4: šifra paketomata iz v2 TestAgentClient (klijent preko named pipe-a dolazi sa transportom u F2).
		private const string __lockerId = "112";
		private static readonly TimeSpan __wait = TimeSpan.FromSeconds(5);
		private static readonly AgentPeer __peer = new(1, "S-1-5-18", 4242);

		private sealed class ManualTime(DateTimeOffset now) : TimeProvider
		{
			public DateTimeOffset Now { get; set; } = now;

			public override DateTimeOffset GetUtcNow() => Now;
		}

		private sealed class Kiosk
		{
			private int _shutdowns;

			public required WorkflowMachine M { get; init; }
			public required KioskStatusSource Status { get; init; }
			public required KioskOperationCoordinator C { get; init; }
			public required KioskOperationCoordinatorTests.ListLogger Log { get; init; }
			public required ManualTime Time { get; init; }
			public AgentCommandDispatcher Dispatcher { get; private set; } = null!;
			/// <summary>Dodatna radnja pri gašenju programa (pipe testovi: zatvaranje servera, kao OnExit).</summary>
			public Action? OnShutdown { get; set; }
			public int ShutdownRequests => Volatile.Read(ref _shutdowns);

			/// <param name="started">Mašina dovedena u Start (inače ostaje Idle).</param>
			/// <param name="lifecycle">Životni ciklus posle starta (u aplikaciji Ready posle pokretanja kanala).</param>
			public static async Task<Kiosk> StartAsync(bool started = true, string lifecycle = KioskLifecycles.Ready)
			{
				var log = new KioskOperationCoordinatorTests.ListLogger();
				var status = new KioskStatusSource(__lockerId, logger: null);
				var coordinator = new KioskOperationCoordinator(status, log);
				var m = Mk.Machine(new WorkflowContext());
				var kiosk = new Kiosk { M = m, Status = status, C = coordinator, Log = log, Time = new ManualTime(DateTimeOffset.UtcNow) };
				m.OnTransitioned((source, destination) => coordinator.OnWorkflowTransition(source, destination));
				m.OnTransitioned(destination => status.SetWorkflowState(destination));
				m.OnEntry(S.Start, () => coordinator.CompleteCleanup(() => Task.CompletedTask));
				var handler = new ShutdownApplicationHandler(status, source => coordinator.TryBeginOperation(m, WorkflowStatusRules.AgentRole, source),
																kiosk.RequestShutdown, log);
				kiosk.Dispatcher = new AgentCommandDispatcher([handler], log, kiosk.Time);
				status.SetOperational(true);
				if (started) {
					await m.FireAsync(T.Reset);
				}
				status.SetLifecycle(lifecycle);
				return kiosk;
			}

			private void RequestShutdown()
			{
				Interlocked.Increment(ref _shutdowns);
				OnShutdown?.Invoke();
			}

			public AgentRequest Request(string purpose = ShutdownPurposes.Restart, string? requestId = null) => new() {
				RequestId = requestId ?? Guid.NewGuid().ToString("N"),
				LockerId = __lockerId,
				ClientInstanceId = Status.ClientInstanceId,
				Command = AgentCommands.ShutdownApplication,
				IssuedAtUtc = Time.Now,
				ExpiresAtUtc = Time.Now.AddSeconds(AgentProtocol.MaxRequestTtlSeconds),
				Payload = JsonSerializer.SerializeToElement(new ShutdownApplicationPayload { Purpose = purpose }, AgentJson.Options),
				Reason = "test",
			};

			public AgentCommandResult Send(AgentRequest request) => Dispatcher.Dispatch(request, __peer)!;

			public KioskLease? BeginSession() => C.TryBegin(M, KioskLeaseMode.WorkflowSession, "Client", "test");

			/// <summary>Odbijanje: tok, vlasnik i životni ciklus netaknuti, gašenja nema.</summary>
			public void AssertRefused(AgentCommandResult result, string expectedCode, S state, KioskLease? owner, string lifecycle = KioskLifecycles.Ready)
			{
				Assert.Equal(expectedCode, result.ResultCode);
				Assert.Null(result.AfterResponse);
				Assert.Equal(state, M.State);
				Assert.Same(owner, C.Owner);
				Assert.Equal(lifecycle, Status.Current.Lifecycle);
				Assert.Equal(0, ShutdownRequests);
				Assert.Contains(Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(expectedCode));
			}
		}

		// ─── Prihvatanje ───────────────────────────────────────────────────────────

		[Fact]
		public async Task Free_Start_accepts_and_shuts_down_only_after_the_response_is_written()
		{
			var k = await Kiosk.StartAsync();

			var result = k.Send(k.Request());

			Assert.Equal(AgentResultCodes.Completed, result.ResultCode);
			var owner = k.C.Owner;
			Assert.NotNull(owner);
			Assert.Equal(WorkflowStatusRules.AgentRole, owner.Role);
			Assert.True(k.C.IsActive(owner));
			var status = k.Status.Current;
			Assert.True(status.Busy);
			Assert.Equal(WorkflowStatusRules.AgentRole, status.Role);
			Assert.Equal(KioskLifecycles.ShuttingDown, status.Lifecycle);
			Assert.Equal(S.Start, k.M.State);
			Assert.Equal(0, k.ShutdownRequests);     // tek posle upisa odgovora

			result.AfterResponse!(true);

			Assert.Equal(1, k.ShutdownRequests);
			Assert.Contains(k.Log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Restart") && e.Message.Contains("S-1-5-18")
																				&& e.Message.Contains("4242") && e.Message.Contains("„test“"));
			// Novi ulazi su odbijeni: kiosk ima vlasnika Agenta.
			Assert.Null(k.BeginSession());
		}

		[Fact]
		public async Task Shutdown_continues_when_the_response_did_not_reach_the_agent()
		{
			var k = await Kiosk.StartAsync();

			k.Send(k.Request()).AfterResponse!(false);

			Assert.Equal(1, k.ShutdownRequests);
			Assert.Contains(k.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("nije stigao do Agenta"));
		}

		[Theory]
		[InlineData(false)]
		[InlineData(null)]
		public async Task Hardware_state_does_not_block_shutdown(bool? operational)
		{
			var k = await Kiosk.StartAsync();
			k.Status.SetOperational(operational);

			Assert.Equal(AgentResultCodes.Completed, k.Send(k.Request()).ResultCode);
		}

		// ─── Odbijanje zauzetog kioska ─────────────────────────────────────────────

		public static TheoryData<string> SessionFlows => new() {
			nameof(T.RecipientPickupRequested), nameof(T.SenderDepositRequested), nameof(T.ServiceBarcodeScanned),
			nameof(T.CourierBarcodeScanned), nameof(T.CourierBarcodeScannedRequiringManualLogin), "CourierLogin",
		};

		[Theory]
		[MemberData(nameof(SessionFlows))]
		public async Task Session_in_progress_is_refused_without_touching_the_flow(string flow)
		{
			var k = await Kiosk.StartAsync();
			var session = k.BeginSession();
			Assert.NotNull(session);
			await (flow switch {
				nameof(T.CourierBarcodeScanned) => k.M.FireAsync(k.M.CourierBarcodeScanned, "QR-1"),
				nameof(T.CourierBarcodeScannedRequiringManualLogin) => k.M.FireAsync(k.M.CourierBarcodeScannedRequiringManualLogin, "QR-1"),
				"CourierLogin" => Mk.CourierLogin(k.M),
				_ => k.M.FireAsync(Enum.Parse<T>(flow)),
			});
			var state = k.M.State;
			Assert.NotEqual(S.Start, state);

			var result = k.Send(k.Request());

			k.AssertRefused(result, AgentResultCodes.Busy, state, session);
			Assert.Contains(state.ToString(), result.Message);
		}

		[Fact]
		public async Task Owner_on_Start_is_refused_until_its_work_finishes()
		{
			// Prozor statusa uređaja: kiosk ostaje na Start-u, ali ima vlasnika (Service) — i dok se njegove provere završavaju.
			var k = await Kiosk.StartAsync();
			var window = k.C.TryBeginOperation(k.M, WorkflowStatusRules.ServiceRole, "prozor statusa uređaja")!;
			var checks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			Assert.NotNull(k.C.TryRun(window, () => checks.Task));

			var whileOpen = k.Send(k.Request());
			k.C.End(window);
			var whileEnding = k.Send(k.Request());

			k.AssertRefused(whileOpen, AgentResultCodes.Busy, S.Start, window);
			Assert.Contains(WorkflowStatusRules.ServiceRole, whileOpen.Message);
			k.AssertRefused(whileEnding, AgentResultCodes.Busy, S.Start, window);

			checks.SetResult();
			Assert.True(await k.C.WaitUntilFreeAsync(__wait));
			Assert.Equal(AgentResultCodes.Completed, k.Send(k.Request()).ResultCode);
		}

		[Fact]
		public async Task Machine_running_a_trigger_in_Start_is_refused()
		{
			var k = await Kiosk.StartAsync();
			var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			k.M.OnEntry(S.Start, async () => await gate.Task.ConfigureAwait(false));
			var reentry = k.M.FireAsync(T.Reset);

			k.AssertRefused(k.Send(k.Request()), AgentResultCodes.Busy, S.Start, owner: null);

			gate.SetResult();
			await reentry.WaitAsync(__wait);
		}

		[Fact]
		public async Task Local_start_and_shutdown_racing_get_exactly_one_owner()
		{
			int localWins = 0, agentWins = 0;
			for (int i = 0; i < 300; i++) {
				var k = await Kiosk.StartAsync();
				using var barrier = new Barrier(2);
				var local = Task.Run(() => { barrier.SignalAndWait(); return k.BeginSession(); });
				var agent = Task.Run(() => { barrier.SignalAndWait(); return k.Send(k.Request()); });
				var (session, result) = (await local, await agent);

				bool agentAccepted = result.ResultCode == AgentResultCodes.Completed;
				Assert.True((session != null) != agentAccepted, $"Iteracija {i}: lokalni {session}, Agent {result.ResultCode} — mora tačno jedan vlasnik.");
				if (agentAccepted) {
					agentWins++;
					Assert.Equal(WorkflowStatusRules.AgentRole, k.C.Owner!.Role);
				}
				else {
					localWins++;
					k.AssertRefused(result, AgentResultCodes.Busy, S.Start, session);
				}
			}
			Assert.Equal(300, localWins + agentWins);
		}

		// ─── Stanje kioska koje nije za gašenje ────────────────────────────────────

		[Theory]
		[InlineData(true, KioskLifecycles.Initializing, AgentResultCodes.NotReady)]
		[InlineData(false, KioskLifecycles.Ready, AgentResultCodes.NotReady)]     // Idle: prvi Start još nije prikazan
		[InlineData(true, KioskLifecycles.ShuttingDown, AgentResultCodes.ShuttingDown)]
		[InlineData(true, KioskLifecycles.RecoveryRequired, AgentResultCodes.RecoveryRequired)]
		public async Task Kiosk_that_is_not_ready_or_is_shutting_down_is_refused(bool started, string lifecycle, string expectedCode)
		{
			var k = await Kiosk.StartAsync(started, lifecycle);

			k.AssertRefused(k.Send(k.Request()), expectedCode, started ? S.Start : S.Idle, owner: null, lifecycle);
		}

		[Fact]
		public async Task Second_shutdown_after_acceptance_gets_ShuttingDown()
		{
			var k = await Kiosk.StartAsync();
			Assert.Equal(AgentResultCodes.Completed, k.Send(k.Request()).ResultCode);
			var owner = k.C.Owner;

			var second = k.Send(k.Request());

			Assert.Equal(AgentResultCodes.ShuttingDown, second.ResultCode);
			Assert.Null(second.AfterResponse);
			Assert.Same(owner, k.C.Owner);
		}

		// ─── Provera zahteva ───────────────────────────────────────────────────────

		public static TheoryData<string> InvalidRequests => new() {
			"bez ClientInstanceId", "bez IssuedAtUtc", "bez ExpiresAtUtc", "rok duži od 30 s", "rok nije posle izdavanja",
			"izdat u budućnosti", "bez payload-a", "payload nije objekat", "nepoznat Purpose", "Purpose malim slovima",
		};

		[Theory]
		[MemberData(nameof(InvalidRequests))]
		public async Task Invalid_request_is_refused(string kind)
		{
			var k = await Kiosk.StartAsync();
			var request = k.Request();
			var now = k.Time.Now;
			switch (kind) {
				case "bez ClientInstanceId": request.ClientInstanceId = null; break;
				case "bez IssuedAtUtc": request.IssuedAtUtc = null; break;
				case "bez ExpiresAtUtc": request.ExpiresAtUtc = null; break;
				case "rok duži od 30 s": request.ExpiresAtUtc = now.AddSeconds(AgentProtocol.MaxRequestTtlSeconds + 1); break;
				case "rok nije posle izdavanja": request.ExpiresAtUtc = now; break;
				case "izdat u budućnosti":
					request.IssuedAtUtc = now.AddSeconds(AgentProtocol.MaxClockSkewSeconds + 1);
					request.ExpiresAtUtc = request.IssuedAtUtc.Value.AddSeconds(10);
					break;
				case "bez payload-a": request.Payload = null; break;
				case "payload nije objekat": request.Payload = JsonSerializer.SerializeToElement(ShutdownPurposes.Restart); break;
				case "nepoznat Purpose": request = k.Request("Reboot"); break;
				case "Purpose malim slovima": request = k.Request("restart"); break;
			}

			k.AssertRefused(k.Send(request), AgentResultCodes.InvalidRequest, S.Start, owner: null);
		}

		[Fact]
		public async Task Expired_request_is_refused_but_clock_skew_is_tolerated()
		{
			var k = await Kiosk.StartAsync();
			var expired = k.Request();
			var withinSkew = k.Request();

			k.Time.Now = expired.ExpiresAtUtc!.Value.AddSeconds(AgentProtocol.MaxClockSkewSeconds + 1);
			k.AssertRefused(k.Send(expired), AgentResultCodes.Expired, S.Start, owner: null);

			k.Time.Now = withinSkew.ExpiresAtUtc!.Value.AddSeconds(AgentProtocol.MaxClockSkewSeconds - 1);
			Assert.Equal(AgentResultCodes.Completed, k.Send(withinSkew).ResultCode);
		}

		// ─── Ponovljen RequestId ───────────────────────────────────────────────────

		[Fact]
		public async Task Repeated_request_gets_the_same_outcome_and_shuts_down_once()
		{
			var k = await Kiosk.StartAsync();
			var request = k.Request();
			var first = k.Send(request);
			var owner = k.C.Owner;

			var repeated = k.Send(request);
			k.Time.Now = request.ExpiresAtUtc!.Value.AddMinutes(1);
			var repeatedAfterExpiry = k.Send(request);
			first.AfterResponse!(true);

			Assert.All(new[] { first, repeated, repeatedAfterExpiry }, r => Assert.Equal(AgentResultCodes.Completed, r.ResultCode));
			Assert.Null(repeated.AfterResponse);
			Assert.Null(repeatedAfterExpiry.AfterResponse);
			Assert.Same(owner, k.C.Owner);
			Assert.Equal(1, k.ShutdownRequests);
		}

		[Fact]
		public async Task Same_RequestId_with_other_content_is_a_conflict()
		{
			var k = await Kiosk.StartAsync();
			var request = k.Request(ShutdownPurposes.Restart);
			Assert.Equal(AgentResultCodes.Completed, k.Send(request).ResultCode);

			var conflict = k.Send(k.Request(ShutdownPurposes.Update, request.RequestId));

			Assert.Equal(AgentResultCodes.DuplicateConflict, conflict.ResultCode);
			Assert.Null(conflict.AfterResponse);
		}

		[Fact]
		public async Task Refused_request_leaves_no_trace_and_is_checked_again()
		{
			var k = await Kiosk.StartAsync();
			var window = k.C.TryBeginOperation(k.M, WorkflowStatusRules.ServiceRole, "prozor statusa uređaja")!;
			var request = k.Request();
			Assert.Equal(AgentResultCodes.Busy, k.Send(request).ResultCode);

			k.C.End(window);
			Assert.True(await k.C.WaitUntilFreeAsync(__wait));

			Assert.Equal(AgentResultCodes.Completed, k.Send(request).ResultCode);
		}

		// ─── Preko pravog pipe-a ───────────────────────────────────────────────────

		// v4: testovi preko named pipe-a (Hello, pretplata, zahtev prethodne instance i ponovljen zahtev na novoj vezi;
		// TestServer, TestAgentClient) dolaze sa transportom u F2.
	}
}
