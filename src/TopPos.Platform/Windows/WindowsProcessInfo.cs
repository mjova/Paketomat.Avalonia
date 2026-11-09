using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TopPos.Platform.Windows
{
	/// <summary>Creation time iz <c>GetProcessTimes</c> (v2 <c>StatusDeclaration</c>, isti izvor koji čita Agent).</summary>
	[SupportedOSPlatform("windows")]
	internal sealed partial class WindowsProcessInfo : IProcessInfo
	{
		public DateTime GetCurrentProcessStartUtc()
		{
			if (!GetProcessTimes(GetCurrentProcess(), out long creationTime, out _, out _, out _)) {
				throw new Win32Exception(Marshal.GetLastWin32Error(), "GetProcessTimes za tekući proces nije uspeo.");
			}
			return DateTime.FromFileTimeUtc(creationTime);
		}

		// Pseudo-ručka tekućeg procesa; ne zatvara se.
		[LibraryImport("kernel32.dll")]
		private static partial IntPtr GetCurrentProcess();

		[LibraryImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static partial bool GetProcessTimes(IntPtr process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);
	}
}
