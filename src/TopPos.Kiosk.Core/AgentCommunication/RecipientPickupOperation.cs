using System.Text.Json;
using Microsoft.Extensions.Logging;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.Workflow;

namespace TopPos.Kiosk.AgentCommunication
{
	/// <summary>
	/// Jedno preuzimanje na zahtev Agenta (<see cref="AgentCommands.RecipientPickup"/>, doc/notes/Info/Ugovor pipe
	/// komandi/RecipientPickup.md): faze <see cref="RecipientPickupPhases.Validating"/> → <see cref="RecipientPickupPhases.AwaitingAnswer"/>
	/// → <see cref="RecipientPickupPhases.Opening"/> → <see cref="RecipientPickupPhases.Completed"/>, sa konačnim ishodom.
	///
	/// Stanje se menja sinhrono, pod lock-om (prvi konačan ishod pobeđuje; faza ne ide unazad), a upis u dnevnik ide posle,
	/// pod gate-om, pa dnevnik uvek dobije najnovije stanje poslednje. Posle svakog upisa status dobija novi
	/// <see cref="KioskStatus.OperationSequence"/>, a posle konačnog ishoda operacija se skida iz statusa — Agent tako pita
	/// <see cref="AgentCommands.GetCommandResult"/> tek kad je zapis u dnevniku. Šifra, telefon i kod prisustva nisu u zapisu.
	/// </summary>
	public sealed class RecipientPickupOperation : IRemotePickupProgress
	{
		/// <summary><see cref="KioskStatus.Activity"/> dok preuzimanje traje.</summary>
		public const string Activity = AgentCommands.RecipientPickup;

		private readonly AgentOperationJournal _journal;
		private readonly KioskStatusSource? _status;
		private readonly ILogger? _logger;
		private readonly string _fingerprint;
		private readonly DateTimeOffset _createdUtc;

		private readonly Lock _lock = new();
		private readonly SemaphoreSlim _saveGate = new(1, 1);
		private string _phase = RecipientPickupPhases.Validating;
		private string? _outcome;
		private int? _doorNumber;
		private decimal? _amount;
		private string? _serverMessage;
		private string? _confirmation;
		private int _sequence;
		private Task<bool>? _openingRecorded;
		// Kod prisustva je samo u memoriji: ne ide u dnevnik ni u rezultat.
		private readonly string _presenceCode;
		private int? _failedCodeAttempts;	// null pre AwaitingAnswer
		private bool _codeAccepted;
		private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public RecipientPickupOperation(string requestId, string fingerprint, string presenceCode, AgentOperationJournal journal, KioskStatusSource? status,
										ILogger? logger)
		{
			RequestId = requestId;
			_fingerprint = fingerprint;
			_presenceCode = presenceCode;
			_journal = journal;
			_status = status;
			_logger = logger;
			_createdUtc = journal.UtcNow;
		}

		public string RequestId { get; }

		public string Phase {
			get { lock (_lock) { return _phase; } }
		}

		public bool IsCompleted => Phase == RecipientPickupPhases.Completed;

		/// <summary>
		/// Završava se kad je konačan ishod predat dnevniku i operacija skinuta iz statusa. <see cref="IsCompleted"/> postaje
		/// true ranije (stanje se menja odmah, upis ide posle), a ishod može postaviti i obrada van reda mašine (treći pogrešan
		/// kod), pa kiosk sme da se oslobodi tek posle ovoga.
		/// </summary>
		public Task Finished => _finished.Task;

		public RecipientPickupResult GetResult()
		{
			lock (_lock) {
				return BuildResult();
			}
		}

		/// <summary>Početak: status dobija operaciju, a dnevnik zapis u fazi <see cref="RecipientPickupPhases.Validating"/>. Vraća da li je zapis upisan na disk.</summary>
		public Task<bool> StartAsync()
		{
			_status?.SetOperation(RequestId, Activity);
			lock (_lock) {
				_sequence++;
			}
			return SaveAsync();
		}

		/// <summary>Server je dodelio vrata; ekran traži kod prisustva. Samo iz <see cref="RecipientPickupPhases.Validating"/>.</summary>
		public Task<bool> AwaitAnswerAsync(int doorNumber)
			=> Advance(RecipientPickupPhases.Validating, RecipientPickupPhases.AwaitingAnswer, () => {
				_doorNumber = doorNumber;
				_failedCodeAttempts = 0;
			});

		public int RemainingCodeAttempts {
			get { lock (_lock) { return RecipientPickupLimits.MaxCodeAttempts - (_failedCodeAttempts ?? 0); } }
		}

