using System.Net.Http.Json;
using System.Text.Json;

namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>Klijent kontrolnog API-ja ServerSimulator-a (/_sim/*).</summary>
	public sealed class ServerSim(int port) : IDisposable
	{
		private static readonly JsonSerializerOptions __json = new(JsonSerializerDefaults.Web);
		private readonly HttpClient _http = new() { BaseAddress = new Uri($"http://localhost:{port}/_sim/"), Timeout = TimeSpan.FromSeconds(10) };

		public async Task<bool> IsUpAsync()
		{
			try {
				return (await _http.GetAsync("health").ConfigureAwait(false)).IsSuccessStatusCode;
			}
			catch (HttpRequestException) {
				return false;
			}
		}

		public async Task AddPackageAsync(SimPackage package)
		{
			var response = await _http.PostAsJsonAsync("packages", package, __json).ConfigureAwait(false);
			await EnsureSuccess(response, $"dodavanje paketa {package.Barkod}").ConfigureAwait(false);
		}

		public async Task<List<SimPackage>> GetPackagesAsync()
			=> await _http.GetFromJsonAsync<List<SimPackage>>("packages", __json).ConfigureAwait(false) ?? [];

		/// <summary>Primljeni API pozivi sa Seq &gt; <paramref name="since"/>, redom obrade.</summary>
		public async Task<List<ServerCall>> GetCallsAsync(long since = 0)
			=> await _http.GetFromJsonAsync<List<ServerCall>>($"calls?since={since}", __json).ConfigureAwait(false) ?? [];

		/// <summary>Poslednji redni broj poziva — kursor pre radnje.</summary>
		public async Task<long> MarkAsync()
			=> (await GetCallsAsync().ConfigureAwait(false)).Select(c => c.Seq).DefaultIfEmpty(0).Max();

		/// <summary>Pozivi posle kursora bez heartbeat-a, sa redom endpoint-a.</summary>
		public async Task<List<ServerCall>> GetBusinessCallsAsync(long since)
			=> (await GetCallsAsync(since).ConfigureAwait(false)).Where(c => c.Endpoint != "HeartbeatPaketomata").ToList();

		/// <summary>
		/// Greška za endpoint: kašnjenje pre obrade i/ili odgovor umesto obrade — 400 sa porukom <paramref name="error"/>,
		/// ili zadati <paramref name="status"/> (npr. 503: prolazna greška, zahtev se ne obrađuje).
		/// <paramref name="count"/> = broj zahteva na koje deluje (0 = dok se ne obriše, <see cref="ClearFaultsAsync"/>).
		/// <paramref name="afterProcessing"/>: zahtev se obradi, pa se tek onda vrati <paramref name="status"/> — izgubljen odgovor.
		/// </summary>
		public async Task ArmFaultAsync(string endpoint, int delayMs = 0, string? error = null, int count = 1, int? status = null, bool afterProcessing = false)
		{
			var response = await _http.PostAsJsonAsync("faults", new { endpoint, delayMs, error, count, status, afterProcessing }, __json).ConfigureAwait(false);
			await EnsureSuccess(response, $"greška za {endpoint}").ConfigureAwait(false);
		}

		/// <summary>Briše sve zadate greške (i one sa count 0).</summary>
		public async Task ClearFaultsAsync()
		{
			var response = await _http.PostAsync("faults/clear", null).ConfigureAwait(false);
			await EnsureSuccess(response, "brisanje grešaka").ConfigureAwait(false);
		}

		private static async Task EnsureSuccess(HttpResponseMessage response, string what)
		{
			if (!response.IsSuccessStatusCode) {
				throw new E2EException($"ServerSimulator: {what} → {(int)response.StatusCode} {await response.Content.ReadAsStringAsync().ConfigureAwait(false)}");
			}
		}

		public void Dispose() => _http.Dispose();
	}

	/// <summary>Paket u ServerSimulator-u (isti oblik kao TopPos.ServerSimulator.PackageItem).</summary>
	public sealed record SimPackage
	{
		public required string Barkod { get; init; }
		public int BrojPozicije { get; init; }
		public int KodZaOtvaranje { get; init; }
		public decimal IznosOtkupa { get; init; }
		public string Velicina { get; init; } = "S";
		public string TelefonPrimaoca { get; init; } = string.Empty;
		public bool Istekao { get; init; }
		public bool Placeno { get; init; }
		public bool Preuzet { get; init; }
	}

	public sealed record ServerCall(
		long Seq,
		DateTime Timestamp,
		string Endpoint,
		string Method,
		Dictionary<string, string?> Query,
		string RequestBody,
		string ResponseBody,
		int StatusCode,
		bool IsError)
	{
		public override string ToString() => $"#{Seq} {Method} {Endpoint} -> {StatusCode}";

		/// <summary>
		/// Greška, osim 400 koje i pravi server vraća na poništavanje nepostojeće greške („je već bez greške", „nije u stanju
		/// greške") — klijent ih tretira kao uspeh, a šalje ih pri zatvaranju vrata i javljanju modula.
		/// </summary>
		public bool IsUnexpectedError => IsError
			&& !(StatusCode == 400 && Endpoint is "PonistavanjeGreskePozicije" or "PonistavanjeGreskePaketomata"
				&& (ResponseBody.Contains("je već bez greške") || ResponseBody.Contains("nije u stanju greške")));

		/// <summary>Vrednost polja iz JSON tela zahteva (ime bez obzira na velika slova), kao tekst.</summary>
		public string? RequestValue(string property) => JsonValue(RequestBody, property);

		/// <summary>Vrednost polja iz JSON odgovora (ime bez obzira na velika slova), kao tekst.</summary>
		public string? ResponseValue(string property) => JsonValue(ResponseBody, property);

		/// <summary>
		/// Poruka greške iz odgovora (responseStatus.message), dekodirana. Za proveru teksta sa dijakriticima koristiti nju, a ne
		/// ResponseBody: server serijalizuje ne-ASCII slova kao escape („već" → „već").
		/// </summary>
		public string? ResponseMessage => ResponseValue("responseStatus") is { } status ? JsonValue(status, "message") : null;

		private static string? JsonValue(string json, string property)
		{
			if (string.IsNullOrWhiteSpace(json)) {
				return null;
			}
			using var document = JsonDocument.Parse(json);
			if (document.RootElement.ValueKind != JsonValueKind.Object) {
				return null;
			}
			foreach (var element in document.RootElement.EnumerateObject()) {
				if (string.Equals(element.Name, property, StringComparison.OrdinalIgnoreCase)) {
					return element.Value.ValueKind == JsonValueKind.String ? element.Value.GetString() : element.Value.GetRawText();
				}
			}
			return null;
		}
	}
}
