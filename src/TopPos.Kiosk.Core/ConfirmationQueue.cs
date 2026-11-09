using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TopPos.Kiosk
{
	/// <summary>Zapis u trajnom redu potvrda (<see cref="ConfirmationQueue{T}"/>).</summary>
	public abstract record QueuedConfirmation
	{
		// Vreme (UTC) kada je potvrda prvi put upisana u red; null za zapise iz fajla starije verzije
		public DateTime? CreatedUtc { get; init; }
	}

	/// <summary>Ishod jednog slanja potvrde iz reda.</summary>
	public enum ConfirmationSendResult
	{
		/// <summary>Server je potvrdu prihvatio — briše se iz reda.</summary>
		Delivered,
		/// <summary>
		/// Server javlja da je potvrda već evidentirana (npr. odgovor na prvo slanje se izgubio) — briše se iz reda, uz Warning:
		/// server ne kaže da li je evidentirana baš ova potvrda.
		/// </summary>
		AlreadyDelivered,
		/// <summary>Server je potvrdu odbio (400) — briše se iz reda, uz Error u logu.</summary>
		Rejected,
		/// <summary>Prolazna greška (mreža, istek vremena, 5xx...) — ostaje u redu i šalje se ponovo.</summary>
		Retry,
	}

	/// <summary>
	/// Trajni red potvrda za server: zapis se odmah upisuje na disk (preživljava restart) i šalje dok ga server ne prihvati
	/// ili odbije, najviše <see cref="MaxPendingAge"/>. Jedan fajl po redu; slanje jedno po jedno, prolazna greška zapis
	/// premešta na kraj reda.
	/// </summary>
	public sealed class ConfirmationQueue<T> : IDisposable where T : QueuedConfirmation
	{
		// Potvrda koju server ni posle ovoliko vremena nije prihvatio uklanja se iz reda (i iz fajla)
		public static readonly TimeSpan MaxPendingAge = TimeSpan.FromHours(48);
		// Fajl zaključan pri startu (npr. antivirus) čita se ponovo; prvi upis posle starta čeka kraj učitavanja, pa je broj
		// pokušaja ograničen.
		private const int __loadAttempts = 3;
		private static readonly TimeSpan __loadRetryDelay = TimeSpan.FromMilliseconds(500);
		// Zamena fajla reda pada dok ga drugi proces drži otvorenog, i kad ga je otvorio sa FileShare.Delete (antivirus, backup,
		// alat koji skuplja fajlove): kratko zauzet fajl se zamenjuje ponovo, pa tuđe čitanje ne obara upis.
		private const int __replaceAttempts = 5;
		private static readonly TimeSpan __replaceRetryDelay = TimeSpan.FromMilliseconds(50);

		private readonly string _name;
		private readonly string _kind;
		private readonly string _path;
		private readonly TimeSpan _pollInterval;
		private readonly Func<T, CancellationToken, Task<ConfirmationSendResult>> _send;
		private readonly Func<T, string> _describe;
		private readonly ILogger _logger;
		private readonly List<T> _pending = new();
		private readonly SemaphoreSlim _listLock = new(1, 1);
		private readonly CancellationTokenSource _ctsLoading = new();
		private readonly CancellationTokenSource _ctsProcessLoop = new();
		private readonly TaskCompletionSource<bool> _tcsLoadingCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		// true kad fajl postoji, a nije ga bilo moguće ni učitati ni sačuvati sa strane: upis bi prepisao potvrde iz njega;
		// i kad putanja fajla nije zadata. Do restarta potvrde žive samo u memoriji (šalju se), a Add vraća false.
		// Postavlja se pre kraja učitavanja (_tcsLoadingCompleted), pa ga svi upisi vide.
		private bool _fileWriteBlocked;

		// Za log: GetFullPath baca za null/praznu putanju
		private string FullPath => string.IsNullOrWhiteSpace(_path) ? "(putanja nije zadata)" : Path.GetFullPath(_path);

		/// <param name="name">Ime vlasnika reda, prefiks svih poruka u logu (npr. <c>PaymentConfirmationSender</c>).</param>
		/// <param name="kind">Šta je u redu, za poruke u logu (npr. „potvrda za placanje").</param>
		/// <param name="path">Fajl reda.</param>
		/// <param name="pollInterval">Pauza kad je red prazan i posle prolazne greške.</param>
		/// <param name="send">Jedno slanje potvrde serveru.</param>
		/// <param name="describe">Opis zapisa za log.</param>
		public ConfirmationQueue(string name, string kind, string path, TimeSpan pollInterval,
								Func<T, CancellationToken, Task<ConfirmationSendResult>> send, Func<T, string> describe, ILogger logger)
		{
			_name = name;
			_kind = kind;
			// null/prazna putanja se ne odbija ovde (program ne pada): LoadFromFile tada zabranjuje upis, a red radi iz memorije
			_path = path;
			_pollInterval = pollInterval;
			_send = send ?? throw new ArgumentNullException(nameof(send));
			_describe = describe ?? throw new ArgumentNullException(nameof(describe));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));

			_ = LoadFromFile(_ctsLoading.Token)
				.ContinueWith(t => {
					if (t.Exception != null) {
						_logger.LogError(t.Exception, "{Name}: exception during loading from file: {FilePath}", _name, _path);
						_tcsLoadingCompleted.SetResult(false);
					}
					else {
						_tcsLoadingCompleted.SetResult(true);
					}
				}, TaskScheduler.Default)
				.ContinueWith(_ => {
					_ = Task.Run(() => ProcessLoop(_ctsProcessLoop.Token));
				}, TaskScheduler.Default);
		}

		/// <summary>
		/// Dodaje potvrdu u red i snima red na disk. Vraća <c>true</c> samo ako je red upisan na disk; <c>false</c> znači da
		/// potvrda (ako je dodata) postoji samo u memoriji — šalje se dok program radi, ali je restart pre uspešnog slanja
		/// gubi. Pozivalac tada mora da obavesti korisnika i podršku.
		/// </summary>
		public async Task<bool> Add(T confirmation)
		{
			try {
				await _tcsLoadingCompleted.Task.ConfigureAwait(false);
				try {
					await _listLock.WaitAsync().ConfigureAwait(false);
					confirmation = WithCreatedUtc(confirmation, confirmation.CreatedUtc ?? DateTime.UtcNow);
					_pending.Add(confirmation);
					_logger.LogInformation($"{_name}.{nameof(Add)}: {_describe(confirmation)} dodata u listu za slanje");
					return await SaveToFile().ConfigureAwait(false);
				}
				finally {
					_listLock.Release();
				}
			}
			catch (Exception ex) {
				_logger.LogError(ex, $"{_name}.{nameof(Add)}: exception");
				return false;
			}
		}

		// `with` ne radi nad parametrom tipa: kopija ide preko osnovnog record-a (virtuelni Clone daje isti tip kao original).
		private static T WithCreatedUtc(T confirmation, DateTime createdUtc)
			=> (T)((QueuedConfirmation)confirmation with { CreatedUtc = createdUtc });

		// Fajl koji ne može da se učita ne sme da prepiše prvi sledeći upis — u njemu mogu biti nepotvrđene potvrde:
		//  - oštećen sadržaj (prazan posle prekinutog upisa starije verzije, skraćen, null) → premešta se u stranu, za ručno
		//    spasavanje; red kreće od nule;
		//  - fajl nedostupan (zaključan) ni posle ponovljenih pokušaja, ili oštećen fajl ne može da se premesti → ostaje gde je
		//    i upis se zabranjuje do restarta (_fileWriteBlocked). Ispravan zaključan fajl se ne premešta: njegove potvrde se
		//    posle restarta učitavaju i šalju normalno.
		private async Task LoadFromFile(CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(_path)) {
				// bez ovoga bi upis išao u relativni fajl ".tmp" (null + ".tmp") u radnom folderu
				_fileWriteBlocked = true;
				_logger.LogCritical($"{_name}.{nameof(LoadFromFile)}: putanja fajla ({_kind}) nije zadata u konfiguraciji; " +
									"potvrde se ne upisuju na disk (salju se iz memorije, restart ih gubi). Ispraviti konfiguraciju.");
				return;
			}
			for (int attempt = 1; ; attempt++) {
				try {
					// Bez File.Exists: on vraća false i kad provera ne uspe (npr. zabranjen pristup), pa bi postojeći fajl bio
					// tretiran kao nepostojeći i prepisan prvim upisom. „Nema fajla“ je samo FileNotFound/DirectoryNotFound niže.
					// fajl se zatvara pre obrade, jer se ispod može ponovo snimati (otvoren za čitanje bi blokirao upis)
					List<T>? loaded;
					using (var stream = File.OpenRead(_path)) {
						loaded = await JsonSerializer.DeserializeAsync<List<T>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
					}
					if (loaded == null) {
						throw new JsonException("fajl ne sadrzi listu potvrda (null)");
					}
					// Čekanje pre try: otkazano čekanje ne preuzima semafor, pa finally ne sme da ga oslobodi.
					await _listLock.WaitAsync(cancellationToken).ConfigureAwait(false);
					try {
						// zapisi iz fajla starije verzije nemaju vreme upisa: rok od 48 h im počinje od ovog učitavanja
						var now = DateTime.UtcNow;
						_pending.AddRange(loaded.Select(c => WithCreatedUtc(c, c.CreatedUtc ?? now)));
						if (loaded.Any(c => c.CreatedUtc == null)) {
							await SaveToFile().ConfigureAwait(false);
						}
					}
					finally {
						_listLock.Release();
					}
					return;
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
					// gašenje programa pre kraja učitavanja — fajl ostaje netaknut
					return;
				}
				catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
					_logger.LogInformation($"{_name}.{nameof(LoadFromFile)}: fajl za slanje ({_kind}) nije pronadjen na {FullPath}.");
					return;
				}
				catch (JsonException ex) {
					_logger.LogError(ex, $"{_name}.{nameof(LoadFromFile)}: fajl ({_kind}) {FullPath} je ostecen");
					if (!TryPreserveCorruptFile()) {
						BlockFileWrites();
					}
					return;
				}
				catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < __loadAttempts) {
					_logger.LogWarning($"{_name}.{nameof(LoadFromFile)}: fajl ({_kind}) {FullPath} nije dostupan " +
										$"(pokusaj {attempt}/{__loadAttempts}): {ex.Message}");
					try {
						await Task.Delay(__loadRetryDelay, cancellationToken).ConfigureAwait(false);
					}
					catch (OperationCanceledException) {
						return;
					}
				}
				catch (Exception ex) {
					_logger.LogError(ex, $"{_name}.{nameof(LoadFromFile)}: citanje fajla ({_kind}) {FullPath} nije uspelo");
					BlockFileWrites();
					return;
				}
			}
		}

		private bool TryPreserveCorruptFile()
		{
			var preservedPath = $"{_path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
			try {
				File.Move(_path, preservedPath);
				_logger.LogError($"{_name}.{nameof(LoadFromFile)}: ostecen fajl potvrda sacuvan kao {Path.GetFullPath(preservedPath)}. " +
									"Potvrde iz njega se ne salju automatski - proveriti rucno.");
				return true;
			}
			catch (Exception ex) {
				_logger.LogError(ex, $"{_name}.{nameof(LoadFromFile)}: ostecen fajl potvrda {FullPath} nije premesten");
				return false;
			}
		}

		private void BlockFileWrites()
		{
			_fileWriteBlocked = true;
			_logger.LogCritical($"{_name}.{nameof(LoadFromFile)}: fajl potvrda {FullPath} nije ucitan i ostaje netaknut; " +
								"do restarta se nove potvrde ne upisuju na disk (salju se iz memorije). Proveriti fajl i restartovati program.");
		}

		// Vraća da li je red upisan na disk. Upis ide u privremeni fajl koji zatim zamenjuje postojeći, pa prekid usred
		// upisa (nestanak struje, pad programa) ostavlja prethodni fajl netaknut umesto praznog ili skraćenog.
		private async Task<bool> SaveToFile()
		{
			if (_fileWriteBlocked) {
				_logger.LogError($"{_name}.{nameof(SaveToFile)}: upis u {FullPath} je zabranjen (fajl nije ucitan pri startu); lista je samo u memoriji");
				return false;
			}
			var tempPath = _path + ".tmp";
			try {
				var json = JsonSerializer.Serialize(_pending);
				await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
					await stream.WriteAsync(Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);
					stream.Flush(flushToDisk: true);
				}
				await ReplaceFile(tempPath).ConfigureAwait(false);
				_logger.LogInformation($"{_name}.{nameof(SaveToFile)}: lista za slanje ({_kind}) snimljena u {FullPath}");
				return true;
			}
			catch (Exception ex) {
				_logger.LogError(ex, $"{_name}.{nameof(SaveToFile)}: snimanje liste za slanje ({_kind}) nije uspelo");
				try {
					File.Delete(tempPath);
				}
				catch (Exception) {
					// privremeni fajl ne smeta: sledeći upis ga prepisuje
				}
				return false;
			}
		}

		private async Task ReplaceFile(string tempPath)
		{
			for (int attempt = 1; ; attempt++) {
				try {
					File.Move(tempPath, _path, overwrite: true);
					return;
				}
				catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < __replaceAttempts) {
					_logger.LogWarning($"{_name}.{nameof(SaveToFile)}: fajl ({_kind}) {FullPath} nije dostupan za zamenu " +
										$"(pokusaj {attempt}/{__replaceAttempts}): {ex.Message}");
					await Task.Delay(__replaceRetryDelay).ConfigureAwait(false);
				}
			}
		}

		// Poziva se pod _listLock.
		private async Task RemoveExpired()
		{
			var cutoff = DateTime.UtcNow - MaxPendingAge;
			var expired = _pending.Where(c => c.CreatedUtc < cutoff).ToList();
			if (expired.Count == 0) {
				return;
			}
			foreach (var confirmation in expired) {
				_pending.Remove(confirmation);
				_logger.LogError($"{_name}: {_describe(confirmation)} uklonjena iz liste za slanje jer nije potvrdjena ni posle {MaxPendingAge.TotalHours} h.");
			}
			await SaveToFile().ConfigureAwait(false);
		}

		private async Task ProcessLoop(CancellationToken cancellationToken)
		{
			await _tcsLoadingCompleted.Task.ConfigureAwait(false);

			while (!cancellationToken.IsCancellationRequested) {
				T? toSend = null;
				await _listLock.WaitAsync(cancellationToken).ConfigureAwait(false);
				try {
					await RemoveExpired().ConfigureAwait(false);
					if (_pending.Count > 0) {
						toSend = _pending[0];
					}
				}
				finally {
					_listLock.Release();
				}

				if (toSend == null) {
					await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
					continue;
				}

				ConfirmationSendResult result;
				try {
					result = await _send(toSend, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception ex) when (!cancellationToken.IsCancellationRequested) {
					// izuzetak iz slanja ne sme da zaustavi petlju: potvrda ostaje u redu
					_logger.LogError(ex, $"{_name}: {_describe(toSend)} - slanje nije uspelo");
					result = ConfirmationSendResult.Retry;
				}
				if (result != ConfirmationSendResult.Retry) {
					await _listLock.WaitAsync(cancellationToken).ConfigureAwait(false);
					try {
						_pending.Remove(toSend);
						switch (result) {
							case ConfirmationSendResult.Delivered:
								_logger.LogInformation($"{_name}: {_describe(toSend)} uspesno poslata");
								break;
							case ConfirmationSendResult.AlreadyDelivered:
								// Server ne kaže da li je evidentirana baš ova potvrda ili druga za isti predmet (npr. kupac je
								// posle neuspele potvrde platio ponovo, drugom transakcijom) — zato Warning, a ne Information.
								_logger.LogWarning($"{_name}: {_describe(toSend)} - server javlja da je vec evidentirana; uklonjena iz liste za slanje. " +
													"Ako je evidentirana druga transakcija, proveriti duplu naplatu.");
								break;
							default:
								_logger.LogError($"{_name}: {_describe(toSend)} uklonjena iz liste za slanje jer ne moze biti potvrdjena.");
								break;
						}
						await SaveToFile().ConfigureAwait(false);
					}
					finally {
						_listLock.Release();
					}
				}
				else {
					// Prolazna greška: potvrda ide na kraj reda, da jedna potvrda koju server trajno ne prihvata
					// ne blokira slanje ostalih. Fajl se ne snima - redosled nije bitan, a bez servera bi se pisao svake sekunde.
					await _listLock.WaitAsync(cancellationToken).ConfigureAwait(false);
					try {
						if (_pending.Count > 1 && _pending.Remove(toSend)) {
							_pending.Add(toSend);
						}
					}
					finally {
						_listLock.Release();
					}
					await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
				}
			}
		}

		public void Dispose()
		{
			try {
				_ctsProcessLoop.Cancel();
				_ctsProcessLoop.Dispose();
				_ctsLoading.Cancel();
				_ctsLoading.Dispose();
				_listLock.Dispose();
			}
			catch (Exception ex) {
				_logger.LogError(ex, $"{_name}: dispose failed.");
			}
		}
	}
}
