using System.Text.Json;
using Microsoft.Extensions.Logging;
using TopPos.Agent.Contracts;

namespace TopPos.Kiosk.AgentCommunication
{
	/// <summary>Ko je poslao zahtev (za log): veza i klijent proveren u handshake-u.</summary>
	public sealed record AgentPeer(int ConnectionId, string ClientSid, int? ProcessId);

	/// <summary>
	/// Ishod komande. <paramref name="AfterResponse"/> se izvršava posle pokušaja upisa odgovora u pipe (<c>true</c> =
	/// upisan, <c>false</c> = veza pukla ili upis nije stigao u roku), van niti veze; mora biti kratak i ne sme da baci izuzetak.
	/// </summary>
	public sealed record AgentCommandResult(string ResultCode, string? Message, Action<bool>? AfterResponse = null)
	{
		/// <summary><see cref="AgentResponse.Result"/>.</summary>
		public JsonElement? Result { get; init; }

		/// <summary>
		/// Odgovor koji čeka posao započet pri prijemu (upis u dnevnik, čitanje vrata): odluka je doneta pod lock-om
		/// dispatcher-a, a odgovor se šalje kad se <see cref="Pending"/> završi. <see cref="ResultCode"/> je tada samo
		/// privremen (<see cref="AgentResultCodes.InProgress"/>).
		/// </summary>
		public Task<AgentCommandResult>? Pending { get; init; }

		public static AgentCommandResult Deferred(Task<AgentCommandResult> pending) => new(AgentResultCodes.InProgress, null) { Pending = pending };
	}

	/// <summary>Handler jedne komande koja menja stanje kioska (<see cref="AgentCommandDispatcher"/>).</summary>
	public interface IAgentCommandHandler
	{
		/// <summary><see cref="AgentCommands"/>.</summary>
		string Command { get; }

		/// <summary>
		/// Čita i proverava payload. Vraća poruku greške (zahtev se odbija sa <see cref="AgentResultCodes.InvalidRequest"/>)
		/// ili null; <paramref name="content"/> je normalizovan sadržaj za poređenje ponovljenog zahteva i za log.
		/// </summary>
		string? TryReadPayload(AgentRequest request, out string content);

		/// <summary>
		/// Odluka pri prijemu (plan §5): odmah izvršenje ili odbijanje, bez reda. Poziva se pod lock-om dispatcher-a, posle
		/// zajedničkih provera — mora biti kratak i neblokirajući.
		/// </summary>
		AgentCommandResult Execute(AgentRequest request, string content);

		/// <summary>
		/// Handler sam pamti prihvaćene zahteve (trajni dnevnik, <see cref="AgentOperationJournal"/>): dispatcher tada ne pamti
		/// njegove ishode u memoriji, nego ponovljen <see cref="AgentRequest.RequestId"/> pita <see cref="Repeat"/>.
		/// </summary>
		bool RemembersRequests => false;

		/// <summary>
		/// Ponovljen zahtev (<see cref="RemembersRequests"/>): raniji ishod, <see cref="AgentResultCodes.DuplicateConflict"/>
		/// za drugačiji sadržaj, ili null kad ID nije poznat. Pod lock-om dispatcher-a, pre provere isteka.
		/// </summary>
		AgentCommandResult? Repeat(AgentRequest request, string fingerprint) => null;
	}

	/// <summary>
	/// Komanda koja samo čita (npr. <see cref="AgentCommands.GetCommandResult"/>): bez obaveznog
	/// <see cref="AgentRequest.ClientInstanceId"/> i roka, bez pamćenja <see cref="AgentRequest.RequestId"/>, pa radi i posle
	/// restarta kioska.
	/// </summary>
	public interface IAgentQueryHandler
	{
		/// <summary><see cref="AgentCommands"/>.</summary>
		string Command { get; }

		/// <summary>Odgovor odmah; kratak i neblokirajući (poziva se na niti veze).</summary>
		AgentCommandResult Handle(AgentRequest request);
	}

	/// <summary>
	/// Komande koje menjaju stanje kioska (plan §5, „Pravilo za sve komande koje menjaju stanje kioska“). Jedan za ceo
	/// server, pa pamćenje prihvaćenih zahteva važi za sve veze iste instance (Agent sme da ponovi zahtev posle prekida
	/// veze). Statusne komande (<see cref="AgentCommands.GetStatus"/>, <see cref="AgentCommands.SubscribeStatus"/>) su
	/// vezane za vezu i ostaju u <see cref="AgentPipeConnection"/>.
	///
	/// Provere pre handlera, redom: obavezan <see cref="AgentRequest.ClientInstanceId"/> (pogrešan već odbija veza),
	/// rokovi zahteva, payload, ponovljen <see cref="AgentRequest.RequestId"/>, istek. Pamte se samo prihvaćeni zahtevi
	/// (<see cref="AgentResultCodes.Completed"/>), u memoriji: odbijen zahtev ne ostavlja trag, pa isti ID posle odbijanja
	/// prolazi novu proveru. Svaki ishod ide u log kioska.
	/// </summary>
	public sealed class AgentCommandDispatcher
	{
		private static readonly TimeSpan __maxTtl = TimeSpan.FromSeconds(AgentProtocol.MaxRequestTtlSeconds);
		private static readonly TimeSpan __clockSkew = TimeSpan.FromSeconds(AgentProtocol.MaxClockSkewSeconds);

