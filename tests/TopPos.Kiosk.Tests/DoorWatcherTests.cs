using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TopPos.LockController;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// DoorWatcher sa lažnim kontrolerom, uključujući broj vrata koji nije deljiv sa brojem vrata po modulu (npr. 35/8).
	/// Kontroler (kao pravi) uvek vraća status za sve pozicije modula, i za nepovezane (zavisno od modela brave, otvorena ili zatvorena).
	/// Deljivi slučajevi (32/8, 48/16, 48/24) su invarijanta: ponašanje mora ostati isto kao pre podrške za delimičan modul.
	/// </summary>
	public class DoorWatcherTests
	{
		[Theory]
		[InlineData(35, 8, 5)]
		[InlineData(32, 8, 4)]
		[InlineData(5, 8, 1)]
		[InlineData(40, 16, 3)]
		[InlineData(50, 24, 3)]
		[InlineData(24, 24, 1)]
		public void GetModuleCount_rounds_up(int installedDoors, int doorsPerModule, int expected)
			=> Assert.Equal(expected, DoorWatcher.GetModuleCount(installedDoors, doorsPerModule));

		[Theory]
		[InlineData(5, 35, 8, 33, 35)]
		[InlineData(4, 32, 8, 25, 32)]
		[InlineData(1, 5, 8, 1, 5)]
		[InlineData(3, 40, 16, 33, 40)]
		[InlineData(3, 50, 24, 49, 50)]
		[InlineData(1, 24, 24, 1, 24)]
		public void GetModuleDoorRange_stops_at_installed_doors(int moduleNo, int installedDoors, int doorsPerModule, int expectedFirst, int expectedLast)
			=> Assert.Equal((expectedFirst, expectedLast), DoorWatcher.GetModuleDoorRange(moduleNo, installedDoors, doorsPerModule));

		[Theory]
		[InlineData(32, 8)]
		[InlineData(48, 16)]
		[InlineData(48, 24)]
		[InlineData(35, 8)]
		[InlineData(5, 8)]
		[InlineData(40, 16)]
		[InlineData(50, 24)]
		[InlineData(127, 8)]     // najviše za Serial / SerialDailyExpress (LockControllerTypes.GetMaxDoorCount)
		[InlineData(256, 16)]    // najviše za NCU16L
		[InlineData(240, 24)]    // najviše za NCU24L
		public async Task Full_scan_reports_exactly_installed_doors(int installedDoors, int doorsPerModule)
		{
			var client = new FakeLockControllerClient(doorsPerModule, installedDoors);
			await using var run = await WatcherRun.StartAsync(client, installedDoors);

			int expectedModules = (installedDoors + doorsPerModule - 1) / doorsPerModule;   // namerno bez DoorWatcher pomoćnih metoda
			Assert.Equal(Enumerable.Range(1, expectedModules), client.RequestedModules.Distinct().Order());
			Assert.Equal(Enumerable.Range(1, installedDoors), run.Watcher.DoorStatuses!.Select(d => d.DoorNumber));
			Assert.All(run.Watcher.DoorStatuses!, d => Assert.True(d.Status?.IsClosed));
			Assert.True(run.Watcher.IsLockerOperational());
			Assert.DoesNotContain(run.ReportedDoors, door => door > installedDoors);
		}

		[Theory]
		[InlineData(35, 8)]
		[InlineData(40, 16)]
		[InlineData(50, 24)]
		public async Task Closed_unconnected_positions_do_not_make_locker_operational(int installedDoors, int doorsPerModule)
		{
			// sva postojeća vrata otvorena, nepovezane pozicije javljaju zatvoreno (model brave) - paketomat NIJE operativan
			var client = new FakeLockControllerClient(doorsPerModule, installedDoors) {
				InstalledDoorStatus = DoorStatusCode.Open,
				UnconnectedPositionStatus = DoorStatusCode.Closed,
			};
			await using var run = await WatcherRun.StartAsync(client, installedDoors);

			Assert.Equal(Enumerable.Range(1, installedDoors), run.Watcher.DoorStatuses!.Select(d => d.DoorNumber));
			Assert.All(run.Watcher.DoorStatuses!, d => Assert.True(d.Status?.IsOpen));
			Assert.False(run.Watcher.IsLockerOperational());
			Assert.DoesNotContain(run.ReportedDoors, door => door > installedDoors);
		}

		[Theory]
		[InlineData(32, 8, 4)]
		[InlineData(35, 8, 5)]
		public async Task Unavailable_last_module_reports_unknown_only_for_installed_doors(int installedDoors, int doorsPerModule, int unavailableModule)
		{
			var client = new FakeLockControllerClient(doorsPerModule, installedDoors, unavailableModule);
			await using var run = await WatcherRun.StartAsync(client, installedDoors);

			int firstUnknownDoor = (unavailableModule - 1) * doorsPerModule + 1;
			var statuses = run.Watcher.DoorStatuses!;
			Assert.Equal(Enumerable.Range(1, installedDoors), statuses.Select(d => d.DoorNumber));
			Assert.All(statuses.Where(d => d.DoorNumber >= firstUnknownDoor), d => Assert.Null(d.Status));
			Assert.All(statuses.Where(d => d.DoorNumber < firstUnknownDoor), d => Assert.True(d.Status?.IsClosed));
			Assert.True(run.Watcher.IsLockerOperational());
			Assert.DoesNotContain(run.ReportedDoors, door => door > installedDoors);
			Assert.Equal([unavailableModule], run.Watcher.GetUnavailableModules());
		}

		[Fact]
		public async Task Partial_module_log_shows_installed_range_and_marks_positions_without_door()
		{
			var logger = new ListLogger();
			var client = new FakeLockControllerClient(8, 35);
			await using var run = await WatcherRun.StartAsync(client, 35, logger);

			Assert.Contains(logger.Messages, m => m.Contains("provera statusa modula [5], pozicije od 33 do 35"));
			Assert.Contains(logger.Messages, m => m.Contains("status modula [5]: |X|X|X|-|-|-|-|-|"));
			Assert.Contains(logger.Messages, m => m.Contains("Module: 5: XXX "));   // DebugLogStatuses ispisuje i poslednji, delimičan red
		}

		[Fact]
		public async Task PauseAsync_waits_for_controller_call_in_progress_and_stops_polling()
		{
			var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var client = new FakeLockControllerClient(8, 16) {
				// kao NCU: semafor kontrolera se čeka bez tokena, pa otkazivanje ne prekida poziv koji je već krenuo
				BeforeCheck = async (_, _) => { entered.TrySetResult(); await release.Task; },
			};
			await using var run = WatcherRun.Start(client, 16);
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

			var paused = run.Watcher.PauseAsync();
			await Task.Delay(100);
			Assert.False(paused.IsCompleted);   // kontroler je i dalje zauzet proverom

			release.TrySetResult();
			await paused.WaitAsync(TimeSpan.FromSeconds(5));

			int requests = client.RequestedModules.Count;
			await Task.Delay(300);              // 3 perioda glavne petlje
			Assert.Equal(requests, client.RequestedModules.Count);
			Assert.True(run.Watcher.PauseAsync().IsCompleted);  // ponovljena pauza: nema poziva u toku
		}

		[Fact]
		public async Task PauseAsync_from_status_handler_does_not_wait_for_itself()
		{
			// MainLoop → StatusUpdateHandler → OnLockerStatusChanged → Reset → … → otvaranje vrata → PauseAsync
			var pausedFromHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var client = new FakeLockControllerClient(8, 16);
			DoorWatcher? watcher = null;
			using var cts = new CancellationTokenSource();
			watcher = new DoorWatcher(client, 16, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(100),
				async (_, _, _) => {
					await watcher!.PauseAsync().WaitAsync(TimeSpan.FromSeconds(2));
					pausedFromHandler.TrySetResult();
				},
				startImmediately: true, logger: null, cts.Token);

			await pausedFromHandler.Task.WaitAsync(TimeSpan.FromSeconds(5));
			cts.Cancel();
		}

		[Fact]
		public async Task Resume_during_pause_cancellation_keeps_polling()
		{
			// Deterministički raspored stare trke: Resume stiže dok Pause otkazuje (registracija na tokenu se izvršava
			// sinhrono u Cancel()). Ranije je Pause zatim obrisao nov CTS → _running=true bez CTS-a → provera stoji zauvek.
			DoorWatcher? watcher = null;
			var registered = 0;
			var client = new FakeLockControllerClient(8, 16) {
				BeforeCheck = async (_, ct) => {
					if (Interlocked.Exchange(ref registered, 1) == 0) {
						ct.Register(() => watcher!.Resume());
						await Task.Delay(Timeout.Infinite, ct);
					}
				},
			};
			await using var run = WatcherRun.Start(client, 16);
			watcher = run.Watcher;
			var deadline = DateTime.UtcNow.AddSeconds(5);
			while (Volatile.Read(ref registered) == 0 && DateTime.UtcNow < deadline)
				await Task.Delay(10);

			run.Watcher.Pause();

			int requests = client.RequestedModules.Count;
			while (client.RequestedModules.Count < requests + 4 && DateTime.UtcNow < deadline)
				await Task.Delay(20);
			Assert.True(client.RequestedModules.Count >= requests + 4, "posle Resume tokom pauze provera ne radi");
		}

		[Fact]
		public async Task Pause_and_resume_from_many_threads_keep_polling_consistent()
		{
			var client = new FakeLockControllerClient(8, 16) {
				BeforeCheck = (_, ct) => Task.Delay(1, ct),
			};
			await using var run = WatcherRun.Start(client, 16);

			// ranije: Pause/Resume/Pause iz dve niti ostavlja _running=true i CTS null → NRE u svakoj iteraciji
			await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() => {
				for (int i = 0; i < 2000; i++) {
					if ((i + t) % 2 == 0)
						run.Watcher.Pause();
					else
						run.Watcher.Resume();
				}
			})));

			run.Watcher.Resume();
			int requests = client.RequestedModules.Count;
			var deadline = DateTime.UtcNow.AddSeconds(5);
			while (client.RequestedModules.Count < requests + 4 && DateTime.UtcNow < deadline)
				await Task.Delay(20);
			Assert.True(client.RequestedModules.Count >= requests + 4, "posle Resume provera ne radi");

			await run.Watcher.PauseAsync().WaitAsync(TimeSpan.FromSeconds(5));
		}

		/// <summary>Pokrenut DoorWatcher; čeka da prvi put prijavi stanje paketomata (IsLockerOperational != null = sva vrata viđena).</summary>
		private sealed class WatcherRun : IAsyncDisposable
		{
			private readonly CancellationTokenSource _cts = new();
			private readonly TaskCompletionSource _lockerStateKnown = new(TaskCreationOptions.RunContinuationsAsynchronously);

			public DoorWatcher Watcher { get; private set; } = null!;

			/// <summary>Sva vrata koja je DoorWatcher prosledio dalje (ka LockerStatusUpdater-u, tj. serveru).</summary>
			public ConcurrentBag<int> ReportedDoors { get; } = new();

			/// <summary>Pokrenut DoorWatcher, bez čekanja prvog izveštaja.</summary>
			public static WatcherRun Start(ILockControllerClient client, int installedDoors, ILogger? logger = null)
			{
				var run = new WatcherRun();
				run.Watcher = new DoorWatcher(client,
					installedDoors,
					TimeSpan.FromMilliseconds(100),     // mainLoopTimeSpan
					TimeSpan.FromMilliseconds(1),       // getDoorStatusTimeSpan (pauza između modula)
					TimeSpan.FromMilliseconds(100),     // readWriteTimeOut
					run.OnStatusUpdate,
					startImmediately: true,
					logger,
					run._cts.Token);
				return run;
			}

			public static async Task<WatcherRun> StartAsync(ILockControllerClient client, int installedDoors, ILogger? logger = null)
			{
				var run = Start(client, installedDoors, logger);

				var finished = await Task.WhenAny(run._lockerStateKnown.Task, Task.Delay(TimeSpan.FromSeconds(5)));
				if (finished != run._lockerStateKnown.Task) {
					run._cts.Cancel();
					Assert.Fail("DoorWatcher za 5 s nije prijavio stanje paketomata (IsLockerOperational je ostao null - nisu viđena sva vrata).");
				}
				return run;
			}

			private ValueTask OnStatusUpdate(bool? isLockerOperational, IEnumerable<DoorWatcher.DoorStatus> differences, CancellationToken ct)
			{
				foreach (var d in differences)
					ReportedDoors.Add(d.DoorNumber);
				if (isLockerOperational != null)
					_lockerStateKnown.TrySetResult();
				return ValueTask.CompletedTask;
			}

			public ValueTask DisposeAsync()
			{
				_cts.Cancel();
				return ValueTask.CompletedTask;
			}
		}

		private sealed class FakeLockControllerClient(int doorsPerModule, int installedDoors, params int[] unavailableModules) : ILockControllerClient
		{
			public ConcurrentQueue<int> RequestedModules { get; } = new();

			public int NumberOfDoorsPerController => doorsPerModule;

			/// <summary>Status koji javljaju postojeća vrata (1..installedDoors).</summary>
			public DoorStatusCode InstalledDoorStatus { get; init; } = DoorStatusCode.Closed;

			/// <summary>Status nepovezane pozicije (van installedDoors) - zavisno od modela brave, otvorena ili zatvorena.</summary>
			public DoorStatusCode UnconnectedPositionStatus { get; init; } = DoorStatusCode.Open;

			/// <summary>Poziva se na početku svake provere modula; Task koji vrati drži kontrolerski poziv „u toku“.</summary>
			public Func<int, CancellationToken, Task>? BeforeCheck { get; init; }

			public ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
				=> BeforeCheck == null
					? Statuses(moduleNumber)
					: CheckAfterAsync(moduleNumber, cancellationToken);

			private async ValueTask<DoorStatusCode[]?> CheckAfterAsync(int moduleNumber, CancellationToken cancellationToken)
			{
				await BeforeCheck!(moduleNumber, cancellationToken);
				return await Statuses(moduleNumber);
			}

			private ValueTask<DoorStatusCode[]?> Statuses(int moduleNumber)
			{
				RequestedModules.Enqueue(moduleNumber);
				if (unavailableModules.Contains(moduleNumber))
					return ValueTask.FromResult<DoorStatusCode[]?>(null);
				// pun niz kao pravi kontroler, uključujući i nepovezane pozicije
				var statuses = Enumerable.Range((moduleNumber - 1) * doorsPerModule + 1, doorsPerModule)
										 .Select(door => door <= installedDoors ? InstalledDoorStatus : UnconnectedPositionStatus)
										 .ToArray();
				return ValueTask.FromResult<DoorStatusCode[]?>(statuses);
			}

			public ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, CancellationToken cancellationToken)
				=> CheckDoorMultiple(moduleNumber, null, null, cancellationToken);
			public ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
				=> CheckDoorMultiple(moduleNumber, null, receiveTimeout, cancellationToken);

			public ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorOpenAttempt> OpenDoorOnce(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
		}

		private sealed class ListLogger : ILogger
		{
			private readonly ConcurrentQueue<string> _messages = new();

			public IEnumerable<string> Messages => _messages;

			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
			public bool IsEnabled(LogLevel logLevel) => true;
			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
				=> _messages.Enqueue(formatter(state, exception));
		}
	}
}
