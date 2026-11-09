using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TopPos.Client.WPF;
using TopPos.Client.WPF.Workflow;
using TopPos.LockController;
using Xunit;
using S = TopPos.Client.WPF.Workflow.WorkflowState;
using T = TopPos.Client.WPF.Workflow.WorkflowTrigger;

namespace TopPos.Workflow.Tests
{
	/// <summary>
	/// Jedan vlasnik kioska (plan §6): rezervacija pre početka, zauzeto do kraja čišćenja i praćenih poslova, stari
	/// callback-ovi ne utiču na novu sesiju, operacija bez napuštanja Start-a (prozor statusa, Agent) preživljava Reset.
	/// Mašina je povezana sa koordinatorom kao u aplikaciji: prelazi idu u koordinator i izvor statusa, a Start OnEntry
	/// predaje čišćenje.
	/// </summary>
	public class KioskOperationCoordinatorTests
	{
		private static readonly TimeSpan __wait = TimeSpan.FromSeconds(5);

		private sealed class Kiosk
		{
			public required WorkflowMachine M { get; init; }
			public required KioskStatusSource Status { get; init; }
			public required KioskOperationCoordinator C { get; init; }
			public required ListLogger Log { get; init; }
			/// <summary>Čišćenje koje Start OnEntry predaje koordinatoru (u aplikaciji: zaustavljanje trackera).</summary>
			public Func<Task> Cleanup { get; set; } = () => Task.CompletedTask;

			public static async Task<Kiosk> StartAsync(TimeSpan? slowEndingWarning = null)
			{
				var log = new ListLogger();
				var status = new KioskStatusSource("112", logger: null);
				var coordinator = new KioskOperationCoordinator(status, log, slowEndingWarning);
				var m = Mk.Machine(new WorkflowContext());
				var kiosk = new Kiosk { M = m, Status = status, C = coordinator, Log = log };
				m.OnTransitioned((source, destination) => coordinator.OnWorkflowTransition(source, destination));
				m.OnTransitioned(destination => status.SetWorkflowState(destination));
				m.OnEntry(S.Start, () => coordinator.CompleteCleanup(kiosk.Cleanup));
				await m.FireAsync(T.Reset);
				return kiosk;
			}

			public KioskLease? BeginSession(string role = "Client")
				=> C.TryBegin(M, KioskLeaseMode.WorkflowSession, role, "test");

			/// <summary>Triger početka sesije kao u aplikaciji: uslov pri izvršavanju, odbijanje oslobađa samo svoju rezervaciju.</summary>
			public Task StartSessionAsync(KioskLease lease, WorkflowTrigger trigger = T.RecipientPickupRequested)
				=> M.FireGuardedAsync(() => C.IsReserved(lease) && M.State == S.Start, trigger, () => C.ReleaseIfReserved(lease));

			public async Task AssertFreeAsync()
			{
				Assert.True(await C.WaitUntilFreeAsync(__wait), "kiosk nije oslobođen");
				Assert.Null(C.Owner);
				Assert.False(Status.Current.Busy);
				Assert.Equal("", Status.Current.Role);
			}
		}

		[Fact]
		public async Task Session_is_busy_from_reservation_until_cleanup_finishes()
		{
			var k = await Kiosk.StartAsync();
			var lease = k.BeginSession()!;
			Assert.NotNull(lease);
			Assert.Equal(KioskLeasePhase.Reserved, k.C.PhaseOf(lease));
			Assert.True(k.Status.Current.Busy);                 // već na Start-u, pre trigera
			Assert.Equal("Client", k.Status.Current.Role);
			Assert.False(k.Status.Current.CanAcceptOpenDoor);

			await k.StartSessionAsync(lease);
			Assert.Equal(S.RecipientCodeInput, k.M.State);
			Assert.Equal(KioskLeasePhase.Active, k.C.PhaseOf(lease));

			var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			k.Cleanup = () => cleanup.Task;
			await k.M.FireAsync(T.Reset);
			Assert.Equal(S.Start, k.M.State);
			Assert.Equal(KioskLeasePhase.Ending, k.C.PhaseOf(lease));
			Assert.True(k.Status.Current.Busy);                 // Start, ali čišćenje nije gotovo
			Assert.Null(k.BeginSession());

			cleanup.SetResult();
			await k.AssertFreeAsync();
			Assert.Equal(KioskLeasePhase.Released, k.C.PhaseOf(lease));
			Assert.NotNull(k.BeginSession());
		}

