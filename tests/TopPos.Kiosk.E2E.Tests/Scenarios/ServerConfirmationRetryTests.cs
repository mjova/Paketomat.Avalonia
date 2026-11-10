using System.Text.Json;
using TopPos.Kiosk.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Stavka E (tasks/todo.md, vault: Codex review klijenta, nalaz 3): potvrda preuzimanja koja ne prođe zbog prolazne
	/// greške čeka u trajnom redu (ServerConfirmationSender, ServerConfirmations.json) i stiže kad server proradi, i posle
	/// restarta klijenta. Server ne odgovara zadatim 503 do brisanja (count 0: Polly ponavlja 5xx).
	/// Potvrda ostavljanja ne ide u red (P5, 2026-10-14): kurir/pošiljalac ne nastavlja bez nje — DepositConfirmationFailedTests.
	/// Potvrda preuzimanja isteklog paketa (KurirPraznjenjePozicijePotvrda) namerno nije ovde: kurir tada čeka odgovor na
	/// ekranu za skeniranje, pa ciljno ponašanje bez servera traži odluku, ne samo red.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class ServerConfirmationRetryTests(ITestOutputHelper output)
	{
		private const string QueueFile = "ServerConfirmations.json";
		private const string PickupEndpoint = "PrimaocPraznjenjePozicijePotvrda";
		private static readonly TimeSpan __deliveryTimeout = TimeSpan.FromSeconds(30);

		/// <summary>
		/// Primalac: potvrda preuzimanja (šalje se čim se vrata otvore) ne prođe → kupac normalno preuzme paket → server
		/// proradi → potvrda stigne naknadno, server paket briše.
		/// </summary>
		[SkippableFact]
		public Task RecipientPickupConfirmation_ServerDown_DeliveredWhenServerRecovers()
			=> KioskSession.RunAsync(nameof(RecipientPickupConfirmation_ServerDown_DeliveredWhenServerRecovers), output, async kiosk => {
				var (package, serverMark) = await PickupWithServerDownAsync(kiosk);
				await AssertRecipientConfirmationDeliveredAsync(kiosk, package, serverMark);
			});

		/// <summary>Kao prethodni, uz pad klijenta pre nego što server proradi: potvrda mora preživeti restart.</summary>
		[SkippableFact]
		public Task RecipientPickupConfirmation_ServerDownAndClientRestart_DeliveredAfterRestart()
			=> KioskSession.RunAsync(nameof(RecipientPickupConfirmation_ServerDownAndClientRestart_DeliveredAfterRestart), output, async kiosk => {
				var (package, serverMark) = await PickupWithServerDownAsync(kiosk);
				await kiosk.RestartClientAsync(hard: true);
				await AssertRecipientConfirmationDeliveredAsync(kiosk, package, serverMark);
			});

		/// <summary>
		/// Nečitljiv ServerConfirmations.json pri startu se premešta u .corrupt-* netaknut i ništa iz njega se ne šalje; kiosk
		/// radi normalno, a sledeća potvrda koja ne prođe ide u nov fajl i stiže kad server proradi (kao red plaćanja,
		/// PaymentConfirmationQueueTests scenario 3).
		/// </summary>
		[SkippableTheory]
		[InlineData("prazan", "")]
		[InlineData("skracen", "[{\"Kind\":\"RecipientPickup\",\"DoorNumber\":3,\"UnlockC")]
		[InlineData("null", "null")]
		public Task UnreadableQueueFileAtStart_IsPreserved_NewConfirmationQueuedAndDelivered(string variant, string content)
		{
			var options = new KioskOptions {
				PrepareClientDirectory = directory => File.WriteAllText(Path.Combine(directory, QueueFile), content),
			};
			return KioskSession.RunAsync($"{nameof(UnreadableQueueFileAtStart_IsPreserved_NewConfirmationQueuedAndDelivered)}-{variant}", output, options, async kiosk => {
				await kiosk.WaitForLogLineAsync("ServerConfirmationSender.LoadFromFile: ostecen fajl potvrda sacuvan kao", 0);
				var corrupt = Assert.Single(CorruptFiles(kiosk));
				Assert.Equal(content, File.ReadAllText(corrupt));
				Assert.False(File.Exists(QueuePath(kiosk)), "Nečitljiv fajl mora biti premešten, ne ostavljen na mestu.");
				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(0), c => c.Endpoint == PickupEndpoint);

				var (package, serverMark) = await PickupWithServerDownAsync(kiosk);
				var queued = await Poll.UntilAsync(() => Task.FromResult(ReadQueue(kiosk) is { Count: > 0 } queue ? queue : null),
													__deliveryTimeout, $"{QueueFile}: potvrda preuzimanja u novom fajlu", kiosk.EnsureClientAlive);
				var entry = Assert.Single(queued);
				Assert.Equal(("RecipientPickup", package.KodZaOtvaranje.ToString()), (entry.GetProperty("Kind").GetString(), entry.GetProperty("UnlockCode").ToString()));
				Assert.Equal(content, File.ReadAllText(Assert.Single(CorruptFiles(kiosk))));

				await AssertRecipientConfirmationDeliveredAsync(kiosk, package, serverMark);
			});
		}

		private static async Task ArmServerDownAsync(KioskSession kiosk, string endpoint)
		{
			await kiosk.Server.ArmFaultAsync(endpoint, status: 503, count: 0);
			kiosk.Trace($"server ne odgovara na {endpoint} (503)");
		}

		/// <summary>Paket bez otkupa, server ne prima potvrdu preuzimanja → vrata → zatvaranje → Start (kao danas).</summary>
		private static async Task<(SimPackage Package, long ServerMark)> PickupWithServerDownAsync(KioskSession kiosk)
		{
			var package = kiosk.Data.RecipientPackage(amount: 0);
			await kiosk.Server.AddPackageAsync(package);
			var serverMark = await kiosk.Server.MarkAsync();
			await ArmServerDownAsync(kiosk, "PrimaocPraznjenjePozicijePotvrda");

			await kiosk.StartRecipientPickupAsync();
			var mark = await kiosk.EnterCodeAndPhoneAsync(package);
			await kiosk.WaitForStateAsync(WorkflowState.RecipientPackagePickup, mark);
			await kiosk.WaitForDoorOpenAsync(package.BrojPozicije, "RecipientPackagePickupPage");
			await kiosk.CloseDoorAsync(package.BrojPozicije, WorkflowState.Start);
			await kiosk.Ui.WaitForPageAsync("StartPage");

			await WaitForFailedAttemptAsync(kiosk, serverMark, "PrimaocPraznjenjePozicijePotvrda");
			return (package, serverMark);
		}

		private static async Task AssertRecipientConfirmationDeliveredAsync(KioskSession kiosk, SimPackage package, long serverMark)
		{
			await kiosk.Server.ClearFaultsAsync();
			kiosk.Trace("server ponovo odgovara");
			var delivered = await WaitForDeliveredAsync(kiosk, serverMark, "PrimaocPraznjenjePozicijePotvrda");
			Assert.Equal(package.KodZaOtvaranje.ToString(), delivered.RequestValue("KodZaOtvaranje"));
			Assert.Equal(package.BrojPozicije.ToString(), delivered.RequestValue("BrojPozicije"));
			Assert.True(Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod).Preuzet);
		}

		/// <summary>
		/// Bar jedan pokušaj potvrde je stigao do servera i odbijen (503), nijedan nije prošao. Čeka se uz rok, a ne proverava
		/// odmah: red bi potvrdu mogao slati u pozadini, posle povratka na Start — bez čekanja bi test obrisao grešku pre prvog
		/// pokušaja i prošao bez provere ponavljanja. Broj pokušaja se ne proverava (Polly ponavlja 5xx).
		/// </summary>
		private static async Task WaitForFailedAttemptAsync(KioskSession kiosk, long serverMark, string endpoint)
		{
			await Poll.UntilAsync(async () => (await kiosk.Server.GetBusinessCallsAsync(serverMark)).Any(c => c.Endpoint == endpoint && c.StatusCode == 503),
									__deliveryTimeout, $"{endpoint} odbijen (503) dok server ne odgovara", kiosk.EnsureClientAlive);
			Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == endpoint && c.StatusCode == 200);
			kiosk.Trace($"{endpoint}: pokušaj odbijen (503)");
		}

		private static Task<ServerCall> WaitForDeliveredAsync(KioskSession kiosk, long serverMark, string endpoint)
			=> Poll.UntilAsync(async () => (await kiosk.Server.GetBusinessCallsAsync(serverMark)).FirstOrDefault(c => c.Endpoint == endpoint && c.StatusCode == 200),
								__deliveryTimeout, $"{endpoint} 200 posle oporavka servera", kiosk.EnsureClientAlive);

		private static string QueuePath(KioskSession kiosk) => Path.Combine(kiosk.ClientDirectory, QueueFile);

		private static string[] CorruptFiles(KioskSession kiosk)
			=> Directory.GetFiles(kiosk.ClientDirectory, QueueFile + ".corrupt-*");

		/// <summary>
		/// Sadržaj reda na disku, ili null ako fajla nema. Čita se uz FileShare.Delete, da otvoren fajl ne obori klijentov
		/// File.Move(.tmp, overwrite).
		/// </summary>
		private static List<JsonElement>? ReadQueue(KioskSession kiosk)
		{
			try {
				using var stream = new FileStream(QueuePath(kiosk), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				return JsonSerializer.Deserialize<List<JsonElement>>(stream);
			}
			catch (FileNotFoundException) {
				return null;
			}
		}
	}
}
