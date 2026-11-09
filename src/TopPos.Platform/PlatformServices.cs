using TopPos.Platform.Linux;
using TopPos.Platform.Windows;

namespace TopPos.Platform
{
	/// <summary>Implementacije platformskog sloja za tekući OS. Jedino mesto koje proverava platformu.</summary>
	public static class PlatformServices
	{
		public static IProcessInfo ProcessInfo { get; } =
			OperatingSystem.IsWindows() ? new WindowsProcessInfo()
			: OperatingSystem.IsLinux() ? new LinuxProcessInfo()
			: throw new PlatformNotSupportedException("Kiosk radi na Windows-u i Linuxu.");
	}
}