		private readonly object _lock = new();
		private readonly Dictionary<string, IAgentCommandHandler> _handlers;
		private readonly Dictionary<string, IAgentQueryHandler> _queries;
		private readonly TimeProvider _timeProvider;
		private readonly ILogger? _logger;
		// RequestId → sadržaj i ishod prihvaćenog zahteva.
		private readonly Dictionary<string, (string Fingerprint, AgentCommandResult Result)> _accepted = [];

		public AgentCommandDispatcher(IEnumerable<IAgentCommandHandler> handlers, ILogger? logger, TimeProvider? timeProvider = null,
									  IEnumerable<IAgentQueryHandler>? queries = null)
		{
			_handlers = handlers.ToDictionary(h => h.Command, StringComparer.Ordinal);
			_queries = (queries ?? []).ToDictionary(q => q.Command, StringComparer.Ordinal);
			_logger = logger;
			_timeProvider = timeProvider ?? TimeProvider.System;
		}

		/// <summary>Komande koje dispatcher izvršava (deo <see cref="ServerHello.SupportedCommands"/>).</summary>
		public IReadOnlyList<string> Commands => [.. _handlers.Keys, .. _queries.Keys];

		/// <summary>
		/// Ishod zahteva, ili null ako dispatcher ne zna komandu (veza tada odgovara <see cref="AgentResultCodes.UnsupportedCommand"/>).
		/// Zahtev je već prošao opšte provere veze (protokol, ID, paketomat, instanca ako je poslata).
		/// </summary>
		public AgentCommandResult? Dispatch(AgentRequest request, AgentPeer peer)
		{
			if (_queries.TryGetValue(request.Command, out var query)) {
				return Query(request, peer, query);
			}
			if (!_handlers.TryGetValue(request.Command, out var handler)) {
				return null;
			}
			string content = "";
			var result = Validate(request, handler, ref content) ?? Decide(request, handler, content);
			if (result.Pending is { } pending) {
				return result with { Pending = LogWhenDone(request, peer, content, pending) };
			}
			Log(request, peer, content, result);
			return result;
		}

		// Odgovor posle posla započetog pri prijemu: u log ide konačan ishod; izuzetak posla → InternalError.
		private async Task<AgentCommandResult> LogWhenDone(AgentRequest request, AgentPeer peer, string content, Task<AgentCommandResult> pending)
		{
			AgentCommandResult result;
			try {
				result = await pending.ConfigureAwait(false);
			}
			catch (Exception ex) {
				_logger?.LogError(ex, "Agent pipe [{ConnectionId}]: {Command} {RequestId} završen greškom.",
								  peer.ConnectionId, request.Command, AgentLogText.Sanitize(request.RequestId));
				result = Rejected(AgentResultCodes.InternalError, "Greška kioska pri izvršavanju komande.");
			}
			Log(request, peer, content, result);
			return result;
		}

		private AgentCommandResult Query(AgentRequest request, AgentPeer peer, IAgentQueryHandler query)
		{
			AgentCommandResult result;
			try {
				result = query.Handle(request);
			}
			catch (Exception ex) {
				_logger?.LogError(ex, "Agent pipe [{ConnectionId}]: {Command} {RequestId} završen greškom.",
								  peer.ConnectionId, request.Command, AgentLogText.Sanitize(request.RequestId));
				result = Rejected(AgentResultCodes.InternalError, "Greška kioska pri čitanju.");
			}
			// Agent pita posle svake promene statusa operacije: ishod ide u Debug, da ne zatrpa log.
			_logger?.LogDebug("Agent pipe [{ConnectionId}]: {Command} {RequestId} → {ResultCode}: {Message}",
							  peer.ConnectionId, request.Command, AgentLogText.Sanitize(request.RequestId), result.ResultCode, result.Message);
			return result;
		}

