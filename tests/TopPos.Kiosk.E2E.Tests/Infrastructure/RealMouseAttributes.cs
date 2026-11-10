using Xunit;

namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>
	/// v4: E2E test sa pravim mišem (Home, dug pritisak). Na Linuxu ga UI ne podržava (<see cref="AtSpiKioskUi"/>: AT-SPI nema
	/// klik mišem, Wayland ne dozvoljava ubacivanje), pa se preskače bez pokretanja sesije.
	/// </summary>
	public sealed class RealMouseFactAttribute : SkippableFactAttribute
	{
		public RealMouseFactAttribute()
		{
			if (OperatingSystem.IsLinux()) {
				Skip = RealMouse.SkipReason;
			}
		}
	}

	/// <inheritdoc cref="RealMouseFactAttribute"/>
	public sealed class RealMouseTheoryAttribute : SkippableTheoryAttribute
	{
		public RealMouseTheoryAttribute()
		{
			if (OperatingSystem.IsLinux()) {
				Skip = RealMouse.SkipReason;
			}
		}
	}

	internal static class RealMouse
	{
		public const string SkipReason = "Pravi miš (Home, dug pritisak) na Linuxu nije podržan u E2E (AT-SPI nema klik mišem, Wayland ne dozvoljava ubacivanje).";
	}
}
