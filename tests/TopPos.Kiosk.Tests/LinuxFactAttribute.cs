using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>v4: test Linux implementacije platformskog sloja; drugde se preskače.</summary>
	public sealed class LinuxFactAttribute : FactAttribute
	{
		public LinuxFactAttribute()
		{
			if (!OperatingSystem.IsLinux()) {
				Skip = "Samo Linux";
			}
		}
	}

	/// <summary>v4: kao <see cref="LinuxFactAttribute"/>, za testove sa podacima.</summary>
	public sealed class LinuxTheoryAttribute : TheoryAttribute
	{
		public LinuxTheoryAttribute()
		{
			if (!OperatingSystem.IsLinux()) {
				Skip = "Samo Linux";
			}
		}
	}
}