		/// <summary>
		/// Kod prisustva sa ekrana; unos se loguje u celini. Pogrešan kod se upisuje u dnevnik (<see cref="RecipientPickupResult.FailedCodeAttempts"/>,
		/// novi <see cref="KioskStatus.OperationSequence"/>), a posle <see cref="RecipientPickupLimits.MaxCodeAttempts"/> pogrešnih
		/// unosa ishod je <see cref="RecipientPickupOutcomes.WrongCode"/>. Posle tačnog koda (i van faze
		/// <see cref="RecipientPickupPhases.AwaitingAnswer"/>) svaki unos dobija <see cref="PresenceCodeResult.NotAwaiting"/>.
		/// </summary>
		public async Task<PresenceCodeResult> SubmitPresenceCodeAsync(string code)
		{
			var entered = code.Trim();
			PresenceCodeResult result;
			int failed;
			lock (_lock) {
				if (_phase != RecipientPickupPhases.AwaitingAnswer || _codeAccepted) {
					result = PresenceCodeResult.NotAwaiting;
				}
				else if (string.Equals(entered, _presenceCode, StringComparison.Ordinal)) {
					_codeAccepted = true;
					result = PresenceCodeResult.Accepted;
				}
				else {
					_failedCodeAttempts = (_failedCodeAttempts ?? 0) + 1;
					result = _failedCodeAttempts >= RecipientPickupLimits.MaxCodeAttempts ? PresenceCodeResult.Failed : PresenceCodeResult.Rejected;
					if (result == PresenceCodeResult.Rejected) {
						_sequence++;
					}
				}
				failed = _failedCodeAttempts ?? 0;
			}
			switch (result) {
				case PresenceCodeResult.NotAwaiting:
					_logger?.LogWarning($"{nameof(RecipientPickupOperation)} {RequestId}: kod prisustva {entered} unet kad se kod ne čeka; zanemaren.");
					break;
				case PresenceCodeResult.Accepted:
					_logger?.LogInformation($"{nameof(RecipientPickupOperation)} {RequestId}: tačan kod prisustva {entered}.");
					break;
				case PresenceCodeResult.Rejected:
					_logger?.LogWarning($"{nameof(RecipientPickupOperation)} {RequestId}: pogrešan kod prisustva {entered}, pokušaj {failed} od {RecipientPickupLimits.MaxCodeAttempts}.");
					await SaveAsync().ConfigureAwait(false);
					break;
				default:
					_logger?.LogWarning($"{nameof(RecipientPickupOperation)} {RequestId}: pogrešan kod prisustva {entered}, pokušaj {failed} od {RecipientPickupLimits.MaxCodeAttempts}; pokušaji su iscrpljeni.");
					await CompleteAsync(RecipientPickupOutcomes.WrongCode).ConfigureAwait(false);
					break;
			}
			return result;
		}

		/// <summary>Korisnik je uneo tačan kod; sledi otvaranje. Samo iz <see cref="RecipientPickupPhases.AwaitingAnswer"/>.</summary>
		public Task<bool> BeginOpeningAsync()
		{
			var saved = Advance(RecipientPickupPhases.AwaitingAnswer, RecipientPickupPhases.Opening, null);
			lock (_lock) {
				if (_phase == RecipientPickupPhases.Opening) {
					_openingRecorded ??= saved;
				}
			}
			return saved;
		}

		// Stranica preuzimanja: pre otvaranja čeka upis faze otvaranja (restart posle otvaranja tako daje OutcomeUnknown,
		// ne Interrupted); faza koja nije dostignuta nije ni upisana.
		Task<bool> IRemotePickupProgress.OpeningRecordedAsync()
		{
			lock (_lock) {
				return _openingRecorded ?? Task.FromResult(false);
			}
		}

		Task IRemotePickupProgress.CompleteOpeningAsync(string outcome, string? confirmation) => CompleteAsync(outcome, confirmation: confirmation);

		/// <summary>
		/// Konačan ishod (<see cref="RecipientPickupOutcomes"/>). Prvi pobeđuje: kasniji poziv se zanemaruje i vraća false.
		/// Stanje je promenjeno pre povratka iz poziva; Task se završava kad je zapis predat dnevniku.
		/// </summary>
		public Task<bool> CompleteAsync(string outcome, decimal? amount = null, string? serverMessage = null, string? confirmation = null)
		{
			lock (_lock) {
				if (_phase == RecipientPickupPhases.Completed) {
					_logger?.LogWarning($"{nameof(RecipientPickupOperation)} {RequestId}: ishod {outcome} zanemaren — preuzimanje je već završeno ({_outcome}).");
					return Task.FromResult(false);
				}
				_phase = RecipientPickupPhases.Completed;
				_outcome = outcome;
				_amount = amount;
				_serverMessage = serverMessage;
				_confirmation = confirmation;
				_sequence++;
			}
			_logger?.LogInformation($"{nameof(RecipientPickupOperation)} {RequestId}: završeno — {outcome}" +
									(amount != null ? $", iznos {amount}" : "") + (serverMessage != null ? $", poruka servera „{serverMessage}“" : "") +
									(confirmation != null ? $", potvrda {confirmation}" : "") + ".");
			return SaveAsync();
		}

