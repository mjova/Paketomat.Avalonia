using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TopPos.Agent.Contracts;

namespace TopPos.Kiosk.AgentCommunication
{
	/// <summary>
	/// Zapis jedne operacije Agenta u <see cref="AgentOperationJournal"/>. <see cref="Result"/> je rezultat iz ugovora
	/// (npr. <see cref="RemoteServiceResult"/>) u obliku u kome ide u <see cref="AgentResponse.Result"/>.
	/// </summary>
	public sealed record AgentOperationRecord
	{
		public required string RequestId { get; init; }

		/// <summary><see cref="AgentCommands"/>.</summary>
		public required string Command { get; init; }

		/// <summary>Normalizovan sadržaj zahteva, za poređenje ponovljenog <see cref="RequestId"/>.</summary>
		public string Fingerprint { get; init; } = "";

		/// <summary><see cref="AgentResultCodes.InProgress"/> dok operacija traje, zatim konačan kod.</summary>
		public string ResultCode { get; init; } = AgentResultCodes.InProgress;

		public string? Message { get; init; }

		public JsonElement? Result { get; init; }

		public DateTimeOffset CreatedUtc { get; init; }

		public DateTimeOffset UpdatedUtc { get; init; }

		public bool IsFinished => ResultCode != AgentResultCodes.InProgress;
	}

	/// <summary>
	/// Trajni dnevnik operacija Agenta (kiosk plan §5, „Ponovljeni zahtevi i prekid procesa"): ishodi za
	/// <see cref="AgentCommands.GetCommandResult"/>, ponovljen <see cref="AgentRequest.RequestId"/> i oporavak posle pada.
	///
	/// Jedan fajl po operaciji u folderu dnevnika, pa je upis mali i oštećenje jednog fajla ne dira ostale. Upis ide u
	/// privremeni fajl koji zamenjuje postojeći (prekid usred upisa ostavlja prethodnu verziju). Svi zapisi su i u memoriji:
	/// čitanje ne ide na disk, a memorija se menja i kad upis ne uspe — dok proces radi, ishod je tačan; upis na disk je
	/// dokaz za restart. Zato pozivalac namere pre poziva kontrolera mora da proveri ishod <see cref="SaveAsync"/>: bez
	/// upisane namere vrata se ne otvaraju.
	///
	/// Pri startu (<see cref="Initialize"/>): zapisi stariji od <see cref="Retention"/> se brišu, a nedovršeni prolaze kroz
	/// oporavak komande (npr. otvorena sesija → <see cref="RemoteServiceEndModes.ProcessRestart"/>); ništa se ne ponavlja.
	/// </summary>
	public sealed class AgentOperationJournal
	{
		public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

