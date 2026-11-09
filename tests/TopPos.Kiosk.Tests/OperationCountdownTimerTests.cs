using TopPos.Client.WPF;
using Xunit;

namespace TopPos.Workflow.Tests
{
	/// <summary>
	/// Istek opšteg tajmera nosi proveru važenja: Reset koji je krenuo baš kad je ekran zaustavio tajmer (ili ga pokrenuo
	/// ispočetka, ili se sesija završila) proverava se tek kad dođe na red, pa ne prekida sledeći ekran ni sledeću sesiju.
	/// </summary>
	public class OperationCountdownTimerTests
	{
		private static async Task<Func<bool>> StartAndWaitForExpiryAsync(OperationCountdownTimer timer)
		{
			var expired = new TaskCompletionSource<Func<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
			timer.NotifyCountdownCompleted = stillCurrent => {
				expired.TrySetResult(stillCurrent);
				return ValueTask.CompletedTask;
			};
			timer.Start();
			return await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
		}

		[Fact]
		public async Task Expiry_is_current_until_timer_is_stopped()
		{
			var timer = new OperationCountdownTimer(TimeSpan.FromMilliseconds(50), logger: null);
			var stillCurrent = await StartAndWaitForExpiryAsync(timer);
			Assert.True(stillCurrent());
			timer.Stop();
			Assert.False(stillCurrent());
		}

		[Fact]
		public async Task Expiry_is_stale_after_restart_for_next_screen()
		{
			var timer = new OperationCountdownTimer(TimeSpan.FromMilliseconds(50), logger: null);
			var stillCurrent = await StartAndWaitForExpiryAsync(timer);
			timer.Start(TimeSpan.FromMinutes(1));
			Assert.False(stillCurrent());
			timer.Stop();
		}
	}
}