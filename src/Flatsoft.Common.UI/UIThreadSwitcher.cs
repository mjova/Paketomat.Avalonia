namespace Flatsoft.Common.UI
{
	public static class UIThreadSwitcher
	{
		// v4: preko UiThread.Dispatcher umesto WPF Application.Current.Dispatcher; isto ponašanje (odmah ili Invoke).
		public static void RunOnUiThread(Action code)
		{
			var dispatcher = UiThread.Dispatcher;
			if (dispatcher.CheckAccess()) {
				code();
			}
			else {
				dispatcher.Invoke(code);
			}
		}
	}
}
