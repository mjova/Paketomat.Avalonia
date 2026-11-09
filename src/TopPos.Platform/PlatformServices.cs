using Flatsoft.Common.UI;
using Microsoft.Extensions.Logging;
using TopPos.Platform.Linux;
using TopPos.Platform.Windows;

namespace TopPos.Platform
{
	/// <summary>Implementacije platformskog sloja za tekući OS. Jedino mesto koje proverava platformu.</summary>
	public static class PlatformServices
	{
		public static IProcessInfo ProcessInfo { get; } = CreateProcessInfo();

		public static IAppPaths AppPaths { get; } = CreateAppPaths();

		public static ISingleInstanceGuard SingleInstanceGuard { get; } = CreateSingleInstanceGuard();

		/// <summary>
		/// Zvuk (<see cref="VlcAudioPlayer"/>); ako LibVLC nije dostupan, kiosk radi bez zvuka, a greška ide u log.
		/// <paramref name="basePath"/> je folder prema kom se razrešavaju relativna imena fajlova.
		/// </summary>
		public static IAudioPlayer CreateAudioPlayer(string basePath, ILogger? logger)
		{
			try {
				return new VlcAudioPlayer(basePath, logger);
			}
			catch (Exception ex) {
				logger?.LogError(ex, "Zvuk: LibVLC nije dostupan; kiosk radi bez zvuka.");
				return new SilentAudioPlayer();
			}
		}

		private static IProcessInfo CreateProcessInfo()
		{
			if (OperatingSystem.IsWindows()) {
				return new WindowsProcessInfo();
			}
			if (OperatingSystem.IsLinux()) {
				return new LinuxProcessInfo();
			}
			throw Unsupported();
		}

		private static IAppPaths CreateAppPaths()
		{
			if (OperatingSystem.IsWindows()) {
				return new WindowsAppPaths();
			}
			if (OperatingSystem.IsLinux()) {
				return new LinuxAppPaths();
			}
			throw Unsupported();
		}

		private static ISingleInstanceGuard CreateSingleInstanceGuard()
		{
			if (OperatingSystem.IsWindows()) {
				return new WindowsSingleInstanceGuard();
			}
			if (OperatingSystem.IsLinux()) {
				return new LinuxSingleInstanceGuard();
			}
			throw Unsupported();
		}

		private static PlatformNotSupportedException Unsupported() => new("Kiosk radi na Windows-u i Linuxu.");

		private sealed class SilentAudioPlayer : IAudioPlayer
		{
			public void Play(string audioFileName) { }
			public void Reset() { }
		}
	}
}