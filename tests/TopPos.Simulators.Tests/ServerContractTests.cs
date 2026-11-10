using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TopPos.PenPosService;
using TopPos.ServerSimulator;
using TopPos.Service;
using Xunit;
using R = TopPos.Service.RecipientPickupLookupResult;

namespace TopPos.Simulators.Tests
{
	/// <summary>
	/// Server simulator u procesu + pravi klijenti kioska (<see cref="TopPosServiceClient"/>, <see cref="PenPosServiceClient"/>):
	/// simulator mora da odgovara onako kako ih kiosk čita. Pravila servera su iz v2 simulatora (de-test).
	/// </summary>
	public sealed class ServerContractTests : IDisposable
	{
		private const string LockerId = "111";
		private const string Phone = "063111222";
		private const int Code = 1122331;

		private readonly HttpServer _server;
		private readonly HttpClient _control;
		private readonly TopPosServiceClient _client;

		public ServerContractTests()
		{
			var config = new ServerSimulatorConfiguration {
				Server = new ServerConfiguration { Port = 0, BindAddress = "localhost", EnableControlApi = true },
				CourierQrCodes = ["KURIR-QR"],
				PaketomatId = LockerId,
				PenPos = new PenPosSimulatorConfiguration { Pin = "1234" },
			};
			_server = new HttpServer(config, NullLogger.Instance);
			_server.Start();
			_control = new HttpClient { BaseAddress = new Uri($"http://localhost:{_server.Port}/_sim/") };
			_client = new TopPosServiceClient(
				new TopPosServiceConfiguration { BaseUrl = $"http://localhost:{_server.Port}/", PathPrefix = "Api", RequestTimeout = 5000, DisableHeartbeats = true },
				LockerId, null, CancellationToken.None);
		}

		public void Dispose()
		{
			_control.Dispose();
			_server.Stop();
		}

		private async Task AddPackage(object package)
		{
			var response = await _control.PostAsJsonAsync("packages", package);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		}

		private Task AddPaidPickupPackage(decimal amount = 0)
			=> AddPackage(new { barkod = "PKT00001", brojPozicije = 5, kodZaOtvaranje = Code, iznosOtkupa = amount, telefonPrimaoca = Phone });

		private async Task<JsonElement[]> Packages()
			=> await _control.GetFromJsonAsync<JsonElement[]>("packages") ?? [];