		[Fact]
		public async Task Reset_reentry_in_Start_keeps_reserved_session()
		{
			var k = await Kiosk.StartAsync();
			var lease = k.BeginSession()!;

			await k.M.FireAsync(T.Reset);                       // npr. tajmer ili povratak operativnosti tokom odziva dugmeta
			Assert.Same(lease, k.C.Owner);
			Assert.Equal(KioskLeasePhase.Reserved, k.C.PhaseOf(lease));

			await k.StartSessionAsync(lease);
			Assert.Equal(KioskLeasePhase.Active, k.C.PhaseOf(lease));
		}

		[Fact]
		public async Task Rejected_start_releases_only_its_own_reservation()
		{
			var k = await Kiosk.StartAsync();
			var first = k.BeginSession()!;
			await k.M.FireGuardedAsync(() => false, T.RecipientPickupRequested, () => k.C.ReleaseIfReserved(first));
			await k.AssertFreeAsync();

			var second = k.BeginSession()!;
			Assert.False(k.C.ReleaseIfReserved(first));         // zakasnelo odbijanje stare rezervacije
			Assert.Same(second, k.C.Owner);
			await k.StartSessionAsync(second);
			Assert.False(k.C.ReleaseIfReserved(second));        // već aktivna
			Assert.Same(second, k.C.Owner);
		}

		[Fact]
		public async Task Tracked_work_delays_release_and_is_refused_once_ending()
		{
			var k = await Kiosk.StartAsync();
			var lease = k.BeginSession()!;
			await k.StartSessionAsync(lease);
			var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			Assert.NotNull(k.C.TryRun(lease, () => work.Task));

			await k.M.FireAsync(T.Reset);
			Assert.Equal(KioskLeasePhase.Ending, k.C.PhaseOf(lease));
			var started = false;
			Assert.Null(k.C.TryRun(lease, () => { started = true; return Task.CompletedTask; }));
			Assert.False(started);
			await Task.Delay(50);
			Assert.Same(lease, k.C.Owner);                      // čišćenje je gotovo, posao nije

			work.SetResult();
			await k.AssertFreeAsync();
		}

		[Fact]
		public async Task Work_held_from_transition_to_Start_delays_release_and_is_refused_once_release_is_scheduled()
		{
			var k = await Kiosk.StartAsync();
			var lease = k.BeginSession()!;
			await k.StartSessionAsync(lease);
			var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			k.Cleanup = () => cleanup.Task;
			var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			bool? held = null;
			// Kao upis ishoda operacije Agenta: handler prelaza registrovan posle koordinatora, pa je sesija već u Ending.
			k.M.OnTransitioned((source, destination) => {
				if (destination == S.Start && source != S.Start) {
					held = k.C.TryHoldRelease(lease, work.Task);
				}
			});

			await k.M.FireAsync(T.Reset);
			Assert.True(held);
			Assert.Equal(KioskLeasePhase.Ending, k.C.PhaseOf(lease));
			Assert.False(k.C.TryHoldRelease(lease, Task.CompletedTask));	// čišćenje je predato, oslobađanje zakazano

			cleanup.SetResult();
			await Task.Delay(50);
			Assert.Same(lease, k.C.Owner);                      // čišćenje je gotovo, zadržani posao nije
			Assert.Null(k.BeginSession());

			work.SetResult();
			await k.AssertFreeAsync();
			Assert.False(k.C.TryHoldRelease(lease, Task.CompletedTask));
		}

