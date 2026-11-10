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
}
