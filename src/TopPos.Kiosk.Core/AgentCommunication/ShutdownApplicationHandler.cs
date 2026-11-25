using System.Text.Json;
using Microsoft.Extensions.Logging;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.Workflow;

namespace TopPos.Kiosk.AgentCommunication
{
	/// <summary>
	/// <see cref="AgentCommands.ShutdownApplication"/>: uredan izlazak iz programa na zahtev Agenta, umesto <c>Kill()</c>
	/// (zahtev: doc/notes/Info/Ugovor pipe komandi/ShutdownApplication.md). Prihvata se samo ako koordinator atomski
	/// dodeli vlasništvo Agentu (Start, mašina ne radi, nema vlasnika); inače odmah odbijanje, bez reda i bez kasnijeg
	/// gašenja. Posle prihvatanja: <see cref="KioskLifecycles.ShuttingDown"/>, odgovor <see cref="AgentResultCodes.Completed"/>,
	/// a tek kad je odgovor upisan (ili veza pukla) — gašenje programa (<c>App.Shutdown</c> → <c>OnExit</c>).
	/// Operativnost se ne proverava: restart se traži i kad hardver ne radi.
	/// </summary>
	public sealed class ShutdownApplicationHandler : IAgentCommandHandler
	{
		private readonly KioskStatusSource _statusSource;
		private readonly Func<string, KioskLease?> _tryBeginAgentOperation;
		private readonly Action _requestShutdown;
		private readonly ILogger? _logger;

		/// <param name="tryBeginAgentOperation">Atomski prijem operacije Agenta (<see cref="KioskOperationCoordinator.TryBeginOperation"/>); argument je opis za log.</param>
		/// <param name="requestShutdown">Pokreće uredno gašenje programa; ne sme da blokira (u aplikaciji: <c>Dispatcher.BeginInvoke(Shutdown)</c>).</param>
		public ShutdownApplicationHandler(KioskStatusSource statusSource, Func<string, KioskLease?> tryBeginAgentOperation, Action requestShutdown, ILogger? logger)
		{
			_statusSource = statusSource;
			_tryBeginAgentOperation = tryBeginAgentOperation;
			_requestShutdown = requestShutdown;
			_logger = logger;
		}

		public string Command => AgentCommands.ShutdownApplication;

		public string? TryReadPayload(AgentRequest request, out string content)
		{
			content = "";
			if (request.Payload is not { ValueKind: JsonValueKind.Object } payload) {
				return "Payload nedostaje ili nije objekat.";
			}
			ShutdownApplicationPayload? parsed;
			try {
				parsed = payload.Deserialize<ShutdownApplicationPayload>(AgentJson.Options);
			}
			catch (JsonException) {
				return "Payload nije ispravan.";
			}
			if (parsed == null || !ShutdownPurposes.All.Contains(parsed.Purpose, StringComparer.Ordinal)) {
				return $"Purpose mora biti jedno od: {string.Join(", ", ShutdownPurposes.All)}.";
			}
			content = parsed.Purpose;
			return null;
		}

		public AgentCommandResult Execute(AgentRequest request, string content)
		{
			var rejection = Rejection(_statusSource.Current);
			if (rejection != null) {
				return rejection;
			}
			if (_tryBeginAgentOperation($"Agent {Command} ({content}, {request.RequestId})") == null) {
				// Stanje se moglo promeniti između provere i prijema (npr. gašenje je upravo počelo).
				var status = _statusSource.Current;
				return Rejection(status) ?? new(AgentResultCodes.Busy, $"Kiosk je zauzet: {status.WorkflowState}, vlasnik {DescribeRole(status.Role)}.");
			}
			// Kiosk ima vlasnika Agenta (novi ulazi se odbijaju); ShuttingDown je konačan i stiže pretplatnicima pre odgovora.
			_statusSource.SetLifecycle(KioskLifecycles.ShuttingDown);
			return new(AgentResultCodes.Completed, "Kiosk se gasi.", written => {
				if (!written) {
					_logger?.LogWarning("Agent pipe: odgovor na {Command} {RequestId} nije stigao do Agenta; gašenje se ipak nastavlja.",
										Command, request.RequestId);
				}
				_logger?.LogInformation("Izlazak iz aplikacije na zahtev Agenta ({Purpose}, {RequestId}).", content, request.RequestId);
				_requestShutdown();
			});
		}

		private static AgentCommandResult? Rejection(KioskStatusSnapshot status) => KioskReadiness.Rejection(status);

		private static string DescribeRole(string role) => KioskReadiness.DescribeRole(role);
	}

	/// <summary>Zajednički razlozi odbijanja komande koja počinje operaciju Agenta (gašenje, oporavak, spremnost).</summary>
	public static class KioskReadiness
	{
		/// <summary>Odbijanje po životnom ciklusu i Idle stanju, ili null kad kiosk može da primi operaciju (zauzetost proverava koordinator).</summary>
		public static AgentCommandResult? Rejection(KioskStatusSnapshot status) => status switch {
			{ Lifecycle: KioskLifecycles.ShuttingDown } => new(AgentResultCodes.ShuttingDown, "Kiosk se već gasi."),
			{ Lifecycle: KioskLifecycles.RecoveryRequired } => new(AgentResultCodes.RecoveryRequired, "Kiosk čeka oporavak."),
			{ Lifecycle: not KioskLifecycles.Ready } => new(AgentResultCodes.NotReady, $"Kiosk još nije spreman ({status.Lifecycle})."),
			{ WorkflowState: WorkflowState.Idle } => new(AgentResultCodes.NotReady, "Kiosk još nije spreman (Idle)."),
			_ => null,
		};

		public static string DescribeRole(string role) => role == "" ? "nema" : role;
	}
}