		[Fact]
		public async Task Standalone_operation_survives_Reset_and_blocks_local_start()
		{
			var k = await Kiosk.StartAsync();
			var operation = k.C.TryBegin(k.M, KioskLeaseMode.StandaloneOperation, WorkflowStatusRules.ServiceRole, "prozor statusa")!;
			Assert.NotNull(operation);
			Assert.True(k.C.TryActivate(operation));
			Assert.False(k.C.TryActivate(operation));

			await k.M.FireAsync(T.Reset);                       // Home, tajmer, povratak operativnosti
			Assert.Same(operation, k.C.Owner);
			Assert.Equal(KioskLeasePhase.Active, k.C.PhaseOf(operation));
			Assert.Equal(S.Start, k.Status.Current.WorkflowState);
			Assert.True(k.Status.Current.Busy);
			Assert.Equal("Service", k.Status.Current.Role);
			Assert.Null(k.BeginSession());

			k.C.End(operation);
			k.C.End(operation);                                 // idempotentno
			await k.AssertFreeAsync();
		}

		[Fact]
		public async Task Standalone_operation_stays_busy_after_End_until_its_work_finishes()
		{
			// Prozor statusa zatvoren dok provera skenera još radi: kiosk je slobodan tek kad se provera završi.
			var k = await Kiosk.StartAsync();
			var operation = k.C.TryBegin(k.M, KioskLeaseMode.StandaloneOperation, WorkflowStatusRules.ServiceRole, "prozor statusa")!;
			Assert.True(k.C.TryActivate(operation));
			var checks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			Assert.NotNull(k.C.TryRun(operation, () => checks.Task));

			k.C.End(operation);
			Assert.Equal(KioskLeasePhase.Ending, k.C.PhaseOf(operation));
			await Task.Delay(50);
			Assert.Same(operation, k.C.Owner);
			Assert.True(k.Status.Current.Busy);
			Assert.Equal("Service", k.Status.Current.Role);
			Assert.Null(k.BeginSession());
			Assert.Null(k.C.TryRun(operation, () => Task.CompletedTask));

			checks.SetResult();
			await k.AssertFreeAsync();
			Assert.Equal(KioskLeasePhase.Released, k.C.PhaseOf(operation));
		}

		[Fact]
		public async Task ActiveSession_is_only_a_running_local_session()
		{
			var k = await Kiosk.StartAsync();
			var operation = k.C.TryBegin(k.M, KioskLeaseMode.StandaloneOperation, WorkflowStatusRules.ServiceRole, "prozor statusa")!;
			Assert.True(k.C.TryActivate(operation));
			Assert.Null(k.C.ActiveSession);                     // operacija koja ne napušta Start nije sesija toka
			k.C.End(operation);
			await k.AssertFreeAsync();

			var session = k.BeginSession()!;
			Assert.Null(k.C.ActiveSession);                     // Reserved: OnEntry toka još nije počeo
			await k.StartSessionAsync(session);
			Assert.Same(session, k.C.ActiveSession);

			var cleanup = new TaskCompletionSource();
			k.Cleanup = () => cleanup.Task;
			await k.M.FireAsync(T.Reset);
			Assert.Equal(KioskLeasePhase.Ending, k.C.PhaseOf(session));
			Assert.Null(k.C.ActiveSession);                     // callback koji tek sada pokreće posao ne dobija sesiju
			cleanup.SetResult();
			await k.AssertFreeAsync();
		}

