// Konfiguracija simulatora — ISKLJUČIVO za dev/test. #if DEBUG garantuje da se NE kompajlira u Release
// (nema je u produkcionom assembly-ju ni u produkcionoj konfiguraciji).
#if DEBUG
namespace TopPos.Client.WPF
{
	/// <summary>
	/// Sadržaj <c>app-configuration.Simulator.json</c> (čita ga <c>App.xaml.cs</c>, samo u Debug-u). Fajl ne postoji →
	/// nijedan simulator nije uključen. Sekcija koja postoji uključuje odgovarajući simulator.
	/// Primer: <c>app-configuration.Simulator.json.sample</c>.
	/// </summary>
	public class SimulatorConfiguration
	{
		/// <summary>POS simulator (<see cref="SimulatedPosClient"/>) umesto pravog terminala. Null → pravi PosClient.
		/// <c>PosTerminal.TerminalInstalled</c> i dalje mora biti true.</summary>
		public PosSimulatorConfiguration? Pos { get; set; }
	}

	public class PosSimulatorConfiguration
	{
		// Ishod koji simulator vraća za Sale (vidi PosSimulatorScenario). String + Enum.TryParse,
		// isti obrazac kao ComPort.Parity — default JsonSerializer ne podržava enum-iz-stringa.
		public string Scenario { get; set; } = nameof(PosSimulatorScenario.Approve);

		// Pauza (ms) između simuliranih poruka terminala (PROVUCI KARTICU → OBRADA → …).
		public int StepDelayMs { get; set; } = 1500;
	}

	/// <summary>Ishod koji <see cref="SimulatedPosClient"/> vraća za Sale.</summary>
	public enum PosSimulatorScenario
	{
		Approve,    // uspešna naplata (Success / TransactionAcceptedWithAuthorization)
		Cancel,     // korisnik prekinuo na terminalu (PREKINUTO) -> TransactionCanceled
		Refuse,     // banka odbila (Success / TransactionRefused)
		Error,      // greška terminala / nedostupan (PosError)
		Timeout,    // duga "obrada" pa FunctionTimeout
		Throw       // Sale baci izuzetak (neočekivana greška IPosClient-a)
	}
}
#endif
