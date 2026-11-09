namespace TopPos.Platform
{
	/// <summary>
	/// Folderi kioska (doc/plan.md 1a i 3.5). Relativne putanje iz konfiguracije se razrešavaju prema odgovarajućem folderu,
	/// ne prema radnom folderu procesa (v2 je sve držao uz program).
	/// </summary>
	public interface IAppPaths
	{
		/// <summary>Program i resursi uz njega (jezici, brendovi, zvuk); samo za čitanje.</summary>
		string ProgramDirectory { get; }

		/// <summary><c>app-configuration.json</c>, <c>serilog.json</c>.</summary>
		string ConfigurationDirectory { get; }

		/// <summary><c>status.lock</c>, redovi potvrda, <c>ServerStatus.json</c>, dnevnik operacija Agenta.</summary>
		string StateDirectory { get; }

		/// <summary>Logovi kioska i ECR biblioteke.</summary>
		string LogDirectory { get; }
	}
}