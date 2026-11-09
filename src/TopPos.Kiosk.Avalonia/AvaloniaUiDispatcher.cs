using Avalonia.Threading;
using Flatsoft.Common.UI;

namespace TopPos.Kiosk.UI
{
	/// <summary>
	/// UI nit Avalonie za jezgro (<see cref="UiThread"/>). <see cref="Post"/> i <see cref="Invoke"/> idu istim prioritetom
	/// (Send), pa se redosled poziva iz iste niti čuva i između njih.
	/// </summary>
	internal sealed class AvaloniaUiDispatcher : IUiDispatcher
	{
		public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

		public void Invoke(Action action) => Dispatcher.UIThread.Invoke(action, DispatcherPriority.Send);

		public void Post(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.Send);

		/// <summary>Na UI niti ugnežđena petlja (<see cref="DispatcherFrame"/>) dok se zadatak ne završi; van nje obično čekanje.</summary>
		public void WaitWithMessagePump(Task task)
		{
			if (task.IsCompleted) {
				return;
			}
			if (!CheckAccess()) {
				((IAsyncResult) task).AsyncWaitHandle.WaitOne();
				return;
			}
			var frame = new DispatcherFrame();
			task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false, DispatcherPriority.Send), TaskScheduler.Default);
			try {
				Dispatcher.UIThread.PushFrame(frame);
			}
			catch (InvalidOperationException) {
				// dispečer se gasi ili je obrada zabranjena: obično čekanje (v2: HasShutdownStarted → Wait)
				((IAsyncResult) task).AsyncWaitHandle.WaitOne();
			}
		}
	}
}