// ****************************************************************************
// <author>Stephen Cleary</author>
// <date>04-2014</date>
// <web>https://msdn.microsoft.com/en-us//magazine/dn630647.aspx</web>
// ****************************************************************************

namespace Flatsoft.Common.UI
{
	public abstract class AsyncCommandBase
    {
        public abstract bool CanExecute(object? parameter = null);

        public abstract Task ExecuteAsync(object? parameter);

        public async void Execute(object? parameter)
        {
            await ExecuteAsync(parameter);
        }

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged()
        {
            UiThread.Raise(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));	// v4: na UI nit
        }
    }
}