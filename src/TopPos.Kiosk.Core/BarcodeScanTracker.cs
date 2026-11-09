using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace TopPos.Kiosk
{
	/// <summary>
	/// Čeka barkod sa skenera do isteka roka i javlja ga (ili istek). Jedno čekanje u isto vreme: novo <see cref="Start"/>
	/// zaustavlja prethodno. Start/Stop su bezbedni za pozive iz više niti (UI, pool, iz callback-a samog čekanja).
	/// </summary>
	public class BarcodeScanTracker
	{
		private readonly BarcodeScannerClient _barcodeScannerClient;
		private readonly TimeSpan _trackLimit;
		private readonly int _minimumBarcodeLength;
		private readonly ILogger? _logger;
		private readonly object _lock = new();
		private Tracking? _current;

		/// <summary>Jedno čekanje: svoj rok (novo čekanje ga ne pomera) i svoj token.</summary>
		private sealed class Tracking
		{
			public readonly CancellationTokenSource Cts = new();
			public DateTime StartTimestamp = DateTime.Now;
			// postoji od početka, pa ga Stop uvek vidi
			public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		public BarcodeScanTracker(BarcodeScannerClient barcodeScannerClient, TimeSpan trackLimit, int minimumBarcodeLength, ILogger? logger)
		{
			_barcodeScannerClient = barcodeScannerClient;
			_trackLimit = trackLimit;
			_minimumBarcodeLength = minimumBarcodeLength;
			_logger = logger;
			_logger?.LogInformation($"{nameof(BarcodeScanTracker)} inicijalizovan. Vremenski limit:{trackLimit.TotalSeconds} s, minimalna duzina barkoda: {minimumBarcodeLength}");
		}

		private async Task TrackingCountdown(Tracking tracking, Func<string?, ValueTask<(bool, TimeSpan?)>> notifyCompleted)
		{
			var cancellationToken = tracking.Cts.Token;
			string? barcode = null;
			try
			{
				_logger?.LogInformation($"{nameof(BarcodeScanTracker)}: {nameof(TrackingCountdown)} starting: attaching a new barcode processing handler.");
				_barcodeScannerClient.ProcessBarcode = bc => {
																if (cancellationToken.IsCancellationRequested) {
																	return new ValueTask<bool>(false);
																}
																if (bc == null || bc.Length < _minimumBarcodeLength) {
																	return new ValueTask<bool>(false);
																}
																barcode = bc;
																return new ValueTask<bool>(true);
															};
				while (DateTime.Now - tracking.StartTimestamp < _trackLimit)
				{
					if (cancellationToken.IsCancellationRequested) {
						return;
					}
					if (!string.IsNullOrEmpty(barcode)) {
						_logger?.LogInformation($"{nameof(BarcodeScanTracker)}: ocitan barkod '{barcode}'");
						try {
							(bool stopTracking, TimeSpan? extendTime) = await notifyCompleted(barcode).ConfigureAwait(false);
							if (stopTracking) {
								return;
							}
							_logger?.LogWarning($"{nameof(BarcodeScanTracker)}: barkod '{barcode}' nije prihvaćen.");
							barcode = null;
							lock (_lock) {
								tracking.StartTimestamp += extendTime ?? _trackLimit;
							}
						}
						catch (Exception ex) {
							// TODO: should we quit this loop if we can't deliver the notification?
							_logger?.LogError($"{nameof(BarcodeScanTracker)}: exception: {ex.Message}");
						}

					}
					await Task.Delay(TimeSpan.FromMilliseconds(1000), cancellationToken).ConfigureAwait(false);
				}
				if (!cancellationToken.IsCancellationRequested)
				{
					try {
						await notifyCompleted(null).ConfigureAwait(false);
					}
					catch (Exception ex) {
						_logger?.LogError($"{nameof(BarcodeScanTracker)}: exception: {ex.Message}");
					}
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
				// zaustavljeno tokom pauze između provera
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(BarcodeScanTracker)}: exception: {ex.Message}");
			}
			finally {
				lock (_lock) {
					if (_current == tracking) {
						_current = null;
					}
				}
				tracking.Cts.Dispose();		// tek ovde: handler skenera i dalje čita token
				tracking.Done.TrySetResult();
			}
		}

		public void ResetCounter() {
			_logger?.LogInformation($"{nameof(BarcodeScanTracker)}: reset brojaca.");
			lock (_lock) {
				if (_current != null) {
					_current.StartTimestamp = DateTime.Now;
				}
			}
		}

		/// <summary>Zaustavlja čekanje i ne čeka ga (vidi <see cref="StopAsync"/>).</summary>
		public void Stop() => _ = StopAsync();

		/// <summary>
		/// Odmah otkazuje čekanje — callback se posle ovoga ne poziva, osim ako je već krenuo. Vraćeni Task se završava kad
		/// čekanje stvarno stane (callback koji je već krenuo). Ne čekati ga iz samog callback-a.
		/// </summary>
		public Task StopAsync()
		{
			_logger?.LogInformation($"{nameof(BarcodeScanTracker)}: zaustavljanje brojaca.");
			Tracking? stopped;
			lock (_lock) {
				stopped = _current;
				_current = null;
				stopped?.Cts.Cancel();
			}
			return stopped?.Done.Task ?? Task.CompletedTask;
		}

		/// <summary>
		/// Pokreće čekanje (prethodno se zaustavlja). Vraćeni Task se završava kad se čekanje završi — posle callback-a, ili
		/// bez njega ako je zaustavljeno; nikad ne baca izuzetak.
		/// </summary>
		public Task Start(Func<string?, ValueTask<(bool, TimeSpan?)>> notifyComplete)
		{
			_logger?.LogInformation($"{nameof(BarcodeScanTracker)}: start brojaca.");
			var tracking = new Tracking();
			lock (_lock) {
				_current?.Cts.Cancel();
				_current = tracking;
			}
			_ = TrackingCountdown(tracking, notifyComplete);	// van lock-a: callback može da pozove Start/Stop
			return tracking.Done.Task;
		}
    }
}
