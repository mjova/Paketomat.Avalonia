namespace Flatsoft.Common.UI
{
	/// <summary>
	/// v4: zvuk kioska (v2 AudioPlayer, WPF MediaPlayer). Implementacija je u platformskom sloju; putanja fajla je relativna
	/// prema folderu programa, kao u v2.
	/// </summary>
	public interface IAudioPlayer
	{
		/// <summary>Dodaje fajl u red; fajlovi se puštaju redom, jedan za drugim (kao v2). Nepostojeći fajl se samo loguje.</summary>
		void Play(string audioFileName);

		/// <summary>Zaustavlja zvuk koji se pušta i prazni red.</summary>
		void Reset();
	}
}