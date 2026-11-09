using Microsoft.Extensions.Logging;

namespace TopPos.Client.WPF
{
	public class OperationCountdownTimer
	{
		private readonly TimeSpan _defaultLimit;
		private TimeSpan _runningLimit;
		private Task? _taskRunningCountdown;
		
		private DateTime? _startTimestamp;
		private CancellationTokenSource? _ctsStopCountdown;
		private readonly ILogger? _logger;

		public OperationCountdownTimer(TimeSpan runningLimit, ILogger? logger) 
		{ 
			_defaultLimit = runningLimit;
			_runningLimit = runningLimit;
			_logger = logger;
			_logger?.LogInformation($"{nameof(OperationCountdownTimer)} inicijalizovan, period: {_runningLimit.TotalSeconds} s.");
		}

		/// <summary>
		/// Istek odbrojavanja. Argument kaže da li je to odbrojavanje i dalje važeće (nije zaustavljeno ni pokrenuto ispočetka):
		/// istek koji je krenuo baš kad je ekran zaustavio tajmer (ili se sesija završila) proverava se tek kad njegov triger
		/// dođe na red (<see cref="Workflow.WorkflowMachine.FireGuardedAsync(Func{bool}, Workflow.WorkflowTrigger, Action?)"/>).
		/// </summary>
		public Func<Func<bool>, ValueTask>? NotifyCountdownCompleted { get; set; }

		private async Task RunningCountdown(CancellationToken cancellationToken)
		{
			while (DateTime.Now - _startTimestamp < _runningLimit)
			{
				if (cancellationToken.IsCancellationRequested) {
					return;
				}
				await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
			}
			if (!cancellationToken.IsCancellationRequested) {
				var notifyCompleted = this.NotifyCountdownCompleted;
				if (notifyCompleted != null) {
					try { 
						_logger?.LogInformation($"{nameof(OperationCountdownTimer)}: odbrojavanje zavrseno, slanje notifikacije o isteklom vremenu.");
						await notifyCompleted(() => !cancellationToken.IsCancellationRequested).ConfigureAwait(false);
					}
					catch (Exception ex) {
						_logger?.LogError($"{nameof(OperationCountdownTimer)}: notification exception: {ex.Message}");
					}
				}
			}
		}

		public void ResetCounter() {
			if (_taskRunningCountdown != null) {
				_logger?.LogInformation($"{nameof(OperationCountdownTimer)}: brojac resetovan.");
				_startTimestamp = DateTime.Now;
			}
		}

		public void Stop()
		{
			_ctsStopCountdown?.Cancel();
			_ctsStopCountdown?.Dispose();
			_ctsStopCountdown = null;
			_startTimestamp = null;
			_taskRunningCountdown = null;
			_logger?.LogInformation($"{nameof(OperationCountdownTimer)}: odbrojavanje zaustavljeno.");
		}

		/// <summary>
		/// Pokreće odbrojavanje ispočetka. <paramref name="runningLimit"/> važi samo za ovo odbrojavanje; bez njega se
		/// koristi podrazumevani rok iz konstruktora — rok zadat jednom ekranu (npr. PaymentTimeout) ne prelazi na sledeće.
		/// </summary>
		public void Start(TimeSpan? runningLimit = null)
		{
			_startTimestamp = DateTime.Now;
			_ctsStopCountdown?.Cancel();
			_ctsStopCountdown?.Dispose();
			_ctsStopCountdown = new CancellationTokenSource();
			_runningLimit = runningLimit ?? _defaultLimit;
			_logger?.LogInformation($"{nameof(OperationCountdownTimer)}: pokretanje odbrojavanja, period: {_runningLimit.TotalSeconds} s.");
			_taskRunningCountdown = RunningCountdown(_ctsStopCountdown.Token);
		}
    }
}