		[Fact]
		public async Task Stale_callback_of_old_session_is_ignored_in_new_session_in_same_state()
		{
			var k = await Kiosk.StartAsync();
			var old = k.BeginSession()!;
			await k.StartSessionAsync(old);
			await k.M.FireAsync(T.Reset);
			await k.AssertFreeAsync();

			var current = k.BeginSession()!;
			await k.StartSessionAsync(current);
			Assert.Equal(S.RecipientCodeInput, k.M.State);      // isto stanje kao stara sesija

			var rejected = 0;
			await k.M.FireGuardedAsync(() => k.C.IsActive(old), T.Reset, () => rejected++);   // npr. zakasneli istek tajmera
			Assert.Equal(1, rejected);
			Assert.Equal(S.RecipientCodeInput, k.M.State);
			Assert.Equal(KioskLeasePhase.Active, k.C.PhaseOf(current));
		}

		[Fact]
		public async Task Tracker_callback_that_fires_Reset_does_not_wait_for_itself()
		{
			// Vrata zatvorena → callback praćenja šalje Reset → Start OnEntry predaje zaustavljanje istog praćenja kao
			// čišćenje. Čekanje u OnEntry-ju bi čekalo samo sebe; koordinator čeka van reda mašine.
			var k = await Kiosk.StartAsync();
			var lease = k.BeginSession()!;
			await k.StartSessionAsync(lease);
			var tracker = new DoorStatusTracker(new DoorStatusTrackerTests.FakeDoorClient { Status = DoorStatusCode.Closed },
												TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100), null);
			k.Cleanup = tracker.StopAsync;

			var tracking = tracker.Start(3, DoorStatusCode.Closed, async _ => await k.M.FireAsync(T.Reset).ConfigureAwait(false));

			await tracking.WaitAsync(__wait);
			await k.AssertFreeAsync();
			Assert.Equal(S.Start, k.M.State);
		}

		[Fact]
		public async Task Closed_coordinator_refuses_new_operations()
		{
			var k = await Kiosk.StartAsync();
			k.C.Close();
			Assert.Null(k.BeginSession());
			Assert.Null(k.C.TryBegin(k.M, KioskLeaseMode.StandaloneOperation, WorkflowStatusRules.AgentRole, "agent"));
		}

		[Fact]
		public async Task Leaving_Start_without_reservation_still_makes_kiosk_busy()
		{
			var k = await Kiosk.StartAsync();
			await k.M.FireAsync(T.RecipientPickupRequested);    // ulaz koji nije prošao kroz TryBegin

			var owner = k.C.Owner;
			Assert.NotNull(owner);
			Assert.Equal(KioskLeasePhase.Active, k.C.PhaseOf(owner));
			Assert.Contains(k.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("bez rezervacije"));

			await k.M.FireAsync(T.Reset);
			await k.AssertFreeAsync();
		}

		[Fact]
		public async Task Slow_ending_is_logged_but_kiosk_stays_busy()
		{
			var k = await Kiosk.StartAsync(slowEndingWarning: TimeSpan.FromMilliseconds(100));
			var lease = k.BeginSession()!;
			await k.StartSessionAsync(lease);
			var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			k.Cleanup = () => cleanup.Task;
			await k.M.FireAsync(T.Reset);

			var deadline = DateTime.UtcNow + __wait;
			while (!k.Log.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("duže od")) && DateTime.UtcNow < deadline)
				await Task.Delay(20);
			Assert.Contains(k.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("duže od"));
			Assert.Same(lease, k.C.Owner);                      // nema oslobađanja po isteku roka

