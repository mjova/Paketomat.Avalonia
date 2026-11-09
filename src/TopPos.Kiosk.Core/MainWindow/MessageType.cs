namespace TopPos.Client.WPF.ViewModels
{
	/// <summary>
	/// Tip poruke koja se prikazuje korisniku (utiče na ikonu/boju). Ranije nested u
	/// <see cref="MainWindowViewModel"/>; izdvojeno na namespace nivo da bi ga koristile
	/// i activator klase (Faza 5) bez kvalifikacije.
	/// </summary>
	public enum MessageType { Success, Error, Information }
}