		private async Task Post(string path, object? body = null)
		{
			using HttpContent content = body == null ? new StringContent("") : JsonContent.Create(body);
			var response = await _control.PostAsync(path, content);
			Assert.True(response.IsSuccessStatusCode, $"POST {path} → {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
		}

		[Fact]
		public async Task Recipient_pays_then_door_is_revealed_and_pickup_is_confirmed()
		{
			await AddPaidPickupPackage(amount: 123);

			Assert.Equal((R.Found, 0, 123m, null), await _client.RecipientRequestPackagePickupDetailed(Phone, Code, CancellationToken.None));

			var (paymentId, paymentError, _) = await _client.PaymentConfirmation(Code.ToString(), "T1", 123m, "SIMT0001", CancellationToken.None);
			Assert.NotNull(paymentId);
			Assert.Null(paymentError);

			Assert.Equal((R.Found, 5, 0m, null), await _client.RecipientRequestPackagePickupDetailed(Phone, Code, CancellationToken.None));
			var (confirmed, _, _) = await _client.RecipientPackagePickupConfirm(Code, Phone, 5, CancellationToken.None);
			Assert.True(confirmed);
			Assert.True((await Packages()).Single().GetProperty("preuzet").GetBoolean());
		}

		[Fact]
		public async Task Repeated_payment_is_rejected_as_not_recoverable()
		{
			await AddPaidPickupPackage(amount: 50);
			Assert.NotNull((await _client.PaymentConfirmation(Code.ToString(), "T1", 50m, "SIMT0001", CancellationToken.None)).Item1);

			var (id, error, recoverable) = await _client.PaymentConfirmation(Code.ToString(), "T1", 50m, "SIMT0001", CancellationToken.None);

			Assert.Null(id);
			Assert.Contains("već evidentirana uplata", error);
			Assert.False(recoverable);
		}

		[Fact]
		public async Task Wrong_phone_and_unknown_code_are_rejected_with_server_message()
		{
			await AddPaidPickupPackage();

			Assert.Equal((R.Rejected, null, null, "Broj telefona nije ispravan"),
				await _client.RecipientRequestPackagePickupDetailed("0640000000", Code, CancellationToken.None));
			Assert.Equal((R.Rejected, null, null, "Neispravan kod za otvaranje"),
				await _client.RecipientRequestPackagePickupDetailed(Phone, 999999, CancellationToken.None));
		}

		[Fact]
		public async Task Armed_status_fault_is_no_response_until_cleared()
		{
			await AddPaidPickupPackage();
			// count 0 = dok se ne obriše; klijent 503 ponavlja (Polly), pa jednokratna greška ne bi stigla do njega
			await Post("faults", new { endpoint = nameof(PrimaocPraznjenjePozicije), status = 503, count = 0 });

			Assert.Equal(R.NoResponse, (await _client.RecipientRequestPackagePickupDetailed(Phone, Code, CancellationToken.None)).Result);
			await Post("faults/clear");
			Assert.Equal((R.Found, 5, 0m, null), await _client.RecipientRequestPackagePickupDetailed(Phone, Code, CancellationToken.None));
		}

		[Fact]
		public async Task Courier_logs_in_and_deposits_into_assigned_door()
		{
			await AddPackage(new { barkod = "PKT00002", velicina = "M", kodZaOtvaranje = 4455661, telefonPrimaoca = Phone });

			Assert.Equal(("K001", null), await _client.CourierLogin("KURIR-QR", CancellationToken.None));
			Assert.Equal((null, "Nepoznat kurir"), await _client.CourierLogin("NEPOZNAT", CancellationToken.None));

			var (door, _) = await _client.CourierRequestPackageDeposit("K001", "PKT00002", CancellationToken.None);
			Assert.Equal(4, door);	// prva M vrata (1–3 su S)
			var (result, _) = await _client.CourierPackageDepositConfirm("K001", "PKT00002", door!.Value, CancellationToken.None);
			Assert.Equal(DepositConfirmResult.Confirmed, result);
			Assert.Equal(4, (await Packages()).Single().GetProperty("brojPozicije").GetInt32());
		}

		[Fact]
		public async Task Door_and_locker_errors_are_reported_and_cleared_idempotently()
		{
			Assert.True((await _client.ReportDoorErrorState(7, "O", CancellationToken.None)).Item1);
			Assert.True((await _client.ClearDoorErrorState(7, CancellationToken.None)).Item1);
			// „već bez greške" (400) klijent tretira kao uspeh
			Assert.True((await _client.ClearDoorErrorState(7, CancellationToken.None)).Item1);

			Assert.True((await _client.ReportLockerErrorState(VrstaGreskePaketomat.K, CancellationToken.None)).Item1);
			Assert.True((await _client.ClearLockerErrorState(CancellationToken.None)).Item1);
			Assert.True((await _client.ClearLockerErrorState(CancellationToken.None)).Item1);
		}

		[Fact]
		public async Task Calls_are_recorded_in_order_and_reset_clears_state()
		{
			await AddPaidPickupPackage();
			await _client.RecipientRequestPackagePickupDetailed(Phone, Code, CancellationToken.None);
			await Post("faults", new { endpoint = nameof(PotvrdaPlacanja), delayMs = 10, count = 0 });

			var calls = await _control.GetFromJsonAsync<JsonElement[]>("calls?since=0") ?? [];
			var call = Assert.Single(calls);
			Assert.Equal(nameof(PrimaocPraznjenjePozicije), call.GetProperty("endpoint").GetString());
			var seq = call.GetProperty("seq").GetInt64();

			await Post("reset");

			Assert.Empty(await Packages());
			Assert.Empty(await _control.GetFromJsonAsync<JsonElement[]>("faults") ?? []);
			Assert.Empty(await _control.GetFromJsonAsync<JsonElement[]>("calls?since=0") ?? []);
			await _client.RecipientRequestPackagePickupDetailed(Phone, Code, CancellationToken.None);
			var next = Assert.Single(await _control.GetFromJsonAsync<JsonElement[]>($"calls?since={seq}") ?? []);
			Assert.True(next.GetProperty("seq").GetInt64() > seq);
		}

		[Fact]
		public async Task Request_delayed_across_reset_does_not_touch_next_test_state()
		{
			// Codex: potvrda uplate koja čeka zadato kašnjenje ne sme posle reset-a da plati isti paket sledećeg testa
			await AddPaidPickupPackage(amount: 50);
			await Post("faults", new { endpoint = nameof(PotvrdaPlacanja), delayMs = 1500, count = 1 });
			// Običan HTTP zahtev: klijent kioska bi 503 ponovio (Polly), a ponovljeni zahtev je nov zahtev posle reset-a
			using var http = new HttpClient();
			var payment = http.PostAsync($"http://localhost:{_server.Port}/Api/{nameof(PotvrdaPlacanja)}/", JsonContent.Create(new {
				sifraBenzinskeStanice = LockerId, kodZaOtvaranje = Code.ToString(), posTransakcijaId = "T1", iznosKartice = 50m,
			}));
			await Task.Delay(300);

			await Post("reset");
			await AddPaidPickupPackage(amount: 50);

			Assert.Equal(HttpStatusCode.ServiceUnavailable, (await payment).StatusCode);
			Assert.False((await Packages()).Single().GetProperty("placeno").GetBoolean());
			Assert.Empty(await _control.GetFromJsonAsync<JsonElement[]>("calls?since=0") ?? []);
		}

		[Fact]
		public async Task Health_reports_actual_port()
		{
			var health = await _control.GetFromJsonAsync<JsonElement>("health");
			Assert.Equal(_server.Port, health.GetProperty("port").GetInt32());
		}

		[Fact]
		public async Task PenPos_accepts_configured_pin_and_issues_invoice()
		{
			var baseUrl = $"http://localhost:{_server.Port}";
			PenPosServiceClient Client(string pin) => new(new PenPosServiceConfiguration {
				IsEnabled = true, BaseManagementUrl = $"{baseUrl}/esir/v3", BaseInvoiceUrl = $"{baseUrl}/api", Pin = pin,
				ItemName = "Otkup", TaxCategory = "Ђ", Cashier = "1", RequestTimeout = 5000,
			}, null, CancellationToken.None);

			Assert.Equal("0100", (await Client("1234").Initialize(CancellationToken.None))?.Code);
			Assert.Equal("2100", (await Client("0000").Initialize(CancellationToken.None))?.Code);

			var invoice = await Client("1234").ProcessInvoice(123m, CancellationToken.None);
			Assert.NotNull(invoice);
			Assert.StartsWith("SIMUL001-SIMUL001-", invoice.InvoiceNumber);
			Assert.Equal(123.0, invoice.TotalAmount ?? 0);
		}
	}
}
