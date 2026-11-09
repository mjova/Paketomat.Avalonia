﻿using Microsoft.Extensions.Logging;
using TopPos.LockController;

namespace TopPos.Kiosk
{
	/// <summary>
	/// Prati status jednih vrata dok ne dostignu ciljni status ili ne istekne rok, pa javlja ishod. Jedno praćenje u isto
	/// vreme: novo <see cref="Start"/> zaustavlja prethodno. Start/Stop su bezbedni za pozive iz više niti (UI, pool, iz
	/// callback-a samog praćenja).
	/// </summary>
	public class DoorStatusTracker
	{
		private readonly ILockControllerClient _lockControllerClient;
		private readonly TimeSpan _trackLimit;
		private readonly TimeSpan _doorStatusResponseTimeout;
		private readonly ILogger? _logger;
		private readonly object _lock = new();
		private Tracking? _current;

		/// <summary>Jedno praćenje: svoj rok (novo praćenje ga ne pomera) i svoj token.</summary>
		private sealed class Tracking
		{
			public readonly CancellationTokenSource Cts = new();
			public DateTime StartTimestamp = DateTime.Now;
			// postoji od početka (pre prvog poziva kontrolera), pa ga Stop uvek vidi
			public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		public DoorStatusTracker(ILockControllerClient lockControllerClient, TimeSpan trackLimit, TimeSpan doorStatusResponseTimeout, ILogger? logger)
		{
			_lockControllerClient = lockControllerClient;
			_trackLimit = trackLimit;
			_doorStatusResponseTimeout = doorStatusResponseTimeout;
			_logger = logger;
			_logger?.LogInformation($"{nameof(DoorStatusTracker)} inicijalizovan, period: {_trackLimit.TotalSeconds} s.");
		}

		private async Task TrackingCountdown(Tracking tracking, int doorNumber, DoorStatusCode targetStatusCode,
											Func<DoorStatusCode?, ValueTask> notifyCompleted)
		{
			var cancellationToken = tracking.Cts.Token;
			try {
				DoorStatusCode? statusCode = null;
				while (DateTime.Now - tracking.StartTimestamp < _trackLimit)
				{
					if (cancellationToken.IsCancellationRequested) {
						return;
					}
					try
					{
						statusCode = await _lockControllerClient.CheckDoorSingle(doorNumber, _doorStatusResponseTimeout, cancellationToken).ConfigureAwait(false);
						if (statusCode == targetStatusCode) {
							break;
						}
					}
					catch (Exception ex) {
						_logger?.LogError($"{nameof(DoorStatusTracker)}: tracking exception: {ex.Message}.");
					}
					await Task.Delay(TimeSpan.FromMilliseconds(1000), cancellationToken).ConfigureAwait(false);
				}
				if (!cancellationToken.IsCancellationRequested)
				{
					try {
						_logger?.LogInformation($"{nameof(DoorStatusTracker)}: pracenje zavrseno, slanje notifikacije sa statusom: '[{doorNumber}]={statusCode}'.");
						await notifyCompleted(statusCode).ConfigureAwait(false);
					}
					catch (Exception ex) {
						_logger?.LogError($"{nameof(DoorStatusTracker)}: notification exception: {ex.Message}");
					}
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
				// zaustavljeno tokom pauze između provera
			}
			finally {
				lock (_lock) {
					if (_current == tracking) {
						_current = null;
					}
				}
				tracking.Cts.Dispose();		// tek ovde: token je do sada mogao biti u upotrebi (CheckDoorSingle)
				tracking.Done.TrySetResult();
			}
		}

		public void ResetCounter() {
			lock (_lock) {
				if (_current == null) {
					return;
				}
				_current.StartTimestamp = DateTime.Now;
			}
			_logger?.LogInformation($"{nameof(DoorStatusTracker)}: brojac resetovan.");
		}

		/// <summary>Zaustavlja praćenje i ne čeka ga (vidi <see cref="StopAsync"/>).</summary>
		public void Stop() => _ = StopAsync();

		/// <summary>
		/// Odmah otkazuje praćenje — callback se posle ovoga ne poziva, osim ako je već krenuo. Vraćeni Task se završava kad
		/// praćenje stvarno stane (provera vrata u toku, odnosno callback koji je već krenuo). Ne čekati ga iz samog callback-a.
		/// </summary>
		public Task StopAsync()
		{
			Tracking? stopped;
			lock (_lock) {
				stopped = _current;
				_current = null;
				stopped?.Cts.Cancel();
			}
			_logger?.LogInformation($"{nameof(DoorStatusTracker)}: brojac zaustavljen.");
			return stopped?.Done.Task ?? Task.CompletedTask;
		}

		/// <summary>
		/// Pokreće praćenje (prethodno se zaustavlja). Vraćeni Task se završava kad se praćenje završi — posle callback-a, ili
		/// bez njega ako je praćenje zaustavljeno; nikad ne baca izuzetak.
		/// </summary>
		public Task Start(int doorNumber, DoorStatusCode targetStatusCode, Func<DoorStatusCode?, ValueTask> notifyComplete)
		{
			var tracking = new Tracking();
			lock (_lock) {
				_current?.Cts.Cancel();
				_current = tracking;
			}
			_logger?.LogInformation($"{nameof(DoorStatusTracker)}: pokretanje pracenja statusa vrata [{doorNumber}], period: {_trackLimit.TotalSeconds} s.");
			_ = TrackingCountdown(tracking, doorNumber, targetStatusCode, notifyComplete);	// van lock-a: callback može da pozove Start/Stop
			return tracking.Done.Task;
		}
    }
}