		/// <summary>
		/// Prekid pre ulaska u workflow (zapis nije upisan, mašina nije ušla u proveru): zapis dobija kod odbijanja, a status
		/// gubi operaciju. Ništa nije pokrenuto, pa nema ni ishoda preuzimanja.
		/// </summary>
		public async Task AbortAsync(string resultCode, string message)
		{
			lock (_lock) {
				_phase = RecipientPickupPhases.Completed;
			}
			if (_journal.TryGet(RequestId, out var record)) {
				await _journal.SaveAsync(record with { ResultCode = resultCode, Message = message }).ConfigureAwait(false);
			}
			_status?.ClearOperation(RequestId);
			_finished.TrySetResult();
		}

		private Task<bool> Advance(string from, string to, Action? apply)
		{
			lock (_lock) {
				if (_phase != from) {
					_logger?.LogWarning($"{nameof(RecipientPickupOperation)} {RequestId}: prelaz u fazu {to} zanemaren — faza je {_phase}.");
					return Task.FromResult(false);
				}
				_phase = to;
				apply?.Invoke();
				_sequence++;
			}
			_logger?.LogInformation($"{nameof(RecipientPickupOperation)} {RequestId}: faza {to}" + (to == RecipientPickupPhases.AwaitingAnswer ? $", vrata {_doorNumber}." : "."));
			return SaveAsync();
		}

		private async Task<bool> SaveAsync()
		{
			await _saveGate.WaitAsync().ConfigureAwait(false);
			try {
				AgentOperationRecord record;
				int sequence;
				bool completed;
				lock (_lock) {
					completed = _phase == RecipientPickupPhases.Completed;
					sequence = _sequence;
					record = new AgentOperationRecord {
						RequestId = RequestId,
						Command = AgentCommands.RecipientPickup,
						Fingerprint = _fingerprint,
						ResultCode = completed ? AgentResultCodes.Completed : AgentResultCodes.InProgress,
						Message = completed ? $"Preuzimanje je završeno ({_outcome})." : null,
						Result = AgentJson.ToResult(BuildResult()),
						CreatedUtc = _createdUtc,
					};
				}
				bool saved;
				try {
					saved = await _journal.SaveAsync(record).ConfigureAwait(false);
				}
				catch (Exception ex) {
					_logger?.LogError(ex, $"{nameof(RecipientPickupOperation)} {RequestId}: upis u dnevnik nije uspeo");
					saved = false;
				}
				if (!saved) {
					_logger?.LogError($"{nameof(RecipientPickupOperation)} {RequestId}: zapis ({record.ResultCode}) nije upisan na disk.");
				}
				_status?.SetOperationSequence(RequestId, sequence);
				if (completed) {
					_status?.ClearOperation(RequestId);
					_finished.TrySetResult();
				}
				return saved;
			}
			finally {
				_saveGate.Release();
			}
		}

		// MORA se pozivati pod _lock.
		private RecipientPickupResult BuildResult() => new() {
			Phase = _phase,
			Outcome = _outcome,
			DoorNumber = _doorNumber,
			Amount = _amount,
			ServerMessage = _serverMessage,
			Confirmation = _confirmation,
			FailedCodeAttempts = _failedCodeAttempts,
		};

		/// <summary>
		/// Oporavak zapisa prekinutog restartom (<see cref="AgentOperationJournal.Initialize"/>): u fazi
		/// <see cref="RecipientPickupPhases.Opening"/> vrata su možda otvorena (<see cref="RecipientPickupOutcomes.OutcomeUnknown"/>),
		/// a pre nje ništa nije otvoreno (<see cref="RecipientPickupOutcomes.Interrupted"/>).
		/// </summary>
		public static AgentOperationRecord RecoverRecord(AgentOperationRecord record)
		{
			var result = record.Result is { ValueKind: JsonValueKind.Object } json ? json.Deserialize<RecipientPickupResult>(AgentJson.Options) : null;
			if (result == null) {
				return record with { ResultCode = AgentResultCodes.OutcomeUnknown, Message = "Program kioska je prekinut usred preuzimanja; zapis nema rezultat." };
			}
			bool opening = result.Phase == RecipientPickupPhases.Opening;
			result.Outcome = opening ? RecipientPickupOutcomes.OutcomeUnknown : RecipientPickupOutcomes.Interrupted;
			result.Phase = RecipientPickupPhases.Completed;
			return record with {
				ResultCode = AgentResultCodes.Completed,
				Message = opening ? "Program kioska je prekinut tokom otvaranja; vrata su možda otvorena."
								  : "Program kioska je prekinut pre otvaranja; ništa nije otvoreno.",
				Result = AgentJson.ToResult(result),
			};
		}
	}
}