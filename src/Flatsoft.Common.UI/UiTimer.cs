namespace Flatsoft.Common.UI
{
	/// <summary>
	/// v4: zamena za WPF DispatcherTimer bez UI biblioteke. <see cref="Tick"/> se izvršava na UI niti
	/// (<see cref="UiThread"/>); tick koji je krenuo pre <see cref="Stop"/> ili ponovnog <see cref="Start"/> se odbacuje.
	/// </summary>
	public sealed class UiTimer
	{
		private readonly object _gate = new();
		private Timer? _timer;
		private int _generation;

		public TimeSpan Interval { get; init; }

		public event EventHandler? Tick;

		public void Start()
		{
			lock (_gate) {
				_timer?.Dispose();
				int generation = ++_generation;
				_timer = new Timer(_ => UiThread.Raise(() => OnTick(generation)), null, Interval, Interval);
			}
		}

		public void Stop()
		{
			lock (_gate) {
				_generation++;
				_timer?.Dispose();
				_timer = null;
			}
		}

		private void OnTick(int generation)
		{
			lock (_gate) {
				if (generation != _generation) {
					return;
				}
			}
			Tick?.Invoke(this, EventArgs.Empty);
		}
	}
}
