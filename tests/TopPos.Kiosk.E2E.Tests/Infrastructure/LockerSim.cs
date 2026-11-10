using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>Klijent kontrolnog API-ja LockerSimulator-a (/_sim/*): vrata, moduli, skener, POS.</summary>
	public sealed class LockerSim(int port) : IDisposable
	{
		private static readonly JsonSerializerOptions __json = new(JsonSerializerDefaults.Web) {
			Converters = { new JsonStringEnumConverter() },
		};
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

		// ─── Vrata ───

		public async Task<List<DoorState>> GetDoorsAsync()
			=> await _http.GetFromJsonAsync<List<DoorState>>("doors", __json).ConfigureAwait(false) ?? [];

		public async Task<DoorState> GetDoorAsync(int number)
			=> (await GetDoorsAsync().ConfigureAwait(false)).Single(d => d.Number == number);

		public Task CloseDoorAsync(int number) => PostAsync($"doors/{number}/close");

		public Task OpenDoorAsync(int number) => PostAsync($"doors/{number}/open");

		public Task BlockDoorAsync(int number) => PostAsync($"doors/{number}/block");

		public Task UnblockDoorAsync(int number) => PostAsync($"doors/{number}/unblock");

		public Task CloseAllDoorsAsync() => PostAsync("doors/close-all");

		public Task SetModuleAsync(int module, bool isOn) => PostAsync($"modules/{module}/{(isOn ? "on" : "off")}");

		/// <param name="fault">DropOpenResponse: vrata se otključaju, a odgovor kiosku se ne pošalje.</param>
		public Task ArmLockFaultAsync(string fault, int count = 1) => PostAsync("faults", new { fault, count });

		/// <summary>Otključavanja koja je kiosk tražio, sa Seq &gt; <paramref name="since"/>.</summary>
		public async Task<List<UnlockEntry>> GetUnlocksAsync(long since = 0)
			=> await _http.GetFromJsonAsync<List<UnlockEntry>>($"unlocks?since={since}", __json).ConfigureAwait(false) ?? [];

		public async Task<long> MarkUnlocksAsync()
			=> (await GetUnlocksAsync().ConfigureAwait(false)).Select(u => u.Seq).DefaultIfEmpty(0).Max();

		// ─── Skener ───

		public Task ScanBarcodeAsync(string barcode) => PostAsync("barcode", new { barcode });

		// ─── POS ───

		public async Task<PosState> GetPosAsync()
			=> await _http.GetFromJsonAsync<PosState>("pos", __json).ConfigureAwait(false)
				?? throw new E2EException("LockerSimulator: prazan odgovor za POS stanje");

		/// <param name="card">VisaChip, MastercardContactless, DinacardDeclined, VisaExpired</param>
		public Task PresentCardAsync(string card) => PostAsync("pos/card", new { card });

		public Task EnterPinAsync(string? pin = null) => PostAsync("pos/pin", pin == null ? null : new { pin });

		public Task CancelOnPosAsync() => PostAsync("pos/cancel");

		public Task SetPosAsync(bool? autoCustomer = null, string? autoCard = null)
			=> PostAsync("pos/settings", new { autoCustomer, autoCard });

		public Task ArmPosFaultAsync(string fault, int count = 1) => PostAsync("pos/faults", new { fault, count });

		private async Task PostAsync(string path, object? body = null)
		{
			// Prazno telo ipak šalje Content-Length: 0 (HttpListener inače vraća 411).
			using HttpContent content = body == null ? new StringContent(string.Empty) : JsonContent.Create(body, options: __json);
			var response = await _http.PostAsync(path, content).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode) {
				throw new E2EException($"LockerSimulator: POST {path} → {(int)response.StatusCode} {await response.Content.ReadAsStringAsync().ConfigureAwait(false)}");
			}
		}

		public void Dispose() => _http.Dispose();
	}

	public sealed record DoorState(int Number, int Module, bool IsLocked, bool IsBlocked, bool ModuleOn);

	/// <param name="Unlocked">false = vrata su blokirana u simulatoru, pa su ostala zaključana.</param>
	public sealed record UnlockEntry(long Seq, DateTime Timestamp, int Door, bool Unlocked);

	public sealed record PosState(
		string Phase,
		string DisplayText,
		long Amount,
		string? SeqNumber,
		string? TransactionType,
		bool AutoCustomer,
		string AutoCard,
		string Cancel11Response,
		Dictionary<string, int> Faults);
}
