namespace Flatsoft.Common.UI
{
	/// <summary>
	/// v4: zvuk kioska (v2 AudioPlayer, WPF MediaPlayer). Implementacija je u platformskom sloju; putanja fajla je relativna
	/// prema folderu programa, kao u v2.
	/// </summary>
	public interface IAudioPlayer
	{
		/// <summary>Pušta fajl; prethodni zvuk se prekida.</summary>
		void Play(string audioFileName);

		/// <summary>Zaustavlja zvuk koji se pušta.</summary>
		void Reset();
	}
}
