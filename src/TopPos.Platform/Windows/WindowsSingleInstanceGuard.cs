using System.Runtime.Versioning;

namespace TopPos.Platform.Windows
{
	/// <summary>
	/// Imenovani mutex, isto ime i opseg (sesija) kao v2 — v2 i v4 na istom računaru tako ne rade istovremeno.
	/// </summary>
	[SupportedOSPlatform("windows")]
	internal sealed class WindowsSingleInstanceGuard(string mutexName = WindowsSingleInstanceGuard.V2MutexName) : ISingleInstanceGuard
	{
		public const string V2MutexName = "TopPos.Client.WPF.SingleInstance";

		public IDisposable? TryAcquire()
		{
			var mutex = new Mutex(true, mutexName, out bool createdNew);
			if (createdNew) {
				return mutex;   // handle se zatvara pri izlasku; kao u v2, mutex se ne oslobađa eksplicitno
			}
			mutex.Dispose();
			return null;
		}
	}
}