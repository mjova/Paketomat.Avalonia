using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.AgentCommunication;
using TopPos.Kiosk.Workflow;
using TopPos.LockController;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// <see cref="RemoteServiceSession"/>: prijava servisera, otvaranje bez ponavljanja, ishodi po vratima, kraj i dnevnik.
	/// Kontroler je lažan, vreme ručno, a razmak magistrale se beleži umesto da se čeka.
	/// </summary>
	public class RemoteServiceSessionTests : IDisposable
	{
		private const string __code = "123456";
		private const string __sessionId = "s1";

		private sealed class ManualTime(DateTimeOffset now) : TimeProvider
		{
			public DateTimeOffset Now { get; set; } = now;

			public override DateTimeOffset GetUtcNow() => Now;
		}

		/// <summary>Lažni kontroler: ishod po vratima, stanje po modulu na kraju i opciona barijera pre otvaranja.</summary>
		internal class FakeController(int doorsPerModule) : ILockControllerClient
		{
			public ConcurrentQueue<int> Opened { get; } = new();
			public ConcurrentQueue<int> CheckedModules { get; } = new();
			public Dictionary<int, DoorOpenAttempt> Attempts { get; } = [];
			public HashSet<int> SilentModules { get; } = [];
			public HashSet<int> OpenDoorsAtEnd { get; } = [];
			public Func<int, Task>? BeforeOpen { get; set; }

			public int NumberOfDoorsPerController => doorsPerModule;

			public async ValueTask<DoorOpenAttempt> OpenDoorOnce(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
			{
				if (BeforeOpen != null) {
					await BeforeOpen(doorNumber);
				}
				Opened.Enqueue(doorNumber);
				return Attempts.TryGetValue(doorNumber, out var attempt) ? attempt : new DoorOpenAttempt(true, DoorStatusCode.Open);
			}

			public ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
			{
				CheckedModules.Enqueue(moduleNumber);
				if (SilentModules.Contains(moduleNumber)) {
					return ValueTask.FromResult<DoorStatusCode[]?>(null);
				}
				var first = (moduleNumber - 1) * doorsPerModule + 1;
				var statuses = Enumerable.Range(first, doorsPerModule)
										 .Select(d => OpenDoorsAtEnd.Contains(d) ? DoorStatusCode.Open : DoorStatusCode.Closed)
										 .ToArray();
				return ValueTask.FromResult<DoorStatusCode[]?>(statuses);
			}

			public ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, CancellationToken cancellationToken)
				=> CheckDoorMultiple(moduleNumber, null, cancellationToken);
			public ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
				=> CheckDoorMultiple(moduleNumber, receiveTimeout, cancellationToken);
			public ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
		}

		private static readonly IReadOnlyDictionary<string, Func<AgentOperationRecord, AgentOperationRecord>> __recoverers
			= new Dictionary<string, Func<AgentOperationRecord, AgentOperationRecord>> {
				[AgentCommands.BeginRemoteService] = RemoteServiceSession.RecoverSessionRecord,
			};

		private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("toppos-remote-service-");
		private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));
		private readonly KioskStatusSource _status;
		private readonly ConcurrentQueue<TimeSpan> _delays = new();
		private int _endedCalls;

		public RemoteServiceSessionTests()
		{
			_status = new KioskStatusSource("L1", logger: null, _time);
		}

		public void Dispose() => _dir.Delete(recursive: true);

		private string Folder => Path.Combine(_dir.FullName, "AgentOperations");

		private AgentOperationJournal OpenJournal(string? folder = null)
		{
			var journal = new AgentOperationJournal(folder ?? Folder, logger: null, _time);
			journal.Initialize(__recoverers);
			return journal;
		}

		private RemoteServiceSession Create(FakeController controller, AgentOperationJournal? journal = null, int lockerBoxCount = 16,
											TimeSpan? commandGap = null, Func<Task>? pauseDoorWatcher = null)
			=> new(new RemoteServiceSessionOptions {
						SessionId = __sessionId,
						PresenceCode = __code,
						IdleTimeout = TimeSpan.FromSeconds(600),
						LockerBoxCount = lockerBoxCount,
						CommandGap = commandGap ?? TimeSpan.Zero,
						UnavailableModulesAtStart = [],
					},
					controller, journal ?? OpenJournal(), _status, logger: null, _time,
					pauseDoorWatcher,
					delay: d => { _delays.Enqueue(d); return Task.CompletedTask; },
					onEnded: _ => Interlocked.Increment(ref _endedCalls));

		private async Task<RemoteServiceSession> Active(FakeController controller, AgentOperationJournal? journal = null, int lockerBoxCount = 16,
														TimeSpan? commandGap = null)
		{
			var session = Create(controller, journal, lockerBoxCount, commandGap);
			Assert.True(await session.StartAsync());
			Assert.Equal(PresenceCodeResult.Accepted, await session.SubmitPresenceCodeAsync(__code));
			return session;
		}

		private static async Task<OpenDoorsStart> OpenAndWait(RemoteServiceSession session, IReadOnlyList<int>? doors, bool all = false,
															  string? requestId = null)
		{
			var start = await session.StartOpenDoorsAsync(doors, all, DoorOpenSources.Portal, requestId);
			Assert.Equal(AgentResultCodes.InProgress, start.ResultCode);
			await start.Completion!;
			return start;
		}

		private static async Task WaitUntilAsync(Func<bool> condition)
		{
			var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
			while (!condition()) {
				Assert.True(DateTime.UtcNow < deadline, "uslov nije ispunjen u roku");
				await Task.Delay(10);
			}
		}

		private static Dictionary<int, string> Outcomes(RemoteServiceResult result)
			=> result.Openings.ToDictionary(o => o.DoorNumber, o => o.Outcome);

		private RemoteServiceResult ReadSessionFile()
		{
			var file = Path.Combine(Folder, AgentOperationJournal.FileNameOf(__sessionId));
			var record = JsonSerializer.Deserialize<AgentOperationRecord>(File.ReadAllBytes(file), AgentJson.Options)!;
			return record.Result!.Value.Deserialize<RemoteServiceResult>(AgentJson.Options)!;
		}

		// --- prijava ---

		[Fact]
		public async Task Start_sets_operation_in_status_and_writes_session_record()
		{
			var journal = OpenJournal();
			var session = Create(new FakeController(8), journal);

			Assert.True(await session.StartAsync());

			Assert.Equal(__sessionId, _status.Current.ActiveOperationId);
			Assert.Equal(RemoteServiceSession.Activity, _status.Current.Activity);
			Assert.True(journal.TryGet(__sessionId, out var record));
			Assert.Equal(AgentCommands.BeginRemoteService, record.Command);
			Assert.Equal(AgentResultCodes.InProgress, record.ResultCode);
			Assert.Equal(RemoteServicePhases.AwaitingTechnician, ReadSessionFile().Phase);
			Assert.DoesNotContain(__code, File.ReadAllText(Path.Combine(Folder, AgentOperationJournal.FileNameOf(__sessionId))));
		}

		[Fact]
		public async Task Correct_code_activates_session_and_raises_sequence()
		{
			var session = Create(new FakeController(8));
			await session.StartAsync();

			Assert.Equal(PresenceCodeResult.Accepted, await session.SubmitPresenceCodeAsync(" 123456 "));

			var result = session.GetResult();
			Assert.Equal(RemoteServicePhases.Active, result.Phase);
			Assert.Equal(_time.Now, result.AuthenticatedAtUtc);
			Assert.Equal(1, result.LastSequence);
			Assert.Equal(1, _status.Current.OperationSequence);
			Assert.Equal(_time.Now.AddSeconds(600), session.IdleDeadline);
		}

		[Fact]
		public async Task Third_wrong_code_ends_session_as_AuthFailed_without_controller_calls()
		{
			var controller = new FakeController(8);
			var journal = OpenJournal();
			var session = Create(controller, journal);
			await session.StartAsync();

			Assert.Equal(PresenceCodeResult.Rejected, await session.SubmitPresenceCodeAsync("1"));
			Assert.Equal(2, session.RemainingAuthAttempts);
			Assert.Equal(PresenceCodeResult.Rejected, await session.SubmitPresenceCodeAsync("2"));
			Assert.Equal(PresenceCodeResult.Failed, await session.SubmitPresenceCodeAsync("3"));
			Assert.Equal(PresenceCodeResult.NotAwaiting, await session.SubmitPresenceCodeAsync(__code));

			var result = session.GetResult();
			Assert.Equal(RemoteServicePhases.Ended, result.Phase);
			Assert.Equal(RemoteServiceEndModes.AuthFailed, result.EndMode);
			Assert.Equal(3, result.FailedAuthAttempts);
			Assert.Equal(4, result.LastSequence);    // tri pokušaja i kraj
			Assert.Empty(result.DoorsNotClosed!);
			Assert.Empty(controller.CheckedModules);
			Assert.Equal(1, _endedCalls);
			Assert.Null(_status.Current.ActiveOperationId);
			Assert.True(journal.TryGet(__sessionId, out var record));
			Assert.Equal(AgentResultCodes.Completed, record.ResultCode);
		}

		// v4: upis se zadržava zaključanim fajlom sesije (FileShare.None); na Linuxu zaključavanje ne zaustavlja zamenu fajla
		// (upis ide preko privremenog fajla), pa razmak koji test proverava ne nastaje.
		[WindowsFact("zaključan fajl zadržava upis samo na Windows-u")]
		public async Task Correct_code_while_third_wrong_code_is_being_saved_is_not_accepted()
		{
			var controller = new FakeController(8);
			var session = Create(controller);
			await session.StartAsync();
			await session.SubmitPresenceCodeAsync("1");
			await session.SubmitPresenceCodeAsync("2");

			// Zaključan fajl sesije drži upis trećeg pokušaja (zamena se ponavlja), pa kraj AuthFailed još nije počeo.
			Task<PresenceCodeResult> third, correct;
			using (new FileStream(Path.Combine(Folder, AgentOperationJournal.FileNameOf(__sessionId)), FileMode.Open, FileAccess.Read, FileShare.None)) {
				third = session.SubmitPresenceCodeAsync("3");
				await WaitUntilAsync(() => session.RemainingAuthAttempts == 0);
				correct = session.SubmitPresenceCodeAsync(__code);
				Assert.Equal(AgentResultCodes.TechnicianNotPresent, (await session.StartOpenDoorsAsync([1], false, DoorOpenSources.Portal)).ResultCode);
			}

			Assert.Equal(PresenceCodeResult.Failed, await third);
			Assert.Equal(PresenceCodeResult.NotAwaiting, await correct);
			var result = session.GetResult();
			Assert.Equal(RemoteServiceEndModes.AuthFailed, result.EndMode);
			Assert.Null(result.AuthenticatedAtUtc);
			Assert.Empty(controller.Opened);
		}

		[Fact]
		public async Task No_login_within_auth_timeout_ends_session_as_AuthTimeout()
		{
			var session = Create(new FakeController(8));
			await session.StartAsync();

			_time.Now += TimeSpan.FromSeconds(RemoteServiceLimits.AuthTimeoutSeconds - 1);
			await session.CheckDeadlinesAsync();
			Assert.Equal(RemoteServicePhases.AwaitingTechnician, session.Phase);

			_time.Now += TimeSpan.FromSeconds(1);
			await session.CheckDeadlinesAsync();
			Assert.Equal(RemoteServiceEndModes.AuthTimeout, session.GetResult().EndMode);
			Assert.Equal(1, _endedCalls);
		}

		[Fact]
		public async Task Correct_code_after_auth_timeout_is_not_accepted()
		{
			var session = Create(new FakeController(8));
			await session.StartAsync();
			_time.Now += TimeSpan.FromSeconds(RemoteServiceLimits.AuthTimeoutSeconds);

			Assert.Equal(PresenceCodeResult.NotAwaiting, await session.SubmitPresenceCodeAsync(__code));
			Assert.Equal(RemoteServiceEndModes.AuthTimeout, session.GetResult().EndMode);
		}

		[Fact]
		public async Task OpenDoors_before_login_is_TechnicianNotPresent()
		{
			var controller = new FakeController(8);
			var session = Create(controller);
			await session.StartAsync();

			var start = await session.StartOpenDoorsAsync([1], all: false, DoorOpenSources.Portal, "r1");

			Assert.Equal(AgentResultCodes.TechnicianNotPresent, start.ResultCode);
			Assert.Empty(controller.Opened);
		}

		[Fact]
		public async Task Portal_end_before_login_is_PortalCancelled_without_reading_doors()
		{
			var controller = new FakeController(8);
			var session = Create(controller);
			await session.StartAsync();

			var (code, result) = await session.EndFromPortalAsync();

			Assert.Equal(AgentResultCodes.Completed, code);
			Assert.Equal(RemoteServiceEndModes.PortalCancelled, result!.EndMode);
			Assert.Empty(controller.CheckedModules);
		}

		// --- otvaranje ---

		[Fact]
		public async Task Outcomes_map_from_controller_attempt_and_silent_module_is_skipped()
		{
			var controller = new FakeController(8);
			controller.Attempts[2] = new DoorOpenAttempt(true, DoorStatusCode.Closed);
			controller.Attempts[9] = new DoorOpenAttempt(true, null);      // modul 2 ne odgovara
			var session = await Active(controller);

			await OpenAndWait(session, [1, 2, 9, 10, 11, 3]);

			var result = session.GetResult();
			var outcomes = Outcomes(result);
			Assert.Equal(DoorOpenOutcomes.OpenConfirmed, outcomes[1]);
			Assert.Equal(DoorOpenOutcomes.OpenNotConfirmed, outcomes[2]);
			Assert.Equal(DoorOpenOutcomes.OutcomeUnknown, outcomes[9]);
			Assert.Equal(DoorOpenOutcomes.ModuleUnavailable, outcomes[10]);
			Assert.Equal(DoorOpenOutcomes.ModuleUnavailable, outcomes[11]);
			Assert.Equal(DoorOpenOutcomes.OpenConfirmed, outcomes[3]);
			Assert.Equal([1, 2, 9, 3], controller.Opened);                  // 10 i 11 se ne šalju
			Assert.Equal([2], result.ModuleErrors.Select(e => e.ModuleNumber));
			Assert.Equal([2], session.FailedModules);
			Assert.Equal([1, 3, 9], session.DoorsPossiblyOpen());
		}

		[Fact]
		public async Task Opening_that_was_not_sent_keeps_door_possibly_open_also_after_restart()
		{
			var controller = new FakeController(8);
			var session = await Active(controller);
			await OpenAndWait(session, [3, 10]);                            // oba otvorena

			controller.Attempts[3] = new DoorOpenAttempt(true, DoorStatusCode.Closed);
			controller.Attempts[9] = DoorOpenAttempt.NotSent;               // modul 2 ne odgovara
			await OpenAndWait(session, [3, 9, 10]);

			Assert.Equal(DoorOpenOutcomes.ModuleUnavailable, session.LastOutcomeByDoor[10]);   // preskočena, bez slanja
			Assert.Equal([3, 10, 3, 9], controller.Opened);
			// 3 je javio zatvoreno; 10 je ostao otvoren od prvog otvaranja, a 9 nije ni poslat.
			Assert.Equal([10], session.DoorsPossiblyOpen());

			var restarted = OpenJournal();                                   // „pad" usred sesije
			Assert.True(restarted.TryGet(__sessionId, out var record));
			Assert.Equal([10], record.Result!.Value.Deserialize<RemoteServiceResult>(AgentJson.Options)!.DoorsNotClosed!);
		}

		[Fact]
		public async Task Command_not_sent_is_ModuleUnavailable_and_skips_rest_of_module()
		{
			var controller = new FakeController(8);
			controller.Attempts[1] = DoorOpenAttempt.NotSent;
			var session = await Active(controller);

			await OpenAndWait(session, [1, 2, 9]);

			var outcomes = Outcomes(session.GetResult());
			Assert.Equal(DoorOpenOutcomes.ModuleUnavailable, outcomes[1]);
			Assert.Equal(DoorOpenOutcomes.ModuleUnavailable, outcomes[2]);
			Assert.Equal(DoorOpenOutcomes.OpenConfirmed, outcomes[9]);
			Assert.Equal([1, 9], controller.Opened);
		}

		[Fact]
		public async Task Sequence_grows_with_every_visible_entry_and_status_follows()
		{
			var controller = new FakeController(8);
			controller.Attempts[9] = new DoorOpenAttempt(true, null);
			var session = await Active(controller);

			await OpenAndWait(session, [1, 9, 10]);

			var result = session.GetResult();
			// prijava 1, vrata 1 → 2, vrata 9 → 3, greška modula 2 → 4, vrata 10 → 5
			Assert.Equal([2, 3, 5], result.Openings.Select(o => o.Sequence));
			Assert.Equal([4], result.ModuleErrors.Select(e => e.Sequence));
			Assert.Equal(5, result.LastSequence);
			Assert.Equal(5, _status.Current.OperationSequence);
		}

		[Fact]
		public async Task All_opens_every_door_including_service_door_once()
		{
			var controller = new FakeController(8);
			var session = await Active(controller, lockerBoxCount: 12);

			await OpenAndWait(session, null, all: true);

			Assert.Equal(Enumerable.Range(1, 12), controller.Opened);
		}

		[Fact]
		public async Task Repeated_doors_in_request_open_once_and_invalid_doors_are_rejected()
		{
			var controller = new FakeController(8);
			var session = await Active(controller);

			await OpenAndWait(session, [3, 3, 4]);
			Assert.Equal([3, 4], controller.Opened);

			Assert.Equal(AgentResultCodes.InvalidDoor, (await session.StartOpenDoorsAsync([17], false, DoorOpenSources.Portal)).ResultCode);
			Assert.Equal(AgentResultCodes.InvalidDoor, (await session.StartOpenDoorsAsync([0], false, DoorOpenSources.Portal)).ResultCode);
			Assert.Equal(AgentResultCodes.InvalidDoor, (await session.StartOpenDoorsAsync([], false, DoorOpenSources.Portal)).ResultCode);
		}

		[Fact]
		public async Task Only_one_opening_at_a_time_and_end_waits_for_it_with_Busy()
		{
			var controller = new FakeController(8);
			var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			controller.BeforeOpen = _ => { reached.TrySetResult(); return release.Task; };
			var session = await Active(controller);

			var first = await session.StartOpenDoorsAsync([1, 2], false, DoorOpenSources.Portal, "r1");
			await reached.Task;

			Assert.True(session.IsOpening);
			Assert.Equal(AgentResultCodes.Busy, (await session.StartOpenDoorsAsync([3], false, DoorOpenSources.Screen)).ResultCode);
			Assert.Equal(AgentResultCodes.Busy, (await session.EndFromPortalAsync()).ResultCode);
			Assert.Equal(AgentResultCodes.Busy, (await session.EndFromScreenAsync()).ResultCode);

			release.SetResult();
			await first.Completion!;
			Assert.False(session.IsOpening);
			Assert.Equal([1, 2], controller.Opened);
		}

		[Fact]
		public async Task Intent_is_on_disk_before_controller_call_but_not_visible_to_Agent()
		{
			var controller = new FakeController(8);
			var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			controller.BeforeOpen = _ => { reached.TrySetResult(); return release.Task; };
			var session = await Active(controller);

			var start = await session.StartOpenDoorsAsync([5], false, DoorOpenSources.Portal, "r1");
			await reached.Task;

			var onDisk = ReadSessionFile();
			var intent = Assert.Single(onDisk.Openings);
			Assert.Equal(5, intent.DoorNumber);
			Assert.Equal(DoorOpenOutcomes.OutcomeUnknown, intent.Outcome);
			Assert.True(intent.Sequence > onDisk.LastSequence);
			Assert.Empty(session.GetResult().Openings);
			Assert.Equal(1, _status.Current.OperationSequence);

			release.SetResult();
			await start.Completion!;
			Assert.Equal(DoorOpenOutcomes.OpenConfirmed, Assert.Single(ReadSessionFile().Openings).Outcome);
		}

		[Fact]
		public async Task Journal_that_cannot_write_means_NotStarted_and_no_controller_call()
		{
			var controller = new FakeController(8);
			var journal = new AgentOperationJournal("", logger: null, _time);
			var session = Create(controller, journal);
			Assert.False(await session.StartAsync());
			await session.SubmitPresenceCodeAsync(__code);

			await OpenAndWait(session, [1, 2]);

			var outcomes = Outcomes(session.GetResult());
			Assert.Equal(DoorOpenOutcomes.NotStarted, outcomes[1]);
			Assert.Equal(DoorOpenOutcomes.NotStarted, outcomes[2]);
			Assert.Empty(controller.Opened);
		}

		[Fact]
		public async Task Portal_opening_has_its_own_journal_record()
		{
			var journal = OpenJournal();
			var session = await Active(new FakeController(8), journal);

			await OpenAndWait(session, [1, 2], requestId: "r1");

			Assert.True(journal.TryGet("r1", out var record));
			Assert.Equal(AgentCommands.OpenDoors, record.Command);
			Assert.Equal(AgentResultCodes.Completed, record.ResultCode);
			var result = record.Result!.Value.Deserialize<OpenDoorsResult>(AgentJson.Options)!;
			Assert.True(result.Finished);
			Assert.Equal([1, 2], result.Openings.Select(o => o.DoorNumber));
			Assert.All(result.Openings, o => Assert.Equal("r1", o.RequestId));
			Assert.Equal([1, 2], session.GetOpenDoorsResult("r1")!.Openings.Select(o => o.DoorNumber));
			Assert.Null(session.GetOpenDoorsResult("r2"));
		}

		[Fact]
		public async Task Serial_gap_is_kept_before_every_command_and_NCU_has_none()
		{
			var serial = new FakeController(8);
			var session = await Active(serial, commandGap: TimeSpan.FromSeconds(2.5));
			await OpenAndWait(session, [1, 2, 3]);
			await session.EndFromScreenAsync();

			// tri otvaranja i dva modula na kraju; prva komanda čeka ceo razmak
			Assert.Equal(5, _delays.Count);
			Assert.All(_delays, d => Assert.InRange(d, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.5)));
			Assert.Equal(TimeSpan.FromSeconds(2.5), _delays.First());

			_delays.Clear();
			var ncu = new FakeController(16);
			var ncuSession = await Active(ncu, journal: OpenJournal(Path.Combine(_dir.FullName, "ncu")));
			await OpenAndWait(ncuSession, [1, 2, 3]);
			Assert.Empty(_delays);
		}

		[Fact]
		public async Task Door_watcher_is_paused_before_every_command()
		{
			var controller = new FakeController(8);
			int pauses = 0;
			var session = Create(controller, pauseDoorWatcher: () => { Interlocked.Increment(ref pauses); return Task.CompletedTask; });
			await session.StartAsync();
			await session.SubmitPresenceCodeAsync(__code);

			await OpenAndWait(session, [1, 2]);

			Assert.Equal(2, pauses);
		}

		// --- rok neaktivnosti ---

		[Fact]
		public async Task Idle_timeout_ends_session_and_touch_extends_it()
		{
			var session = await Active(new FakeController(8));

			_time.Now += TimeSpan.FromSeconds(500);
			session.Touch();
			_time.Now += TimeSpan.FromSeconds(500);
			await session.CheckDeadlinesAsync();
			Assert.Equal(RemoteServicePhases.Active, session.Phase);

			_time.Now += TimeSpan.FromSeconds(100);
			await session.CheckDeadlinesAsync();
			Assert.Equal(RemoteServiceEndModes.IdleTimeout, session.GetResult().EndMode);
		}

		[Fact]
		public async Task Idle_timeout_does_not_run_while_opening()
		{
			var controller = new FakeController(8);
			var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			controller.BeforeOpen = _ => { reached.TrySetResult(); return release.Task; };
			var session = await Active(controller);

			var start = await session.StartOpenDoorsAsync([1], false, DoorOpenSources.Screen);
			await reached.Task;
			_time.Now += TimeSpan.FromSeconds(700);
			await session.CheckDeadlinesAsync();
			Assert.Equal(RemoteServicePhases.Active, session.Phase);

			release.SetResult();
			await start.Completion!;
			Assert.Equal(_time.Now.AddSeconds(600), session.IdleDeadline);
		}

		/// <summary>
		/// Rok je prošao, a tajmer (otkucaj) to još nije video: dodir ne produžava sesiju (Codex v4 F1, 26.11.2026). Ranije je
		/// dodir pomerao rok za ceo interval, pa je istekla sesija nastavljala da radi.
		/// </summary>
		[Fact]
		public async Task Touch_after_the_idle_deadline_does_not_extend_the_session()
		{
			var session = await Active(new FakeController(8));
			var deadline = session.IdleDeadline;

			_time.Now = deadline + TimeSpan.FromSeconds(1);
			session.Touch();

			Assert.Equal(deadline, session.IdleDeadline);
			await session.CheckDeadlinesAsync();
			Assert.Equal(RemoteServiceEndModes.IdleTimeout, session.GetResult().EndMode);
		}

		/// <summary>Otvaranje (ekran ili portal) posle roka, pre otkucaja tajmera: vrata se ne otvaraju, sesija se završava odmah.</summary>
		[Theory]
		[InlineData(DoorOpenSources.Screen)]
		[InlineData(DoorOpenSources.Portal)]
		public async Task Opening_after_the_idle_deadline_is_refused_and_ends_the_session(string source)
		{
			var controller = new FakeController(8);
			var session = await Active(controller);
			_time.Now = session.IdleDeadline + TimeSpan.FromSeconds(1);

			var start = await session.StartOpenDoorsAsync([1], all: false, source, source == DoorOpenSources.Portal ? "r1" : null);

			Assert.Equal(AgentResultCodes.NoServiceSession, start.ResultCode);
			await WaitUntilAsync(() => Volatile.Read(ref _endedCalls) == 1);	// kraj upisan u dnevnik
			Assert.Equal(RemoteServiceEndModes.IdleTimeout, session.GetResult().EndMode);
			Assert.Empty(controller.Opened);
		}

		/// <summary>
		/// Kraj sa ekrana važi samo za spisak vrata koji je serviser video (Codex v4 F1, 26.11.2026): otvaranje iz portala
		/// završeno između prikaza spiska i potvrde vraća <see cref="RemoteServiceSession.EndPromptOutdated"/>, a sesija traje.
		/// </summary>
		[Fact]
		public async Task Screen_end_with_an_outdated_door_list_does_not_end_the_session()
		{
			var session = await Active(new FakeController(8));
			Assert.Empty(session.DoorsPossiblyOpen(out var shown));
			await OpenAndWait(session, [3]);

			var (code, result) = await session.EndFromScreenAsync(shown);

			Assert.Equal(RemoteServiceSession.EndPromptOutdated, code);
			Assert.Null(result);
			Assert.Equal(RemoteServicePhases.Active, session.Phase);
			Assert.Equal([3], session.DoorsPossiblyOpen(out var current));
			Assert.Equal(AgentResultCodes.Completed, (await session.EndFromScreenAsync(current)).ResultCode);
		}

		// --- kraj ---

		[Fact]
		public async Task Screen_end_reads_every_module_once_and_lists_doors_not_closed()
		{
			var controller = new FakeController(8);
			controller.OpenDoorsAtEnd.Add(3);
			controller.SilentModules.Add(2);
			var journal = OpenJournal();
			var session = await Active(controller, journal, lockerBoxCount: 20);
			await OpenAndWait(session, [3]);

			var (code, result) = await session.EndFromScreenAsync();

			Assert.Equal(AgentResultCodes.Completed, code);
			Assert.Equal(RemoteServiceEndModes.ScreenConfirmed, result!.EndMode);
			Assert.Equal(RemoteServicePhases.Ended, result.Phase);
			Assert.Equal([1, 2, 3], controller.CheckedModules);
			Assert.Equal([3, .. Enumerable.Range(9, 8)], result.DoorsNotClosed!);
			Assert.Equal([2], result.ModuleErrors.Select(e => e.ModuleNumber));
			Assert.Equal(_time.Now, result.EndedAtUtc);
			Assert.Equal(1, _endedCalls);
			Assert.Null(_status.Current.ActiveOperationId);
			Assert.True(journal.TryGet(__sessionId, out var record));
			Assert.Equal(AgentResultCodes.Completed, record.ResultCode);
			Assert.Equal(RemoteServiceEndModes.ScreenConfirmed, ReadSessionFile().EndMode);
		}

		[Fact]
		public async Task Portal_end_after_login_is_PortalConfirmed_and_nothing_is_sent_afterwards()
		{
			var controller = new FakeController(8);
			var session = await Active(controller);
			await OpenAndWait(session, [1]);

			var (_, result) = await session.EndFromPortalAsync();
			Assert.Equal(RemoteServiceEndModes.PortalConfirmed, result!.EndMode);

			Assert.Equal(AgentResultCodes.NoServiceSession, (await session.StartOpenDoorsAsync([2], false, DoorOpenSources.Portal)).ResultCode);
			await session.CheckDeadlinesAsync();
			Assert.Equal([1], controller.Opened);
			Assert.Equal(1, _endedCalls);
		}

		[Fact]
		public async Task Concurrent_ends_get_the_same_result_and_first_mode_wins()
		{
			var session = await Active(new FakeController(8));

			var ends = await Task.WhenAll(session.EndFromScreenAsync(), session.EndFromPortalAsync());

			Assert.All(ends, e => Assert.Equal(AgentResultCodes.Completed, e.ResultCode));
			Assert.Equal(ends[0].Result!.EndMode, ends[1].Result!.EndMode);
			Assert.Equal(1, _endedCalls);
		}

		// --- rezultat i oporavak ---

		[Fact]
		public void Page_returns_entries_after_sequence_in_order_and_flags_the_rest()
		{
			var full = new RemoteServiceResult {
				SessionId = __sessionId,
				Openings = Enumerable.Range(1, 250).Select(i => new DoorOpening { Sequence = i * 2, DoorNumber = i }).ToList(),
				ModuleErrors = [new ModuleError { Sequence = 3, ModuleNumber = 1 }],
				LastSequence = 500,
			};
			full.Openings.Add(new DoorOpening { Sequence = 501, DoorNumber = 7 });  // namera bez ishoda

			var first = RemoteServiceSession.Page(full, afterSequence: null);
			Assert.Equal(RemoteServiceLimits.MaxEntriesPerResult, first.Openings.Count + first.ModuleErrors.Count);
			Assert.True(first.HasMore);
			Assert.Equal(500, first.LastSequence);
			Assert.Single(first.ModuleErrors);

			int seen = first.Openings.Max(o => o.Sequence);
			var second = RemoteServiceSession.Page(full, seen);
			Assert.False(second.HasMore);
			Assert.Equal(51, second.Openings.Count);
			Assert.DoesNotContain(second.Openings, o => o.Sequence > 500);
		}

		[Fact]
		public async Task Restart_mid_opening_ends_session_as_ProcessRestart_and_intent_becomes_OutcomeUnknown()
		{
			var controller = new FakeController(8);
			var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			controller.BeforeOpen = door => {
				if (door == 2) {
					reached.TrySetResult();
					return release.Task;
				}
				return Task.CompletedTask;
			};
			var session = await Active(controller);
			var start = await session.StartOpenDoorsAsync([1, 2], false, DoorOpenSources.Portal, "r1");
			await reached.Task;

			// „Pad": nov dnevnik nad istim folderom, kao start programa.
			var restarted = OpenJournal();

			Assert.True(restarted.TryGet(__sessionId, out var record));
			Assert.Equal(AgentResultCodes.Completed, record.ResultCode);
			var result = record.Result!.Value.Deserialize<RemoteServiceResult>(AgentJson.Options)!;
			Assert.Equal(RemoteServicePhases.Ended, result.Phase);
			Assert.Equal(RemoteServiceEndModes.ProcessRestart, result.EndMode);
			Assert.Equal(DoorOpenOutcomes.OpenConfirmed, Outcomes(result)[1]);
			Assert.Equal(DoorOpenOutcomes.OutcomeUnknown, Outcomes(result)[2]);
			Assert.Equal([1, 2], result.DoorsNotClosed!);
			Assert.True(result.LastSequence > result.Openings.Max(o => o.Sequence));
			Assert.Equal(2, RemoteServiceSession.Page(result, null).Openings.Count);
			Assert.True(restarted.TryGet("r1", out var openRecord));
			Assert.Equal(AgentResultCodes.OutcomeUnknown, openRecord.ResultCode);

			release.SetResult();
			await start.Completion!;
		}
	}
}
