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

	/// <summary>
	/// v4: test koji traži običnog korisnika (npr. klijent kanala koji nije root se odbija); kao root na Linuxu se preskače.
	/// </summary>
	public sealed class NotRootFactAttribute : FactAttribute
	{
		public NotRootFactAttribute()
		{
			if (!OperatingSystem.IsWindows() && Environment.UserName == "root") {
				Skip = "Test radi kao root";
			}
		}

		/// <summary>Test i samo na Linuxu (npr. Unix socket).</summary>
		public bool LinuxOnly {
			get => false;
			init {
				if (value && !OperatingSystem.IsLinux()) {
					Skip = "Samo Linux";
				}
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