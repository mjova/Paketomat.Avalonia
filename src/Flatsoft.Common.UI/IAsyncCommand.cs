// ****************************************************************************
// <author>Stephen Cleary</author>
// <date>04-2014</date>
// <web>https://msdn.microsoft.com/en-us//magazine/dn630647.aspx</web>
// ****************************************************************************


using System.Windows.Input;

namespace Flatsoft.Common.UI
{

    public interface IAsyncCommand : ICommand
    {
        NotifyTaskCompletion Execution { get; }

        Task ExecuteAsync(object? parameter);

        void RaiseCanExecuteChanged();
    }

    public interface IAsyncCommand<TResult> : ICommand
    {
        NotifyTaskCompletion<TResult> Execution { get; }
    }
}