			cleanup.SetResult();
			await k.AssertFreeAsync();
		}

		[Fact]
		public async Task Faulted_cleanup_still_releases_and_is_logged()
		{
			var k = await Kiosk.StartAsync();
			var lease = k.BeginSession()!;
			await k.StartSessionAsync(lease);
			k.Cleanup = () => Task.FromException(new InvalidOperationException("tracker"));

			await k.M.FireAsync(T.Reset);

			await k.AssertFreeAsync();
			Assert.Contains(k.Log.Entries, e => e.Level == LogLevel.Error);
		}

		[Fact]
		public async Task Cleanup_throwing_before_hand_over_still_releases()
		{
			var k = await Kiosk.StartAsync();
			var lease = k.BeginSession()!;
			await k.StartSessionAsync(lease);
			k.Cleanup = () => throw new InvalidOperationException("GoToStartPage");

			await k.M.FireAsync(T.Reset);

			await k.AssertFreeAsync();
			Assert.Contains(k.Log.Entries, e => e.Level == LogLevel.Error);
		}

		[Fact]
		public async Task Begin_is_refused_while_machine_runs_a_trigger_in_Start()
		{
			// TryBegin mora ići kroz red mašine: Start prikazan, ali OnEntry (npr. Reset reentry) još radi → odbijeno.
			var k = await Kiosk.StartAsync();
			var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			k.M.OnEntry(S.Start, async () => await gate.Task.ConfigureAwait(false));
			var reentry = k.M.FireAsync(T.Reset);
			Assert.Equal(S.Start, k.M.State);

			Assert.Null(k.BeginSession());
			Assert.Null(k.C.TryBegin(k.M, KioskLeaseMode.StandaloneOperation, WorkflowStatusRules.AgentRole, "agent"));
			Assert.Null(k.C.Owner);

			gate.SetResult();
			await reentry.WaitAsync(__wait);
			Assert.NotNull(k.BeginSession());
		}

		[Fact]
		public async Task Local_start_and_agent_racing_get_exactly_one_owner()
		{
			var k = await Kiosk.StartAsync();
			int localWins = 0, agentWins = 0;
			for (int i = 0; i < 5000; i++) {
				using var barrier = new Barrier(2);
				var local = Task.Run(() => { barrier.SignalAndWait(); return k.BeginSession(); });
				var agent = Task.Run(() => {
					barrier.SignalAndWait();
					return k.C.TryBegin(k.M, KioskLeaseMode.StandaloneOperation, WorkflowStatusRules.AgentRole, "agent");
				});
				var (l, a) = (await local, await agent);

				Assert.True((l == null) != (a == null), $"Iteracija {i}: lokalni {l}, Agent {a} — mora tačno jedan vlasnik.");
				if (l != null) {
					localWins++;
					Assert.True(k.C.ReleaseIfReserved(l));
				}
				else {
					agentWins++;
					Assert.True(k.C.TryActivate(a!));
					k.C.End(a!);
				}
				Assert.Null(k.C.Owner);
			}
			Assert.Equal(5000, localWins + agentWins);
		}

		[Fact]
		public async Task Status_lock_shows_owner_role_on_Start()
		{
			var dir = Directory.CreateTempSubdirectory("toppos-status-");
			try {
				var semaphore = new StatusSemaphore(dir.FullName, logger: null);
				semaphore.InitializeSession();
				var k = await Kiosk.StartAsync();
				k.Status.Subscribe(s => semaphore.Update(s.Busy, s.WorkflowState, s.Operational, s.Role));

				var operation = k.C.TryBegin(k.M, KioskLeaseMode.StandaloneOperation, WorkflowStatusRules.ServiceRole, "prozor statusa")!;
				Assert.True(semaphore.WaitForPendingWrites(__wait));

				using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "status.lock")));
				var root = doc.RootElement;
				Assert.True(root.GetProperty("Busy").GetBoolean());
				Assert.Equal("Service", root.GetProperty("Role").GetString());
				Assert.Equal("Start", root.GetProperty("State").GetString());
				k.C.End(operation);
				Assert.True(semaphore.WaitForPendingWrites(__wait));    // pre brisanja direktorijuma
			}
			finally {
				dir.Delete(recursive: true);
			}
		}

		internal sealed class ListLogger : ILogger
		{
			private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

			public IEnumerable<(LogLevel Level, string Message)> Entries => _entries;

			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
			public bool IsEnabled(LogLevel logLevel) => true;
			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
				=> _entries.Enqueue((logLevel, formatter(state, exception)));
		}
	}
}
