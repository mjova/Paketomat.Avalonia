using System.Diagnostics;
using Flatsoft.Common.UI;
using Microsoft.Extensions.Logging;
using TopPos.LockController;

namespace TopPos.Kiosk
{
	/// <summary>
	/// Zatvaranje veze sa kontrolerom vrata pri urednom izlasku iz aplikacije. NCU klijenti pri Dispose čekaju odgovor
	/// na komandu u toku: ZLG modul, kad mu se veza zatvori dok odgovor još stiže, minutima odbija svaku novu vezu
	/// (teren, paketomat 112, 2026-10-16). Nasilno gašenje procesa (Kill, taskkill /f) ovuda ne prolazi.
	/// </summary>
	public sealed class LockControllerShutdown(ILockControllerClient client, ILogger? logger)
	{
		private readonly Stopwatch _elapsed = Stopwatch.StartNew();

		// Samo UI nit.
		private Task? _closing;
		private bool _finished;

		/// <summary>
		/// Zatvara klijenta; poziva se sa UI niti. Komanda u toku je možda pokrenuta sa UI niti (otvaranje vrata), pa
		/// njen nastavak čeka tu nit: dispečer se zato vrti dok se zatvaranje ne završi. Blokirana UI nit bi zadržala
		/// komandu, a Dispose bi čekao do isteka roka i ipak zatvorio vezu usred odgovora.
		/// Dok se dispečer vrti može da stigne WM_QUERYENDSESSION: ponovljen poziv ne pokreće novo zatvaranje, a vraća se
		/// tek kad se započeto završi.
		/// v4: dispečer se vrti kroz <see cref="IUiDispatcher.WaitWithMessagePump"/> (v2: WPF DispatcherFrame).
		/// </summary>
		public void Close()
		{
			if (client is not IDisposable disposable) {
				return;
			}

			var closing = _closing ??= Task.Run(disposable.Dispose);
			try
			{
				UiThread.Dispatcher.WaitWithMessagePump(closing);

				if (_finished) {
					return;     // ugnežđeni poziv je završio i prijavio ishod; logger je možda već zatvoren
				}
				closing.GetAwaiter().GetResult();
				logger?.LogInformation($"{nameof(LockControllerShutdown)}: veza sa kontrolerom vrata je zatvorena ({_elapsed.ElapsedMilliseconds} ms).");
			}
			catch (Exception ex)
			{
				logger?.LogWarning($"{nameof(LockControllerShutdown)}: zatvaranje veze sa kontrolerom vrata nije uspelo: {ex.Message}");
			}
			_finished = true;
		}
	}
}