		private AgentCommandResult? Validate(AgentRequest request, IAgentCommandHandler handler, ref string content)
		{
			if (string.IsNullOrEmpty(request.ClientInstanceId)) {
				return Rejected(AgentResultCodes.InvalidRequest, "ClientInstanceId je obavezan za komandu koja menja stanje kioska.");
			}
			if (request.IssuedAtUtc is not { } issued || request.ExpiresAtUtc is not { } expires) {
				return Rejected(AgentResultCodes.InvalidRequest, "IssuedAtUtc i ExpiresAtUtc su obavezni.");
			}
			if (expires <= issued || expires - issued > __maxTtl) {
				return Rejected(AgentResultCodes.InvalidRequest, $"Rok zahteva mora biti duži od 0 i najviše {AgentProtocol.MaxRequestTtlSeconds} s.");
			}
			if (issued > _timeProvider.GetUtcNow() + __clockSkew) {
				return Rejected(AgentResultCodes.InvalidRequest, $"IssuedAtUtc je više od {AgentProtocol.MaxClockSkewSeconds} s u budućnosti.");
			}
			var payloadError = handler.TryReadPayload(request, out content);
			return payloadError != null ? Rejected(AgentResultCodes.InvalidRequest, payloadError) : null;
		}

		/// <summary>Otisak zahteva za poređenje ponovljenog <see cref="AgentRequest.RequestId"/>: komanda, sadržaj i rokovi.</summary>
		public static string FingerprintOf(AgentRequest request, string content)
			=> $"{request.Command}|{content}|{request.IssuedAtUtc:O}|{request.ExpiresAtUtc:O}";

		private AgentCommandResult Decide(AgentRequest request, IAgentCommandHandler handler, string content)
		{
			var fingerprint = FingerprintOf(request, content);
			lock (_lock) {
				// Ponovljen zahtev pre roka: istekao duplikat dobija raniji ishod, ali ne novo izvršavanje (plan §5).
				if (Repeated(request, handler, fingerprint) is { } repeated) {
					return repeated with { AfterResponse = null };
				}
				if (_timeProvider.GetUtcNow() > request.ExpiresAtUtc!.Value + __clockSkew) {
					return Rejected(AgentResultCodes.Expired, "Rok zahteva je istekao.");
				}
				var result = handler.Execute(request, content);
				if (result.ResultCode == AgentResultCodes.Completed && result.Pending == null && !handler.RemembersRequests) {
					_accepted[request.RequestId] = (fingerprint, result);
				}
				return result;
			}
		}

		/// <summary>
		/// Raniji ishod istog <see cref="AgentRequest.RequestId"/> ili null. ID važi za sve komande, pa se traži u oba izvora
		/// prihvaćenih zahteva: u memoriji (<see cref="_accepted"/>) i u dnevniku handler-a koji pamte. ID prihvaćen za drugu
		/// komandu → <see cref="AgentResultCodes.DuplicateConflict"/> (ranije: npr. <c>ShutdownApplication</c> sa ID-em završene
		/// servisne sesije zakazivao je gašenje). Pod <see cref="_lock"/>-om.
		/// </summary>
		private AgentCommandResult? Repeated(AgentRequest request, IAgentCommandHandler handler, string fingerprint)
		{
			if (_accepted.TryGetValue(request.RequestId, out var earlier)) {
				return earlier.Fingerprint == fingerprint
						? earlier.Result
						: Rejected(AgentResultCodes.DuplicateConflict, "Isti RequestId je već prihvaćen sa drugačijim sadržajem.");
			}
			if (handler.RemembersRequests && handler.Repeat(request, fingerprint) is { } repeated) {
				return repeated;
			}
			// Dnevnik drugih komandi: ID koji tamo postoji pripada drugoj komandi (Repeat poredi i komandu).
			foreach (var other in _handlers.Values) {
				if (other != handler && other.RemembersRequests && other.Repeat(request, fingerprint) is not null) {
					return Rejected(AgentResultCodes.DuplicateConflict, "Isti RequestId je već prihvaćen za drugu komandu.");
				}
			}
			return null;
		}

		private static AgentCommandResult Rejected(string resultCode, string message) => new(resultCode, message);

		private void Log(AgentRequest request, AgentPeer peer, string content, AgentCommandResult result)
		{
			var level = result.ResultCode is AgentResultCodes.Completed or AgentResultCodes.InProgress ? LogLevel.Information : LogLevel.Warning;
			_logger?.Log(level, "Agent pipe [{ConnectionId}]: {Command} {RequestId} (klijent {ClientSid}, PID {ProcessId}, {Content}, razlog „{Reason}“) → {ResultCode}: {Message}",
						peer.ConnectionId, request.Command, AgentLogText.Sanitize(request.RequestId), peer.ClientSid, peer.ProcessId,
						AgentLogText.Sanitize(content), AgentLogText.Sanitize(request.Reason, 200), result.ResultCode, result.Message);
		}
	}
}
