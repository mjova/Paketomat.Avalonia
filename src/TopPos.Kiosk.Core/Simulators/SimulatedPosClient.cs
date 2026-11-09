// POS simulator — ISKLJUČIVO za dev/test. #if DEBUG garantuje da se NE kompajlira u Release
// (nema ga u produkcionom assembly-ju). Isti obrazac kao F11 barcode simulator.
#if DEBUG
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using static TopPos.Client.WPF.PosClient;

namespace TopPos.Client.WPF
{
	/// <summary>
	/// Simulator POS terminala za dev/test okruženje — bez pravog hardvera i bez <c>TopPos.ECR</c>.
	/// Bira se u <c>App.xaml.cs</c> kada <c>app-configuration.Simulator.json</c> ima sekciju <c>Pos</c>
	/// (<see cref="SimulatorConfiguration"/>).
	/// <para>
	/// Reprodukuje isti niz callback poruka kao pravi <see cref="PosClient"/>
	/// (PROVUCI/UBACI KARTICU → OBRADA KARTICE → …) i vraća isti tuple, a ishod određuje
	/// <c>Pos.Scenario</c> (<see cref="PosSimulatorScenario"/>). Tako se ceo
	/// recipient payment tok — uključujući prekid (PREKINUTO), odbijanje i timeout — može testirati
	/// bez terminala.
	/// </para>
	/// </summary>
	public sealed class SimulatedPosClient : IPosClient
	{
		private readonly int _stepDelayMs;
		private readonly ILogger? _logger;
		private int _transactionCounter;

		/// <summary>Simulator je uvek "povezan".</summary>
		public bool IsConnected { get; private set; } = true;

		/// <summary>
		/// Ishod koji sledeća <see cref="Sale"/> vraća. Inicijalno iz configa
		/// (<c>Pos.Scenario</c>), ali se može menjati u toku rada
		/// (DEBUG hotkey F10 — vidi MainWindow). Čita se na početku svake transakcije.
		/// </summary>
		public PosSimulatorScenario Scenario { get; set; }

		public SimulatedPosClient(PosSimulatorConfiguration config, ILogger? logger)
		{
			Scenario = Enum.TryParse<PosSimulatorScenario>(config.Scenario, ignoreCase: true, out var scenario)
							? scenario : PosSimulatorScenario.Approve;
			_stepDelayMs = config.StepDelayMs > 0 ? config.StepDelayMs : 1500;
			_logger = logger;
		}

		public Task<bool> CheckPresence(CancellationToken cancellationToken)
		{
			IsConnected = true;
			_logger?.LogInformation($"{nameof(SimulatedPosClient)}: CheckPresence -> true (scenario={Scenario}).");
			return Task.FromResult(true);
		}

		public async ValueTask<(ResponseCodes, TransactionFlagCodes, string?, string?)> Sale(
			decimal amount, Func<string, ValueTask>? posTerminalMessagesHandler, CancellationToken cancellationToken)
		{
			var scenario = Scenario;   // stabilno tokom jedne transakcije (može se promeniti F10 hotkey-om)
			_logger?.LogInformation($"{nameof(SimulatedPosClient)}: Sale(amount={amount}) start, scenario={scenario}.");

			// Lokalni helper: loguje i prosleđuje sirovu poruku terminala (kao EcrMessageReceived kod pravog klijenta).
			async ValueTask Emit(string message)
			{
				_logger?.LogInformation($"{nameof(SimulatedPosClient)}: poruka terminala: {message}");
				if (posTerminalMessagesHandler != null) {
					await posTerminalMessagesHandler(message).ConfigureAwait(false);
				}
			}

			try {
				await Emit("PROVUCI/UBACI KARTICU").ConfigureAwait(false);
				await Task.Delay(_stepDelayMs, cancellationToken).ConfigureAwait(false);
				await Emit("OBRADA KARTICE").ConfigureAwait(false);
				await Task.Delay(_stepDelayMs, cancellationToken).ConfigureAwait(false);

				switch (scenario) {
					case PosSimulatorScenario.Cancel:
						// Korisnik prekinuo na terminalu — ista poruka koju pravi terminal pošalje pre prekida.
						await Emit(PosClient.CanceledByUserMessage).ConfigureAwait(false);   // "PREKINUTO"
						_logger?.LogInformation($"{nameof(SimulatedPosClient)}: Sale -> TransactionCanceled (korisnik prekinuo).");
						return (ResponseCodes.TransactionCanceled, TransactionFlagCodes.NotValidValue, null, null);

					case PosSimulatorScenario.Refuse:
						// Banka odbila naplatu.
						_logger?.LogInformation($"{nameof(SimulatedPosClient)}: Sale -> Success/TransactionRefused (banka odbila).");
						return (ResponseCodes.Success, TransactionFlagCodes.TransactionRefused, null, null);

					case PosSimulatorScenario.Error:
						// Greška u radu terminala / nedostupan.
						_logger?.LogInformation($"{nameof(SimulatedPosClient)}: Sale -> PosError (greška terminala).");
						return (ResponseCodes.PosError, TransactionFlagCodes.NotValidValue, null, null);

					case PosSimulatorScenario.Timeout:
						// Dugačka "obrada" pa timeout — za testiranje sporog terminala i Home-dugme race-a.
						await Emit("MOLIM SACEKAJ").ConfigureAwait(false);
						await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
						_logger?.LogInformation($"{nameof(SimulatedPosClient)}: Sale -> FunctionTimeout.");
						return (ResponseCodes.FunctionTimeout, TransactionFlagCodes.NotValidValue, null, null);

					case PosSimulatorScenario.Throw:
						// Neočekivan izuzetak iz IPosClient-a — za proveru da kiosk ne ostane zaglavljen na ekranu plaćanja.
						_logger?.LogInformation($"{nameof(SimulatedPosClient)}: Sale -> izuzetak (simulirano).");
						throw new InvalidOperationException("Simuliran izuzetak iz POS klijenta.");

					case PosSimulatorScenario.Approve:
					default:
						await Emit("MOLIM SACEKAJ").ConfigureAwait(false);
						await Task.Delay(_stepDelayMs, cancellationToken).ConfigureAwait(false);
						var txNumber = (++_transactionCounter).ToString("D6");
						const string tid = "SIMUL001";
						_logger?.LogInformation($"{nameof(SimulatedPosClient)}: Sale -> Success/Approved (txNum={txNumber}, tid={tid}).");
						return (ResponseCodes.Success, TransactionFlagCodes.TransactionAcceptedWithAuthorization, txNumber, tid);
				}
			}
			catch (OperationCanceledException) {
				// Otkazano spolja (cancellation token) — tretiramo kao prekid transakcije.
				_logger?.LogInformation($"{nameof(SimulatedPosClient)}: Sale otkazan (CancellationToken) -> TransactionCanceled.");
				return (ResponseCodes.TransactionCanceled, TransactionFlagCodes.NotValidValue, null, null);
			}
		}
	}
}
#endif
