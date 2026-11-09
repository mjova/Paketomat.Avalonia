using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TopPos.Platform.Linux
{
	/// <summary>
	/// LibVLCSharp učitava biblioteku kao „libvlc“ (i „libvlccore“), a Debian/Ubuntu paket <c>libvlc5</c> donosi samo
	/// <c>libvlc.so.5</c> i <c>libvlccore.so.9</c> — nesufiksiran <c>libvlc.so</c> je u razvojnom paketu <c>libvlc-dev</c>.
	/// Resolver usmerava ta imena na sufiksirane verzije, pa kiosku na uređaju treba samo <c>libvlc5</c> + <c>vlc-plugin-base</c>
	/// (Debian 12, 9.11.2026).
	/// </summary>
	[SupportedOSPlatform("linux")]
	internal static class LinuxLibVlc
	{
		private static int __registered;

		/// <summary>Registruje resolver jednom po procesu; baca izuzetak ako sklop LibVLCSharp već ima svoj.</summary>
		public static void RegisterResolver()
		{
			if (Interlocked.Exchange(ref __registered, 1) != 0) {
				return;
			}
			NativeLibrary.SetDllImportResolver(typeof(LibVLCSharp.Shared.Core).Assembly, Resolve);
		}

		internal static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
		{
			string[]? candidates = libraryName switch {
				"libvlc" => ["libvlc.so.5", "libvlc.so"],
				"libvlccore" => ["libvlccore.so.9", "libvlccore.so"],
				_ => null
			};
			if (candidates != null) {
				foreach (var candidate in candidates) {
					if (NativeLibrary.TryLoad(candidate, out var handle)) {
						return handle;
					}
				}
			}
			return IntPtr.Zero;     // podrazumevana pretraga runtime-a
		}
	}
}