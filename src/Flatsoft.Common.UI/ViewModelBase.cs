using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Flatsoft.Common.UI
{
	public abstract class ViewModelBase : INotifyPropertyChanged
	{
		public event PropertyChangedEventHandler? PropertyChanged;

		protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
			=> OnPropertyChanged(new PropertyChangedEventArgs(propertyName));

		// v4: obaveštenje ide na UI nit (v2 kod menja svojstva i sa pozadinskih niti; WPF je to prebacivao sam).
		protected void OnPropertyChanged(PropertyChangedEventArgs args)
			=> UiThread.Raise(() => PropertyChanged?.Invoke(this, args));

		protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
		{
			if (Equals(field, value))
				return false;

			field = value;
			OnPropertyChanged(propertyName);
			return true;
		}
	}
}
