using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// v4: test koji opisuje ponašanje samo Windows-a, pa se drugde preskače. Primer: fajl otvoren bez FileShare.Delete na
	/// Windows-u ne može da se zameni ni premesti, a na Linuxu rename uspeva i dok je fajl otvoren (zaključavanje je
	/// savetodavno) — kvar koji test proverava tamo ne postoji.
	/// </summary>
	public sealed class WindowsFactAttribute : FactAttribute
	{
		public WindowsFactAttribute(string reason)
		{
			if (!OperatingSystem.IsWindows()) {
				Skip = $"Samo Windows: {reason}";
			}
		}
	}
}
