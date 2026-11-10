using Xunit;

namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>
	/// v4: E2E test za tok koji v4 kiosk još nema (kurir, pošiljalac, istekli paketi — F3). Ostaje u fajlu prenetom iz v2
	/// (fajl ostaje uporediv sa v2, sync skripta) i preskače se bez pokretanja sesije. Kad tok stigne, atribut se vraća na
	/// <c>[SkippableFact]</c>.
	/// </summary>
	public sealed class PendingFlowAttribute : FactAttribute
	{
		public PendingFlowAttribute(string flow, string phase = "F3")
		{
			Skip = $"Tok \"{flow}\" još nije u v4 kiosku ({phase}).";
		}
	}
}
