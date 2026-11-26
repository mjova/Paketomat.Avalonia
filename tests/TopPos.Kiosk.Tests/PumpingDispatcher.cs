using System.Collections.Concurrent;
using Flatsoft.Common.UI;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// v4: UI nit za testove umesto WPF Dispatcher-a. UI nit je nit koja je napravila dispečer; poruke se obrađuju samo dok
	/// se čeka (<see cref="WaitWithMessagePump"/>, kao WPF DispatcherFrame i Avalonia PushFrame) ili dok traje <see cref="Run"/>.
	/// </summary>
	internal sealed class PumpingDispatcher : IUiDispatcher
	{
		private static readonly TimeSpan __timeout = TimeSpan.FromSeconds(20);

		private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
		private readonly BlockingCollection<Action> _queue = new();

		public bool CheckAccess() => Environment.CurrentManagedThreadId == _uiThreadId;
		public void Invoke(Action action) => throw new NotSupportedException();
		public void Post(Action action) => _queue.Add(action);

		public void WaitWithMessagePump(Task task)
		{
			Assert.True(CheckAccess());
			Pump(task, "zatvaranje se nije završilo");
		}

		/// <summary>
		/// Pokreće <paramref name="body"/> na UI niti sa <see cref="SynchronizationContext"/> ovog dispečera (nastavci posle
		/// await-a stižu u red) i vrti poruke dok se ne završi — kao WPF <c>Dispatcher.Run</c> do <c>InvokeShutdown</c>.
		/// </summary>
		public void Run(Func<Task> body)
		{
			Assert.True(CheckAccess());
			var previous = SynchronizationContext.Current;
			SynchronizationContext.SetSynchronizationContext(new Context(this));
			try
			{
				Pump(body(), "UI nit nije završila");
			}
			finally {
				SynchronizationContext.SetSynchronizationContext(previous);
			}
		}

		private void Pump(Task task, string timeoutMessage)
		{
			var deadline = DateTime.UtcNow + __timeout;
			while (!task.IsCompleted) {
				Assert.True(DateTime.UtcNow < deadline, timeoutMessage);
				if (_queue.TryTake(out var action, TimeSpan.FromMilliseconds(10))) {
					action();
				}
			}
		}

		private sealed class Context(PumpingDispatcher dispatcher) : SynchronizationContext
		{
			public override void Post(SendOrPostCallback d, object? state) => dispatcher.Post(() => d(state));
			public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
			public override SynchronizationContext CreateCopy() => this;
		}
	}
}