		private const string __fileExtension = ".json";
		private const string __hashedPrefix = "h-";
		// Fajl zaključan pri startu (antivirus, backup) čita se ponovo.
		private const int __loadAttempts = 3;
		private static readonly TimeSpan __loadRetryDelay = TimeSpan.FromMilliseconds(200);
		// Zamena fajla pada dok ga drugi proces drži otvorenog (i sa FileShare.Delete), pa se kratko ponavlja.
		private const int __replaceAttempts = 5;
		private static readonly TimeSpan __replaceRetryDelay = TimeSpan.FromMilliseconds(50);
		// Samo mala slova: Windows ne razlikuje veličinu slova u imenu fajla, a dnevnik razlikuje ID-eve po Ordinal poređenju.
		private static readonly Regex __plainFileName = new("^[a-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

		private readonly string _folder;
		private readonly TimeProvider _timeProvider;
		private readonly ILogger? _logger;
		private readonly Lock _lock = new();
		private readonly Dictionary<string, AgentOperationRecord> _records = new(StringComparer.Ordinal);
		// Serijalizuje upise na disk; memorija se menja odmah, pod _lock-om.
		private readonly SemaphoreSlim _writeGate = new(1, 1);

		public AgentOperationJournal(string folder, ILogger? logger, TimeProvider? timeProvider = null)
		{
			_folder = folder;
			_logger = logger;
			_timeProvider = timeProvider ?? TimeProvider.System;
		}

		public string FullPath => string.IsNullOrWhiteSpace(_folder) ? "(putanja nije zadata)" : Path.GetFullPath(_folder);

		public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

		/// <summary>
		/// Učitava dnevnik: briše zapise starije od <see cref="Retention"/>, a nedovršene predaje oporavku njihove komande
		/// (<paramref name="recoverers"/>; za komandu bez oporavka zapis postaje <see cref="AgentResultCodes.OutcomeUnknown"/>).
		/// Oporavljeni zapisi se odmah upisuju. Poziva se jednom, pri startu, pre prvog zahteva Agenta.
		/// </summary>
		public void Initialize(IReadOnlyDictionary<string, Func<AgentOperationRecord, AgentOperationRecord>> recoverers)
		{
			if (string.IsNullOrWhiteSpace(_folder)) {
				_logger?.LogCritical($"{nameof(AgentOperationJournal)}: folder dnevnika nije zadat; ishodi operacija se ne upisuju " +
									"na disk, pa se vrata ne otvaraju. Ispraviti konfiguraciju.");
				return;
			}
			try {
				Directory.CreateDirectory(_folder);
			}
			catch (Exception ex) {
				_logger?.LogCritical(ex, $"{nameof(AgentOperationJournal)}: folder dnevnika {FullPath} nije dostupan; vrata se ne otvaraju.");
				return;
			}

			var cutoff = UtcNow - Retention;
			var recovered = new List<AgentOperationRecord>();
			foreach (var file in EnumerateRecordFiles()) {
				var record = Load(file);
				if (record == null) {
					continue;
				}
				if (record.UpdatedUtc < cutoff) {
					Delete(file, record);
					continue;
				}
				if (!record.IsFinished) {
					record = Recover(record, recoverers);
					recovered.Add(record);
				}
				lock (_lock) {
					_records[record.RequestId] = record;
				}
			}
			foreach (var record in recovered) {
				if (!WriteAsync(record).GetAwaiter().GetResult()) {
					_logger?.LogError($"{nameof(AgentOperationJournal)}: oporavljen zapis {record.Command} {record.RequestId} nije upisan na disk.");
				}
			}
			_logger?.LogInformation($"{nameof(AgentOperationJournal)}: učitano {Count} zapisa iz {FullPath}, oporavljeno {recovered.Count}.");
		}

		public int Count
		{
			get {
				lock (_lock) {
					return _records.Count;
				}
			}
		}

		public bool TryGet(string requestId, out AgentOperationRecord record)
		{
			lock (_lock) {
				return _records.TryGetValue(requestId, out record!);
			}
		}

		/// <summary>
		/// Upisuje zapis (nov ili izmenjen) u memoriju i na disk; <see cref="AgentOperationRecord.UpdatedUtc"/> postavlja
		/// dnevnik. Vraća da li je zapis upisan na disk. Memorija je izmenjena pre povratka iz poziva (ponovljen zahtev je
		/// odmah vidi), a upis na disk ide na pool niti, nikad na niti pozivaoca (UI, nit veze).
		/// </summary>
		public async Task<bool> SaveAsync(AgentOperationRecord record)
		{
			var now = UtcNow;
			record = record with {
				CreatedUtc = record.CreatedUtc == default ? now : record.CreatedUtc,
				UpdatedUtc = now,
			};
			lock (_lock) {
				_records[record.RequestId] = record;
			}
			await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
			return await WriteAsync(record).ConfigureAwait(false);
		}

		private AgentOperationRecord Recover(AgentOperationRecord record,
											IReadOnlyDictionary<string, Func<AgentOperationRecord, AgentOperationRecord>> recoverers)
		{
			AgentOperationRecord recovered;
			try {
				recovered = recoverers.TryGetValue(record.Command, out var recover)
					? recover(record)
					: record with {
						ResultCode = AgentResultCodes.OutcomeUnknown,
						Message = "Program kioska je prekinut pre kraja operacije.",
					};
			}
			catch (Exception ex) {
				_logger?.LogError(ex, $"{nameof(AgentOperationJournal)}: oporavak {record.Command} {record.RequestId} nije uspeo");
				recovered = record with {
					ResultCode = AgentResultCodes.OutcomeUnknown,
					Message = "Program kioska je prekinut pre kraja operacije; oporavak zapisa nije uspeo.",
				};
			}
			if (!recovered.IsFinished) {
				recovered = recovered with { ResultCode = AgentResultCodes.OutcomeUnknown };
			}
			recovered = recovered with { UpdatedUtc = UtcNow };
			_logger?.LogWarning($"{nameof(AgentOperationJournal)}: {record.Command} {record.RequestId} je prekinut restartom programa; " +
								$"ishod {recovered.ResultCode}. Ništa se ne ponavlja.");
			return recovered;
		}

		private IEnumerable<string> EnumerateRecordFiles()
		{
			try {
				return Directory.EnumerateFiles(_folder, "*" + __fileExtension).ToList();
			}
			catch (Exception ex) {
				_logger?.LogError(ex, $"{nameof(AgentOperationJournal)}: čitanje foldera {FullPath} nije uspelo");
				return [];
			}
		}

		// null: fajl nije učitan (oštećen se premešta u stranu, zaključan ostaje netaknut).
		private AgentOperationRecord? Load(string file)
		{
			for (int attempt = 1; ; attempt++) {
				try {
					var record = JsonSerializer.Deserialize<AgentOperationRecord>(File.ReadAllBytes(file), AgentJson.Options);
					if (record == null || string.IsNullOrEmpty(record.RequestId) || string.IsNullOrEmpty(record.Command)) {
						throw new JsonException("zapis nema RequestId ili Command");
					}
					return record;
				}
				catch (JsonException ex) {
					PreserveCorrupt(file, ex);
					return null;
				}
				catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < __loadAttempts) {
					Thread.Sleep(__loadRetryDelay);
				}
				catch (Exception ex) {
					_logger?.LogError(ex, $"{nameof(AgentOperationJournal)}: zapis {Path.GetFullPath(file)} nije učitan i ostaje netaknut");
					return null;
				}
			}
		}

