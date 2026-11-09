namespace Flatsoft.Common.UI
{
	/// <summary>
	/// UI nit prikaza, bez zavisnosti od UI biblioteke. Prikaz (Avalonia) pri startu postavlja <see cref="UiThread.Dispatcher"/>;
	/// bez toga (testovi, konzola) sve se izvršava odmah, na niti pozivaoca.
	/// </summary>
	public interface IUiDispatcher
	{
		/// <summary>Da li je pozivalac na UI niti.</summary>
		bool CheckAccess();

		/// <summary>Izvršava na UI niti i čeka kraj.</summary>
		void Invoke(Action action);

		/// <summary>Stavlja u red UI niti i ne čeka; redosled poziva iz iste niti se čuva, i u odnosu na <see cref="Invoke"/>.</summary>
		void Post(Action action);

		/// <summary>
		/// Čeka kraj <paramref name="task"/> bez izuzetka (ishod čita pozivalac). Na UI niti za to vreme obrađuje poruke
		/// (ugnežđena petlja, v2 WPF <c>DispatcherFrame</c>), da nastavak koji čeka UI nit ne bi zastao.
		/// </summary>
		void WaitWithMessagePump(Task task);
	}

	/// <summary>
	/// v4: zamena za WPF Application.Current.Dispatcher. ViewModel-i iz v2 menjaju svojstva i sa pozadinskih niti (WPF to
	/// trpi za obična svojstva), pa se obaveštenja prikazu ovde prebacuju na UI nit (<see cref="ViewModelBase"/>, komande).
	/// </summary>
	public static class UiThread
	{
		private sealed class ImmediateDispatcher : IUiDispatcher
		{
			public bool CheckAccess() => true;
			public void Invoke(Action action) => action();
			public void Post(Action action) => action();
			public void WaitWithMessagePump(Task task) => ((IAsyncResult) task).AsyncWaitHandle.WaitOne();
		}

		public static IUiDispatcher Dispatcher { get; set; } = new ImmediateDispatcher();

		/// <summary>Na UI niti odmah, inače u red UI niti (bez čekanja). Za obaveštenja prikazu.</summary>
		public static void Raise(Action action)
		{
			var dispatcher = Dispatcher;
			if (dispatcher.CheckAccess()) {
				action();
			}
			else {
				dispatcher.Post(action);
			}
		}
	}
}