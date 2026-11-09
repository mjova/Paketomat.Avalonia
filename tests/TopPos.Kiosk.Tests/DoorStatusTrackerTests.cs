using System.Collections.Concurrent;
using TopPos.Client.WPF;
using TopPos.LockController;
using Xunit;

namespace TopPos.Workflow.Tests
{
	/// <summary>
	/// DoorStatusTracker: kraj praćenja je vidljiv pozivaocu i kad je praćenje zaustavljeno (ranije je čekanje na callback
	/// visilo zauvek — „Preuzmi paket“ posle Reset-a), a StopAsync čeka proveru vrata koja je u toku.
	/// </summary>
	public class DoorStatusTrackerTests
	{
		private static readonly TimeSpan __wait = TimeSpan.FromSeconds(5);

		[Fact]
		public async Task Target_status_calls_callback_then_tracking_completes()
		{
			var client = new FakeDoorClient { Status = DoorStatusCode.Closed };
			var tracker = new DoorStatusTracker(client, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100), null);
			DoorStatusCode? reported = null;

			await tracker.Start(7, DoorStatusCode.Closed, s => { reported = s; return ValueTask.CompletedTask; }).WaitAsync(__wait);

			Assert.Equal(DoorStatusCode.Closed, reported);
			Assert.All(client.CheckedDoors, d => Assert.Equal(7, d));
		}

		[Fact]
		public async Task Stop_completes_tracking_without_callback()
		{
			var client = new FakeDoorClient { Status = DoorStatusCode.Open };
			var tracker = new DoorStatusTracker(client, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100), null);
			var called = false;

			var tracking = tracker.Start(7, DoorStatusCode.Closed, _ => { called = true; return ValueTask.CompletedTask; });
			await client.WaitForChecksAsync(1);
			tracker.Stop();

			await tracking.WaitAsync(__wait);
			Assert.False(called);
		}

		[Fact]
		public async Task StopAsync_waits_for_door_check_in_progress()
		{
			var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			// kao NCU: poziv koji je krenuo ne poštuje token
			var client = new FakeDoorClient { Status = DoorStatusCode.Open, BeforeCheck = _ => release.Task };
			var tracker = new DoorStatusTracker(client, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100), null);
			var called = false;

			var tracking = tracker.Start(7, DoorStatusCode.Closed, _ => { called = true; return ValueTask.CompletedTask; });
			await client.WaitForChecksAsync(1);

			var stopped = tracker.StopAsync();
			await Task.Delay(100);
			Assert.False(stopped.IsCompleted);

			release.TrySetResult();
			await stopped.WaitAsync(__wait);
			Assert.True(tracking.IsCompleted);
			Assert.False(called);
			Assert.True(tracker.StopAsync().IsCompleted);   // ništa više ne radi
		}

		[Fact]
		public async Task Time_limit_reports_last_status()
		{
			var client = new FakeDoorClient { Status = DoorStatusCode.Open };
			var tracker = new DoorStatusTracker(client, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100), null);
			DoorStatusCode? reported = null;
			var called = false;

			await tracker.Start(7, DoorStatusCode.Closed, s => { called = true; reported = s; return ValueTask.CompletedTask; }).WaitAsync(__wait);

			Assert.True(called);
			Assert.Equal(DoorStatusCode.Open, reported);
		}

		[Fact]
		public async Task Start_from_own_callback_runs_new_tracking()
		{
			// CourierPickupActivator pokreće praćenje sledećih vrata iz callback-a prethodnog. Kraj prvog praćenja ne sme da
			// „obriše“ drugo: StopAsync posle toga mora da otkaže drugo i sačeka njegovu proveru u toku.
			var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var client = new FakeDoorClient { Status = DoorStatusCode.Closed, BeforeCheck = door => door == 2 ? releaseSecond.Task : Task.CompletedTask };
			var tracker = new DoorStatusTracker(client, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100), null);
			var second = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
			var reported = new ConcurrentQueue<int>();

			var first = tracker.Start(1, DoorStatusCode.Closed, _ => {
				reported.Enqueue(1);
				second.TrySetResult(tracker.Start(2, DoorStatusCode.Closed, _ => { reported.Enqueue(2); return ValueTask.CompletedTask; }));
				return ValueTask.CompletedTask;
			});

			await first.WaitAsync(__wait);
			var secondTracking = await second.Task.WaitAsync(__wait);
			await client.WaitForChecksAsync(2);         // druga provera visi na vratima 2
			Assert.False(secondTracking.IsCompleted);

			var stopped = tracker.StopAsync();
			await Task.Delay(100);
			Assert.False(stopped.IsCompleted);          // čeka proveru drugog praćenja, ne vraća se odmah

			releaseSecond.TrySetResult();
			await stopped.WaitAsync(__wait);
			Assert.True(secondTracking.IsCompleted);
			Assert.Equal([1], reported);                // drugo je zaustavljeno pre callback-a
		}

		[Fact]
		public async Task New_start_stops_previous_tracking()
		{
			var client = new FakeDoorClient { Status = DoorStatusCode.Open };
			var tracker = new DoorStatusTracker(client, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100), null);
			var firstCalled = false;

			var first = tracker.Start(1, DoorStatusCode.Closed, _ => { firstCalled = true; return ValueTask.CompletedTask; });
			var second = tracker.Start(2, DoorStatusCode.Closed, _ => ValueTask.CompletedTask);

			await first.WaitAsync(__wait);
			Assert.False(firstCalled);
			Assert.False(second.IsCompleted);
			await tracker.StopAsync().WaitAsync(__wait);
			Assert.True(second.IsCompleted);
		}

		internal sealed class FakeDoorClient : ILockControllerClient
		{
			private int _checks;

			public DoorStatusCode Status { get; init; } = DoorStatusCode.Closed;

			/// <summary>Poziva se na početku svake provere; Task koji vrati drži proveru „u toku“ (bez obzira na token).</summary>
			public Func<int, Task>? BeforeCheck { get; init; }

			public ConcurrentQueue<int> CheckedDoors { get; } = new();

			public int NumberOfDoorsPerController => 8;

			public async Task WaitForChecksAsync(int count)
			{
				var deadline = DateTime.UtcNow + __wait;
				while (Volatile.Read(ref _checks) < count) {
					Assert.True(DateTime.UtcNow < deadline, "provera vrata nije krenula");
					await Task.Delay(10);
				}
			}

			public async ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
			{
				CheckedDoors.Enqueue(doorNumber);
				Interlocked.Increment(ref _checks);
				if (BeforeCheck != null) {
					await BeforeCheck(doorNumber);
				}
				return Status;
			}

			public ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, CancellationToken cancellationToken)
				=> CheckDoorSingle(doorNumber, null, null, cancellationToken);
			public ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
				=> CheckDoorSingle(doorNumber, null, receiveTimeout, cancellationToken);

			public ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorOpenAttempt> OpenDoorOnce(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
		}
	}
}
