using System;
using System.Threading;
using System.Threading.Tasks;
using static TopPos.Client.WPF.PosClient;

namespace TopPos.Client.WPF
{
	/// <summary>
	/// Apstrakcija nad POS terminalom. Implementacija se bira u kompozicionom korenu
	/// (<c>App.xaml.cs</c>) — isti obrazac kao <c>Locker.ControllerType</c> za lock kontrolere:
	/// <list type="bullet">
	/// <item><see cref="PosClient"/> — pravi ECR/serijski terminal (preko <c>TopPos.ECR</c>).</item>
	/// <item><c>SimulatedPosClient</c> — simulator za dev/test, bez hardvera i bez <c>TopPos.ECR</c>; postoji samo u Debug-u
	/// i uključuje se fajlom <c>app-configuration.Simulator.json</c> (vidi <c>Simulators/</c>).</item>
	/// </list>
	/// Tip vrednosti (<see cref="ResponseCodes"/>, <see cref="TransactionFlagCodes"/>) ostaju ugnežđeni
	/// u <see cref="PosClient"/> i dele se preko <c>using static</c>.
	/// </summary>
	public interface IPosClient
	{
		/// <summary>Da li je poslednja provera prisutnosti terminala bila uspešna.</summary>
		bool IsConnected { get; }

		/// <summary>Proverava da li je terminal dostupan (real: GetTID; sim: uvek true).</summary>
		Task<bool> CheckPresence(CancellationToken cancellationToken);

		/// <summary>
		/// Izvršava prodaju. <paramref name="posTerminalMessagesHandler"/> dobija sirove statusne
		/// poruke terminala (npr. "PROVUCI/UBACI KARTICU", "OBRADA KARTICE", "PREKINUTO").
		/// Vraća (response, transactionFlag, transactionNumber, tidNumber).
		/// </summary>
		ValueTask<(ResponseCodes, TransactionFlagCodes, string?, string?)> Sale(
			decimal amount,
			Func<string, ValueTask>? posTerminalMessagesHandler,
			CancellationToken cancellationToken);
	}
}
