using System.Windows.Input;

namespace Flatsoft.Common.UI
{
	public class RelayCommand : ICommand
	{
		private readonly Action<object?> _execute;
		private readonly Func<object?, bool>? _canExecute;

		public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
		{
			_execute = execute ?? throw new ArgumentNullException(nameof(execute));
			_canExecute = canExecute;
		}

		public bool CanExecute(object? parameter)
		{
			return _canExecute == null || _canExecute(parameter);
		}

		public void Execute(object? parameter)
		{
			_execute(parameter);
		}

		// v4: van WPF-a nema CommandManager.RequerySuggested; ko menja uslov poziva RaiseCanExecuteChanged.
		public event EventHandler? CanExecuteChanged;

		public void RaiseCanExecuteChanged()
		{
			UiThread.Raise(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
		}
	}
}