		private void PreserveCorrupt(string file, Exception error)
		{
			var preserved = $"{file}.corrupt-{_timeProvider.GetLocalNow():yyyyMMdd-HHmmss}";
			try {
				File.Move(file, preserved);
				_logger?.LogError(error, $"{nameof(AgentOperationJournal)}: oštećen zapis sačuvan kao {Path.GetFullPath(preserved)}");
			}
			catch (Exception ex) {
				_logger?.LogError(ex, $"{nameof(AgentOperationJournal)}: oštećen zapis {Path.GetFullPath(file)} nije premešten");
			}
		}

		private void Delete(string file, AgentOperationRecord record)
		{
			try {
				File.Delete(file);
			}
			catch (Exception ex) {
				_logger?.LogWarning($"{nameof(AgentOperationJournal)}: zastareo zapis {record.Command} {record.RequestId} nije obrisan: {ex.Message}");
			}
		}

		private async Task<bool> WriteAsync(AgentOperationRecord record)
		{
			if (string.IsNullOrWhiteSpace(_folder)) {
				return false;
			}
			var path = Path.Combine(_folder, FileNameOf(record.RequestId));
			var tempPath = path + ".tmp";
			await _writeGate.WaitAsync().ConfigureAwait(false);
			try {
				// Pod gate-om se piše poslednja verzija iz memorije: zakasneli upis starije verzije ne sme da je prepiše.
				lock (_lock) {
					if (_records.TryGetValue(record.RequestId, out var latest)) {
						record = latest;
					}
				}
				var json = JsonSerializer.SerializeToUtf8Bytes(record, AgentJson.Options);
				await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
					await stream.WriteAsync(json).ConfigureAwait(false);
					stream.Flush(flushToDisk: true);
				}
				await ReplaceFile(tempPath, path).ConfigureAwait(false);
				return true;
			}
			catch (Exception ex) {
				_logger?.LogError(ex, $"{nameof(AgentOperationJournal)}: upis zapisa {record.Command} {record.RequestId} u {FullPath} nije uspeo");
				try {
					File.Delete(tempPath);
				}
				catch (Exception) {
					// privremeni fajl ne smeta: sledeći upis ga prepisuje
				}
				return false;
			}
			finally {
				_writeGate.Release();
			}
		}

		private async Task ReplaceFile(string tempPath, string path)
		{
			for (int attempt = 1; ; attempt++) {
				try {
					File.Move(tempPath, path, overwrite: true);
					return;
				}
				catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < __replaceAttempts) {
					_logger?.LogWarning($"{nameof(AgentOperationJournal)}: {Path.GetFullPath(path)} nije dostupan za zamenu " +
										$"(pokušaj {attempt}/{__replaceAttempts}): {ex.Message}");
					await Task.Delay(__replaceRetryDelay).ConfigureAwait(false);
				}
			}
		}

		// RequestId je obično UUID (mala slova) i ostaje ime fajla; svaki drugi ID (velika slova, znakovi koji ne smeju u ime
		// fajla, ili ime koje počinje kao heš) dobija ime iz punog SHA-256, pa se dva različita ID-a ne slivaju u isti fajl.
		public static string FileNameOf(string requestId)
		{
			if (__plainFileName.IsMatch(requestId) && !requestId.StartsWith(__hashedPrefix, StringComparison.Ordinal)) {
				return requestId + __fileExtension;
			}
			var hash = SHA256.HashData(Encoding.UTF8.GetBytes(requestId));
			return __hashedPrefix + Convert.ToHexString(hash).ToLowerInvariant() + __fileExtension;
		}
	}
}
