using System.Collections.Concurrent;
using Flatsoft.Common.UI;
using TopPos.Kiosk.AgentCommunication;
using TopPos.Kiosk.Workflow;
using Xunit;
using S = TopPos.Kiosk.Workflow.WorkflowState;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Sonda za heartbeat: potvrđuje živu UI nit, a zaglavljena UI nit ne potvrđuje u roku. v4: iz v2
	/// <c>AgentPipeServerTests.Dispatcher_probe_confirms_running_dispatcher_and_not_a_blocked_one</c>; UI nit je nit sa redom
	/// poruka iza <see cref="UiThread.Dispatcher"/> (v2: WPF Dispatcher).
	/// </summary>
	[Collection(nameof(UiThreadCollection))]
	public sealed class DispatcherLivenessProbeTests : IDisposable
	{
		private static readonly TimeSpan __readTimeout = TimeSpan.FromSeconds(5);

		private readonly IUiDispatcher _original = UiThread.Dispatcher;
		private readonly ThreadDispatcher _dispatcher = new();

		public DispatcherLivenessProbeTests() => UiThread.Dispatcher = _dispatcher;

		public void Dispose()
		{
			UiThread.Dispatcher = _original;
			_dispatcher.Dispose();
		}

		[Fact]
		public async Task Dispatcher_probe_confirms_running_dispatcher_and_not_a_blocked_one()
		{
			var source = new KioskStatusSource("112", logger: null);
			source.SetWorkflowState(S.Start);
			var probe = DispatcherLivenessProbe.Create(source);

			Assert.True(await probe(CancellationToken.None).WaitAsync(__readTimeout));

			using var release = new ManualResetEventSlim();
			_dispatcher.Post(() => release.Wait(__readTimeout));
			using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
			var blocked = probe(cts.Token);
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked.WaitAsync(TimeSpan.FromSeconds(2)));
			release.Set();
		}

		/// <summary>
		/// v4: dok je UI nit blokirana, istekle sonde se ne gomilaju u njenom redu (Post se ne može povući) — u redu je najviše
		/// jedna; posle oporavka sledeća sonda potvrđuje rad.
		/// </summary>
		[Fact]
		public async Task Blocked_ui_thread_holds_at_most_one_probe_and_confirms_after_recovery()
		{
			var source = new KioskStatusSource("112", logger: null);
			source.SetWorkflowState(S.Start);
			var probe = DispatcherLivenessProbe.Create(source);
			using var release = new ManualResetEventSlim();
			_dispatcher.Post(() => release.Wait(__readTimeout));
			int postedBefore = _dispatcher.PostNormalCount;

			for (int i = 0; i < 3; i++) {
				using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
				await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe(cts.Token));
			}

			Assert.Equal(postedBefore + 1, _dispatcher.PostNormalCount);
			release.Set();
			Assert.True(await probe(CancellationToken.None).WaitAsync(__readTimeout));
		}

		/// <summary>UI nit sa redom poruka; poruke se izvršavaju redom, na njoj.</summary>
		private sealed class ThreadDispatcher : IUiDispatcher, IDisposable
		{
			private readonly BlockingCollection<Action> _queue = new();
			private readonly Thread _thread;

			public ThreadDispatcher()
			{
				_thread = new Thread(() => {
					foreach (var action in _queue.GetConsumingEnumerable()) {
						action();
					}
				}) { IsBackground = true };
				_thread.Start();
			}

			public bool CheckAccess() => Thread.CurrentThread == _thread;
			public void Invoke(Action action) => throw new NotSupportedException();
			/// <summary>Broj poziva PostNormal (samo sonda; Post koriste i ostaci drugih testova koji još rade).</summary>
			public int PostNormalCount => Volatile.Read(ref _postNormalCount);
			private int _postNormalCount;

			public void Post(Action action) => _queue.Add(action);

			public void PostNormal(Action action)
			{
				Interlocked.Increment(ref _postNormalCount);
				_queue.Add(action);
			}
			public void WaitWithMessagePump(Task task) => throw new NotSupportedException();

			public void Dispose()
			{
				_queue.CompleteAdding();
				_thread.Join(__readTimeout);
			}
		}
	}
}
