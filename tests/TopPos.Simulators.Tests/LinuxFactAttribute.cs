using Xunit;

namespace TopPos.Simulators.Tests
{
	/// <summary>Test samo za Linux; drugde se preskače.</summary>
	public sealed class LinuxFactAttribute : FactAttribute
	{
		public LinuxFactAttribute()
		{
			if (!OperatingSystem.IsLinux()) {
				Skip = "Samo Linux";
			}
		}
	}

	/// <summary>Test samo za Windows; drugde se preskače.</summary>
	public sealed class WindowsFactAttribute : FactAttribute
	{
		public WindowsFactAttribute()
		{
			if (!OperatingSystem.IsWindows()) {
				Skip = "Samo Windows";
			}
		}
	}
}
