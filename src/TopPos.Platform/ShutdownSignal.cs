using System.Runtime.InteropServices;

namespace TopPos.Platform
{
	/// <summary>
	/// Zahtev sistema za gašenje procesa: SIGTERM (systemd stop, <c>TimeoutStopSec</c>), SIGINT (Ctrl+C), SIGQUIT. Podrazumevano
	/// gašenje runtime-a se otkazuje: pozivalac sam radi uredan izlazak (završni upis u <c>status.lock</c>, zatvaranje veze sa
	/// kontrolerom), kao v2 na kraju Windows sesije. Poziva se na niti signala i može doći više puta.
	/// Na Windows-u GUI proces ove signale ne dobija; kraj Windows sesije (<c>WM_QUERYENDSESSION</c>) javlja prikaz.
	/// </summary>
	public sealed class ShutdownSignal : IDisposable
	{
		private readonly PosixSignalRegistration[] _registrations;

		public ShutdownSignal(Action<PosixSignal> onShutdownRequested)
		{
			_registrations = new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT }
				.Select(signal => PosixSignalRegistration.Create(signal, context => {
					context.Cancel = true;
					onShutdownRequested(context.Signal);
				}))
				.ToArray();
		}

		public void Dispose()
		{
			foreach (var registration in _registrations) {
				registration.Dispose();
			}
		}
	}
}