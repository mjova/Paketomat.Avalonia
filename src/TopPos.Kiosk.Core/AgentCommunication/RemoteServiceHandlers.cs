using System.Text.Json;
using TopPos.Agent.Contracts;

namespace TopPos.Kiosk.AgentCommunication
{
	/// <summary>
	/// <see cref="AgentCommands.BeginRemoteService"/> (doc/notes/Info/Ugovor pipe komandi/RemoteService.md): početak servisne
	/// sesije. Uslovi: <see cref="KioskLifecycles.Ready"/>, Start, nema vlasnika; operativnost se ne proverava. Odgovor
	/// <see cref="AgentResultCodes.Completed"/> + <see cref="RemoteServiceResult"/> stiže kad je zapis sesije upisan i mašina
	/// ušla u <c>RemoteService</c>. Ponovljen zahtev se prepoznaje iz dnevnika.
	/// </summary>
	public sealed class BeginRemoteServiceHandler(RemoteServiceManager manager) : IAgentCommandHandler
	{
		public string Command => AgentCommands.BeginRemoteService;

		public bool RemembersRequests => true;

		public string? TryReadPayload(AgentRequest request, out string content)
		{
			content = "";
			var error = RemoteServicePayloads.TryRead<BeginRemoteServicePayload>(request, out var payload);
			if (error != null) {
				return error;
			}
			if (payload!.IdleTimeoutSeconds is < RemoteServiceLimits.MinIdleTimeoutSeconds or > RemoteServiceLimits.MaxIdleTimeoutSeconds) {
				return $"IdleTimeoutSeconds mora biti {RemoteServiceLimits.MinIdleTimeoutSeconds}..{RemoteServiceLimits.MaxIdleTimeoutSeconds}.";
			}
			// JSON null prolazi deserijalizaciju uprkos podrazumevanoj vrednosti "".
			if (payload.PresenceCode is not { } presenceCode
				|| presenceCode.Length is < RemoteServiceLimits.MinPresenceCodeLength or > RemoteServiceLimits.MaxPresenceCodeLength
				|| !presenceCode.All(char.IsAsciiDigit)) {
				return $"PresenceCode mora imati {RemoteServiceLimits.MinPresenceCodeLength}..{RemoteServiceLimits.MaxPresenceCodeLength} cifara.";
			}
			if (payload.Layout == null) {
				return "Layout je obavezan.";
			}
			content = RemoteServiceManager.DescribeBegin(payload);
			return null;
		}

		public AgentCommandResult Execute(AgentRequest request, string content)
		{
			RemoteServicePayloads.TryRead<BeginRemoteServicePayload>(request, out var payload);
			return manager.Begin(request.RequestId, payload!, AgentCommandDispatcher.FingerprintOf(request, content));
		}

		public AgentCommandResult? Repeat(AgentRequest request, string fingerprint) => manager.RepeatBegin(request.RequestId, fingerprint);
	}

	/// <summary>
	/// <see cref="AgentCommands.OpenDoors"/>: otvaranje u servisnoj sesiji. Odmah <see cref="AgentResultCodes.InProgress"/>
	/// (zapis otvaranja je upisan); ishodi kroz status i <see cref="AgentCommands.GetCommandResult"/>. Ponovljen zahtev ne
	/// otvara ponovo — dobija ishod iz dnevnika.
	/// </summary>
	public sealed class OpenDoorsHandler(RemoteServiceManager manager) : IAgentCommandHandler
	{
		public string Command => AgentCommands.OpenDoors;

		public bool RemembersRequests => true;

		public string? TryReadPayload(AgentRequest request, out string content)
		{
			content = "";
			var error = RemoteServicePayloads.TryRead<OpenDoorsPayload>(request, out var payload);
			if (error != null) {
				return error;
			}
			if (string.IsNullOrWhiteSpace(payload!.SessionId)) {
				return "SessionId je obavezan.";
			}
			if (payload.All == (payload.DoorNumbers != null)) {
				return "Zadaje se ili DoorNumbers, ili All.";
			}
			content = $"sesija={payload.SessionId}, " + (payload.All ? "sva vrata" : $"vrata={string.Join(",", payload.DoorNumbers!)}");
			return null;
		}

		public AgentCommandResult Execute(AgentRequest request, string content)
		{
			RemoteServicePayloads.TryRead<OpenDoorsPayload>(request, out var payload);
			return manager.OpenDoors(request.RequestId, payload!, AgentCommandDispatcher.FingerprintOf(request, content));
		}

		public AgentCommandResult? Repeat(AgentRequest request, string fingerprint) => manager.RepeatOpenDoors(request.RequestId, fingerprint);
	}

	/// <summary>
	/// <see cref="AgentCommands.EndRemoteService"/>: serviser je u portalu potvrdio da je zatvorio vrata (pre prijave: odustajanje).
	/// <see cref="AgentResultCodes.Completed"/> + rezultat posle čitanja stanja vrata; dok otvaranje traje
	/// <see cref="AgentResultCodes.Busy"/>. Za već završenu sesiju vraća njen konačni rezultat, pa se zahtev ne pamti.
	/// </summary>
	public sealed class EndRemoteServiceHandler(RemoteServiceManager manager) : IAgentCommandHandler
	{
		public string Command => AgentCommands.EndRemoteService;

		public bool RemembersRequests => true;

		public string? TryReadPayload(AgentRequest request, out string content)
		{
			content = "";
			var error = RemoteServicePayloads.TryRead<EndRemoteServicePayload>(request, out var payload);
			if (error != null) {
				return error;
			}
			if (string.IsNullOrWhiteSpace(payload!.SessionId)) {
				return "SessionId je obavezan.";
			}
			content = $"sesija={payload.SessionId}";
			return null;
		}

		public AgentCommandResult Execute(AgentRequest request, string content)
		{
			RemoteServicePayloads.TryRead<EndRemoteServicePayload>(request, out var payload);
			return manager.End(payload!.SessionId);
		}
	}

	/// <summary>
	/// <see cref="AgentCommands.GetCommandResult"/>: samo čita dnevnik operacija. Bez obaveznog <c>ClientInstanceId</c> i roka,
	/// pa radi i posle restarta kioska; nepoznat ID → <see cref="AgentResultCodes.ResultNotFound"/>.
	/// </summary>
	public sealed class GetCommandResultHandler(RemoteServiceManager manager) : IAgentQueryHandler
	{
		public string Command => AgentCommands.GetCommandResult;

		public AgentCommandResult Handle(AgentRequest request)
		{
			var error = RemoteServicePayloads.TryRead<GetCommandResultPayload>(request, out var payload);
			if (error == null && string.IsNullOrWhiteSpace(payload!.RequestId)) {
				error = "RequestId je obavezan.";
			}
			if (error != null) {
				return new(AgentResultCodes.InvalidRequest, error);
			}
			return manager.GetCommandResult(payload!.RequestId, payload.AfterSequence);
		}
	}

	internal static class RemoteServicePayloads
	{
		public static string? TryRead<T>(AgentRequest request, out T? payload) where T : class
		{
			payload = null;
			if (request.Payload is not { ValueKind: JsonValueKind.Object } json) {
				return "Payload nedostaje ili nije objekat.";
			}
			try {
				payload = json.Deserialize<T>(AgentJson.Options);
			}
			catch (JsonException) {
				return "Payload nije ispravan.";
			}
			return payload == null ? "Payload nije ispravan." : null;
		}
	}
}
