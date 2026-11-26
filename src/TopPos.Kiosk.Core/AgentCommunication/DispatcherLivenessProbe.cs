using Flatsoft.Common.UI;
using TopPos.Kiosk.Workflow;

namespace TopPos.Kiosk.AgentCommunication
{
	/// <summary>
	/// Potvrda za heartbeat (plan §5): UI nit mora da izvrši kratku operaciju u roku, i u njoj da pročita izvor statusa.
	/// Zaglavljena UI nit ili zaključan izvor znače da potvrde nema, pa heartbeat izostaje i Agentu status zastari.
	/// v4: UI nit preko <see cref="UiThread"/> (v2: WPF <c>Dispatcher.InvokeAsync</c> sa <c>DispatcherPriority.Normal</c>), iza već
	/// zakazanog UI posla.
	/// </summary>
	public static class DispatcherLivenessProbe
	{
		public static Func<CancellationToken, Task<bool>> Create(KioskStatusSource statusSource)
		{
			// v4: u redu UI niti je najviše jedna sonda. Istekla sonda ostaje u redu (Post se ne može povući), pa bi blokirana
			// UI nit inače skupljala po jednu za svaki heartbeat; sledeći poziv čeka istu dok je UI ne obradi.
			Task<bool>? queued = null;
			var gate = new Lock();
			return cancellationToken => {
				Task<bool> probe;
				lock (gate) {
					if (queued == null || queued.IsCompleted) {
						var operation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
						UiThread.Dispatcher.PostNormal(() => {
							try {
								operation.TrySetResult(statusSource.Current.Revision > 0);
							}
							catch (Exception ex) {
								operation.TrySetException(ex);
							}
						});
						queued = operation.Task;
					}
					probe = queued;
				}
				return probe.WaitAsync(cancellationToken);
			};
		}
	}
}
