using System.Collections.Concurrent;
using Flatsoft.Common.UI;
using TopPos.LockController;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// v4: v2 test (WPF Dispatcher) sa UI niti koju glumi <see cref="PumpingDispatcher"/>: poruke se obrađuju dok
	/// <see cref="IUiDispatcher.WaitWithMessagePump"/> čeka, kao WPF DispatcherFrame i Avalonia PushFrame.
	/// </summary>
	[Collection(nameof(UiThreadCollection))]
	public class LockControllerShutdownTests : IDisposable
	{
		private readonly IUiDispatcher _original = UiThread.Dispatcher;

		public void Dispose() => UiThread.Dispatcher = _original;

		[Fact]
		public void Close_repeated_while_waiting_closes_once_and_returns_only_after_the_end()
		{
			// WM_QUERYENDSESSION usred izlaska: dispečer koji čeka zatvaranje obradi poruku, pa se Close poziva ugnežđeno. Windows
			// posle tog odgovora sme da ugasi proces, a spoljni poziv ne može da nastavi dok se ugnežđeni ne vrati.
			using var nestedEntered = new ManualResetEventSlim();
			var client = new SlowClosingController(() => {
				Assert.True(nestedEntered.Wait(TimeSpan.FromSeconds(10)), "ugnežđeni poziv nije stigao");
				Thread.Sleep(100);
			});
			var dispatcher = new PumpingDispatcher();
			UiThread.Dispatcher = dispatcher;
			var shutdown = new LockControllerShutdown(client, logger: null);
			bool closedWhenNestedReturned = false;

			dispatcher.Post(() => {
				nestedEntered.Set();
				shutdown.Close();
				closedWhenNestedReturned = client.Closed;
			});
			shutdown.Close();

			Assert.True(nestedEntered.IsSet, "ugnežđeni poziv nije obrađen dok se čekalo");
			Assert.True(closedWhenNestedReturned, "ugnežđeni poziv se vratio pre kraja zatvaranja");
			Assert.Equal(1, client.DisposeCount);
		}

		[Fact]
		public void Controller_without_Dispose_is_not_waited_for()
		{
			var dispatcher = new PumpingDispatcher();
			UiThread.Dispatcher = dispatcher;
			bool messageProcessed = false;
			dispatcher.Post(() => messageProcessed = true);

			new LockControllerShutdown(new NoOpController(), logger: null).Close();

			Assert.False(messageProcessed);
		}

		/// <summary>UI nit je nit koja je napravila dispečer; poruke se obrađuju samo dok se čeka.</summary>
		private sealed class PumpingDispatcher : IUiDispatcher
		{
			private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
			private readonly BlockingCollection<Action> _queue = new();

			public bool CheckAccess() => Environment.CurrentManagedThreadId == _uiThreadId;
			public void Invoke(Action action) => throw new NotSupportedException();
			public void Post(Action action) => _queue.Add(action);

			public void WaitWithMessagePump(Task task)
			{
				Assert.True(CheckAccess());
				var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
				while (!task.IsCompleted) {
					Assert.True(DateTime.UtcNow < deadline, "zatvaranje se nije završilo");
					if (_queue.TryTake(out var action, TimeSpan.FromMilliseconds(10))) {
						action();
					}
				}
			}
		}

		private sealed class SlowClosingController(Action whileClosing) : NoOpController, IDisposable
		{
			private int _disposeCount;
			private volatile bool _closed;

			public int DisposeCount => Volatile.Read(ref _disposeCount);
			public bool Closed => _closed;

			public void Dispose()
			{
				Interlocked.Increment(ref _disposeCount);
				whileClosing();
				_closed = true;
			}
		}

		/// <summary>Kontroler bez I/O i bez Dispose; nijedna komanda se ne poziva.</summary>
		private class NoOpController : ILockControllerClient
		{
			public int NumberOfDoorsPerController => 16;
			public ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorOpenAttempt> OpenDoorOnce(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode[]?> OpenDoorMultiple(int moduleNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode[]?> OpenDoorMultiple(int moduleNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode[]?> OpenDoorMultiple(int moduleNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
		}
	}
}