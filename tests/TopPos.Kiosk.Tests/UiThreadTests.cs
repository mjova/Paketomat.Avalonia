using System.ComponentModel;
using Flatsoft.Common.UI;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>Testovi menjaju globalni UiThread.Dispatcher, pa ne rade paralelno sa ostalima.</summary>
	[CollectionDefinition(nameof(UiThreadCollection), DisableParallelization = true)]
	public sealed class UiThreadCollection { }

	/// <summary>
	/// v4: ViewModel-i iz v2 menjaju svojstva i sa pozadinskih niti; obaveštenja prikazu moraju na UI nit, istim redom.
	/// </summary>
	[Collection(nameof(UiThreadCollection))]
	public sealed class UiThreadTests : IDisposable
	{
		private sealed class QueueDispatcher : IUiDispatcher
		{
			public readonly List<Action> Posted = new();
			public bool OnUiThread { get; set; }
			public int Invoked { get; private set; }
			public bool CheckAccess() => OnUiThread;
			public void Invoke(Action action) { Invoked++; action(); }
			public void Post(Action action) { lock (Posted) { Posted.Add(action); } }
			public void WaitWithMessagePump(Task task) => task.ContinueWith(_ => { }, TaskScheduler.Default).Wait();
			public void RunPosted()
			{
				List<Action> actions;
				lock (Posted) {
					actions = Posted.ToList();
					Posted.Clear();
				}
				foreach (var action in actions) {
					action();
				}
			}
		}

		private sealed class TestViewModel : ViewModelBase
		{
			private int _value;
			public int Value { get => _value; set => SetProperty(ref _value, value); }
		}

		private readonly IUiDispatcher _original = UiThread.Dispatcher;
		private readonly QueueDispatcher _dispatcher = new();

		public UiThreadTests() => UiThread.Dispatcher = _dispatcher;

		public void Dispose() => UiThread.Dispatcher = _original;

		[Fact]
		public void Property_change_off_ui_thread_is_raised_later_on_ui_thread_in_order()
		{
			var vm = new TestViewModel();
			var raised = new List<string?>();
			vm.PropertyChanged += (_, e) => raised.Add($"{e.PropertyName}={vm.Value}");

			vm.Value = 1;
			vm.Value = 2;

			Assert.Empty(raised);
			Assert.Equal(2, _dispatcher.Posted.Count);
			_dispatcher.RunPosted();
			Assert.Equal(["Value=2", "Value=2"], raised);	// getter čita najnoviju vrednost, kao WPF binding
		}

		[Fact]
		public void Property_change_on_ui_thread_is_raised_immediately()
		{
			_dispatcher.OnUiThread = true;
			var vm = new TestViewModel();
			int raised = 0;
			vm.PropertyChanged += (_, _) => raised++;

			vm.Value = 1;

			Assert.Equal(1, raised);
			Assert.Empty(_dispatcher.Posted);
		}

		[Fact]
		public void RunOnUiThread_off_ui_thread_waits_with_Invoke()
		{
			bool ran = false;
			UIThreadSwitcher.RunOnUiThread(() => ran = true);
			Assert.True(ran);
			Assert.Equal(1, _dispatcher.Invoked);
		}

		[Fact]
		public void Command_CanExecuteChanged_off_ui_thread_goes_to_ui_thread()
		{
			var command = new RelayCommand(_ => { });
			int raised = 0;
			command.CanExecuteChanged += (_, _) => raised++;

			command.RaiseCanExecuteChanged();

			Assert.Equal(0, raised);
			_dispatcher.RunPosted();
			Assert.Equal(1, raised);
		}

		[Fact]
		public void Timer_tick_queued_before_Stop_is_dropped()
		{
			var timer = new UiTimer { Interval = TimeSpan.FromMilliseconds(10) };
			int ticks = 0;
			timer.Tick += (_, _) => ticks++;

			timer.Start();
			Assert.True(SpinWait.SpinUntil(() => { lock (_dispatcher.Posted) { return _dispatcher.Posted.Count > 0; } }, TimeSpan.FromSeconds(5)));
			timer.Stop();
			Thread.Sleep(50);
			_dispatcher.RunPosted();

			Assert.Equal(0, ticks);
		}

		[Fact]
		public void Timer_ticks_on_ui_thread_until_stopped()
		{
			UiThread.Dispatcher = _original;	// odmah, na niti tajmera
			var timer = new UiTimer { Interval = TimeSpan.FromMilliseconds(10) };
			int ticks = 0;
			timer.Tick += (_, _) => Interlocked.Increment(ref ticks);

			timer.Start();
			Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 2, TimeSpan.FromSeconds(5)));
			timer.Stop();
			Thread.Sleep(20);	// tick koji je prošao proveru pre Stop() se završava
			int afterStop = Volatile.Read(ref ticks);
			Thread.Sleep(50);

			Assert.Equal(afterStop, Volatile.Read(ref ticks));
		}
	}
}