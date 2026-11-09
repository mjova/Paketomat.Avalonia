using System.Windows;

namespace Flatsoft.Common.UI
{
	public static class UIThreadSwitcher
	{
        public static void RunOnUiThread(Action code)
        {
            if (Application.Current.Dispatcher.CheckAccess()) {
                code();
            }
            else {
                Application.Current.Dispatcher.Invoke(() =>	code());
            }
        }
	}
}
