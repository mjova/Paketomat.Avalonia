using System.Text.Json;
using TopPos.Kiosk.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Red potvrda plaćanja (PaymentConfirmation.json) i restart klijenta — vault: backlog/E2E — red potvrda plaćanja i
	/// restart klijenta (plan testa). Kartica je odobrena, a PotvrdaPlacanja ne prođe: prolazna greška (503, istek vremena)
	/// stavlja potvrdu u red, kupac ne dobija vrata; red je pošalje kad server proradi, a kupac posle ponovnog unosa koda
	/// dobija vrata bez nove naplate. Samo 400 je konačno odbijanje, osim „već evidentirana uplata" (scenario 7).
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class PaymentConfirmationQueueTests(ITestOutputHelper output)
	{
		private const decimal Amount = 250m;
		private const string QueueFile = "PaymentConfirmation.json";
		private const string QueuedLine = "slanje potvrde će biti pokušavano dok ne uspe";
		private const int LateResponseDelayMs = 8000;	// > RequestTimeout klijenta (5 s)
		private static readonly TimeSpan __queueTimeout = TimeSpan.FromSeconds(20);

		/// <summary>Scenario 1: 503 → potvrda u redu → server proradi → red je pošalje → kupac preuzima paket bez nove naplate.</summary>
		[SkippableFact]
		public Task TransientFailure_QueueSendsConfirmation_PickupWithoutNewCharge()
			=> KioskSession.RunAsync(nameof(TransientFailure_QueueSendsConfirmation_PickupWithoutNewCharge), output, async kiosk => {
				var payment = await PayWithFailedConfirmationAsync(kiosk);
				await RecoverAndPickupAsync(kiosk, payment);
			});

		/// <summary>
		/// Scenario 2: restart klijenta dok potvrda čeka u redu (pad procesa i uredno gašenje) — potvrda preživljava restart,
		/// učita se bez greške i pošalje posle restarta.
		/// </summary>
		[SkippableTheory]
		[InlineData(true)]
		[InlineData(false)]
		public Task RestartWhileQueued_ConfirmationSurvives_PickupWithoutNewCharge(bool hard)
			=> KioskSession.RunAsync($"{nameof(RestartWhileQueued_ConfirmationSurvives_PickupWithoutNewCharge)}-{(hard ? "kill" : "close")}", output, async kiosk => {
				var payment = await PayWithFailedConfirmationAsync(kiosk);

				var mark = kiosk.Log.Mark();
				await kiosk.RestartClientAsync(hard);
				Assert.Empty(kiosk.Log.LinesAfter(mark, "PaymentConfirmationSender.LoadFromFile:"));
				Assert.Empty(CorruptFiles(kiosk));
				Assert.Single(ReadQueue(kiosk)!, c => c.TransactionNumber == payment.Queued.TransactionNumber);

				await RecoverAndPickupAsync(kiosk, payment);
			});

		/// <summary>
		/// Scenario 3 (ab4ef2b): nečitljiv PaymentConfirmation.json pri startu se premešta u .corrupt-* netaknut, ništa iz njega
		/// se ne šalje, a sledeća potvrda ide u nov fajl — ranije bi prvi upis prepisao stari sadržaj bez traga.
		/// </summary>
		[SkippableTheory]
		[InlineData("prazan", "")]
		[InlineData("skracen", "[{\"UnlockCode\":\"777777\",\"Transac")]
		[InlineData("null", "null")]
		public Task UnreadableFileAtStart_IsPreserved_NewConfirmationGoesToNewFile(string variant, string content)
		{
			var options = new KioskOptions {
				PrepareClientDirectory = directory => File.WriteAllText(Path.Combine(directory, QueueFile), content),
			};
			return KioskSession.RunAsync($"{nameof(UnreadableFileAtStart_IsPreserved_NewConfirmationGoesToNewFile)}-{variant}", output, options, async kiosk => {
				await kiosk.WaitForLogLineAsync("ostecen fajl potvrda sacuvan kao", 0);
				var corrupt = Assert.Single(CorruptFiles(kiosk));
				Assert.Equal(content, File.ReadAllText(corrupt));
				Assert.False(File.Exists(QueuePath(kiosk)), "Nečitljiv fajl mora biti premešten, ne ostavljen na mestu.");
				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(0), c => c.Endpoint == "PotvrdaPlacanja");

				var payment = await PayWithFailedConfirmationAsync(kiosk);
				Assert.Single(ReadQueue(kiosk)!);
				Assert.Equal(content, File.ReadAllText(Assert.Single(CorruptFiles(kiosk))));
				Assert.Equal(payment.Queued.UnlockCode, ReadQueue(kiosk)![0].UnlockCode);
			});
		}

		/// <summary>
		/// Scenario 4: ostatak prekinutog upisa (.tmp sa smećem) pored ispravnog fajla — start bez greške, potvrda iz pravog
		/// fajla se pošalje, a .tmp nestaje sa prvim sledećim upisom.
		/// </summary>
		[SkippableFact]
		public Task LeftoverTmpFile_ValidQueueIsSent_TmpRemoved()
		{
			SimPackage? package = null;
			var options = new KioskOptions {
				// Paket mora biti na serveru pre starta klijenta: red šalje potvrdu iz fajla odmah posle učitavanja.
				BeforeClientStartAsync = async kiosk => {
					package = kiosk.Data.RecipientPackage(amount: Amount);
					await kiosk.Server.AddPackageAsync(package);
					var queued = new QueueEntry(package.KodZaOtvaranje.ToString(), "E2E-TX-1", "SIMT0001", Amount, DateTime.UtcNow);
					File.WriteAllText(QueuePath(kiosk), JsonSerializer.Serialize(new[] { queued }));
					File.WriteAllText(QueuePath(kiosk) + ".tmp", "smeće iz prekinutog upisa");
				},
			};
			return KioskSession.RunAsync(nameof(LeftoverTmpFile_ValidQueueIsSent_TmpRemoved), output, options, async kiosk => {
				var queue = await WaitForQueueAsync(kiosk, q => q.Count == 0, "red ispražnjen");
				Assert.Empty(queue);
				Assert.False(File.Exists(QueuePath(kiosk) + ".tmp"), ".tmp mora nestati sa prvim sledećim upisom.");
				Assert.Empty(CorruptFiles(kiosk));

				var confirmation = Assert.Single(await kiosk.Server.GetBusinessCallsAsync(0), c => c.Endpoint == "PotvrdaPlacanja");
				Assert.Equal(200, confirmation.StatusCode);
				Assert.Equal(package!.KodZaOtvaranje.ToString(), confirmation.RequestValue("KodZaOtvaranje"));
				Assert.Equal("E2E-TX-1", confirmation.RequestValue("PosTransakcijaId"));
				Assert.True(Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod).Placeno);
			});
		}

		/// <summary>
		/// Varijanta scenarija 3: ispravan PaymentConfirmation.json je zaključan (otvoren bez deljenja) dok se klijent podiže →
		/// nije učitan i ostaje netaknut (nema .corrupt-*), a upis je do restarta zabranjen: nova neuspela potvrda ostaje samo u
		/// memoriji (kupac dobija „pozovite podršku"), fajl je i dalje isti. Posle restarta, sa puštenim fajlom, potvrda iz
		/// fajla se učita i pošalje.
		/// </summary>
		[SkippableFact]
		public Task LockedValidFileAtStart_KeptUntouched_SentAfterRestart()
		{
			SimPackage? package = null;
			string? content = null;
			FileStream? fileLock = null;
			var options = new KioskOptions {
				BeforeClientStartAsync = async kiosk => {
					package = kiosk.Data.RecipientPackage(amount: Amount);
					await kiosk.Server.AddPackageAsync(package);
					var queued = new QueueEntry(package.KodZaOtvaranje.ToString(), "E2E-TX-LOCK", "SIMT0001", Amount, DateTime.UtcNow);
					content = JsonSerializer.Serialize(new[] { queued });
					File.WriteAllText(QueuePath(kiosk), content);
					// drži se dok klijent ne odustane od učitavanja (3 pokušaja na 500 ms)
					fileLock = new FileStream(QueuePath(kiosk), FileMode.Open, FileAccess.Read, FileShare.None);
				},
			};
			return KioskSession.RunAsync(nameof(LockedValidFileAtStart_KeptUntouched_SentAfterRestart), output, options, async kiosk => {
				try {
					await kiosk.WaitForLogLineAsync("nije ucitan i ostaje netaknut", 0, TimeSpan.FromSeconds(30));
				}
				finally {
					fileLock?.Dispose();
					kiosk.Trace($"{QueueFile} pušten");
				}
				Assert.Empty(CorruptFiles(kiosk));
				Assert.Equal(content, File.ReadAllText(QueuePath(kiosk)));
				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(0), c => c.Endpoint == "PotvrdaPlacanja");

				// nova neuspela potvrda: upis zabranjen → samo u memoriji, fajl netaknut
				var other = kiosk.Data.RecipientPackage(amount: Amount);
				await kiosk.Server.AddPackageAsync(other);
				await kiosk.Server.ArmFaultAsync("PotvrdaPlacanja", status: 503, count: 0);
				var mark = await PayAsync(kiosk, other);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, QueuedLine));
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "je zabranjen (fajl nije ucitan pri startu)"));
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "potvrda NIJE upisana na disk"));
				Assert.Equal(content, File.ReadAllText(QueuePath(kiosk)));

				// restart pre oporavka servera: potvrda iz memorije se gubi (zato „pozovite podršku"), potvrda iz fajla ostaje
				var restartMark = kiosk.Log.Mark();
				await kiosk.RestartClientAsync(hard: true);
				Assert.Empty(kiosk.Log.LinesAfter(restartMark, "PaymentConfirmationSender.LoadFromFile:"));
				await kiosk.Server.ClearFaultsAsync();
				kiosk.Trace("greške servera obrisane");
				await WaitForQueueAsync(kiosk, q => q.Count == 0, "potvrda iz fajla poslata posle restarta");

				var sent = await kiosk.Server.GetBusinessCallsAsync(0);
				var confirmation = Assert.Single(sent, c => c.Endpoint == "PotvrdaPlacanja" && c.StatusCode == 200);
				Assert.Equal(("E2E-TX-LOCK", package!.KodZaOtvaranje.ToString()), (confirmation.RequestValue("PosTransakcijaId"), confirmation.RequestValue("KodZaOtvaranje")));
				var packages = await kiosk.Server.GetPackagesAsync();
				Assert.True(Assert.Single(packages, p => p.Barkod == package.Barkod).Placeno);
				Assert.False(Assert.Single(packages, p => p.Barkod == other.Barkod).Placeno);
				Assert.Empty(CorruptFiles(kiosk));
			});
		}

		/// <summary>Scenario 5: server konačno odbije potvrdu (400) — nema reda, vrata se ne otvaraju, trag je samo u logu.</summary>
		[SkippableFact]
		public Task RejectedConfirmation_NotQueued_NoDoor()
			=> KioskSession.RunAsync(nameof(RejectedConfirmation_NotQueued_NoDoor), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: Amount);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.Server.ArmFaultAsync("PotvrdaPlacanja", error: "E2E: potvrda odbijena");

				var mark = await PayAsync(kiosk, package);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "potvrda uplate nije uspela"));
				Assert.Empty(kiosk.Log.LinesAfter(mark, QueuedLine));

				await Task.Delay(TimeSpan.FromSeconds(3));	// namerno: dokaz da red ništa ne šalje
				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				Assert.Equal(400, Assert.Single(calls, c => c.Endpoint == "PotvrdaPlacanja").StatusCode);
				Assert.True(ReadQueue(kiosk) is null or { Count: 0 }, "Odbijena potvrda ne sme u red.");
				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				Assert.False(Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod).Placeno);
			});

		/// <summary>
		/// Scenario 6: server obradi potvrdu, ali odgovor zakasni preko RequestTimeout-a (5 s) — klijent je stavi u red, a
		/// server je u međuvremenu evidentirao uplatu. Red istu potvrdu pošalje opet, a server je — kao pravi (de-test,
		/// 2026-10-09) — odbije sa 400 „već evidentirana uplata"; red je briše, uplata nije ni izgubljena ni dupla. Kašnjenje
		/// važi do brisanja (count 0): sa jednim zakasnelim odgovorom red bi potvrdu poslao pre nego što je test vidi u fajlu.
		/// </summary>
		[SkippableFact]
		public Task LateServerResponse_QueueResendsAccepted_PickupWithoutNewCharge()
			=> KioskSession.RunAsync(nameof(LateServerResponse_QueueResendsAccepted_PickupWithoutNewCharge), output, async kiosk => {
				var payment = await PayWithFailedConfirmationAsync(kiosk, arm: () => kiosk.Server.ArmFaultAsync("PotvrdaPlacanja", delayMs: LateResponseDelayMs, count: 0));
				await Poll.UntilAsync(async () => (await kiosk.Server.GetPackagesAsync()).Any(p => p.Barkod == payment.Package.Barkod && p.Placeno),
										__queueTimeout, "server evidentirao zakasnelu potvrdu", kiosk.EnsureClientAlive);
				Assert.Single(ReadQueue(kiosk)!);	// klijent za to ne zna: potvrda i dalje čeka u redu
				await RecoverAndPickupAsync(kiosk, payment, drainDelay: TimeSpan.FromMilliseconds(LateResponseDelayMs + 1000));

				var confirmations = (await kiosk.Server.GetBusinessCallsAsync(payment.ServerMark)).Where(c => c.Endpoint == "PotvrdaPlacanja").ToList();
				Assert.True(confirmations.Count >= 2, $"Očekivano ponovno slanje iz reda: {string.Join(", ", confirmations)}");
				// Seq se dodeljuje po završetku obrade; sve imaju isto kašnjenje, pa je prva završena ona prva poslata.
				Assert.Equal(200, confirmations[0].StatusCode);
				Assert.All(confirmations.Skip(1), c => {
					Assert.Equal(400, c.StatusCode);
					Assert.Contains("već evidentirana uplata", c.ResponseMessage);
				});
			});

		/// <summary>
		/// Scenario 7: server obradi potvrdu, ali se odgovor izgubi (502 posle obrade). Polly u klijentu ponovi POST, a server ga
		/// — kao pravi (de-test, 2026-10-09) — odbije sa 400 „već evidentirana uplata". Uplata je na serveru, pa kupac dobija
		/// vrata u istoj sesiji: bez reda, bez poruke za podršku i bez nove naplate. Ranije je to bilo konačno odbijanje.
		/// </summary>
		[SkippableFact]
		public Task LostResponse_RetryAlreadyRecorded_DoorOpens()
			=> KioskSession.RunAsync(nameof(LostResponse_RetryAlreadyRecorded_DoorOpens), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: Amount);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.Server.ArmFaultAsync("PotvrdaPlacanja", status: 502, afterProcessing: true);

				await RecipientPaymentTests.StartPaymentAsync(kiosk, package);
				var mark = kiosk.Log.Mark();
				await kiosk.Locker.PresentCardAsync("MastercardContactless");
				kiosk.Trace("POS: kartica MastercardContactless");
				var approved = await kiosk.WaitForStateAsync(WorkflowState.RecipientPackagePickup, mark, TimeSpan.FromSeconds(45));
				Assert.Equal("RecipientPaymentApproved", approved.Trigger);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "server je uplatu već evidentirao"));
				Assert.Empty(kiosk.Log.LinesAfter(mark, "potvrda uplate nije uspela"));
				await kiosk.WaitForDoorOpenAsync(package.BrojPozicije, "RecipientPackagePickupPage");
				await kiosk.CloseDoorAsync(package.BrojPozicije, WorkflowState.Start);
				await kiosk.Ui.WaitForPageAsync("StartPage");

				var confirmations = (await kiosk.Server.GetBusinessCallsAsync(serverMark)).Where(c => c.Endpoint == "PotvrdaPlacanja").ToList();
				Assert.Equal([502, 400], confirmations.Select(c => c.StatusCode).ToList());
				Assert.Contains("već evidentirana uplata", confirmations[1].ResponseMessage);
				Assert.Equal(confirmations[0].RequestValue("PosTransakcijaId"), confirmations[1].RequestValue("PosTransakcijaId"));
				Assert.True(ReadQueue(kiosk) is null or { Count: 0 }, "Evidentirana uplata ne sme u red.");
				Assert.True(Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod).Placeno);
				Assert.Equal(package.BrojPozicije, Assert.Single(await kiosk.Locker.GetUnlocksAsync(unlockMark)).Door);
			});

		/// <summary>
		/// K10, dupla naplata: prva potvrda čeka u redu (server ne radi), kupac se vrati i plati ponovo; dok terminal čeka
		/// karticu server proradi i red pošalje prvu potvrdu. Druga potvrda tada dobija 400 „već evidentirana uplata" →
		/// kupac dobija vrata, a klijent u Error zapiše sve podatke druge naplate za povraćaj novca.
		/// </summary>
		[SkippableFact]
		public Task SecondChargeWhileFirstQueued_AlreadyRecorded_DoorOpensAndDoubleChargeLogged()
			=> KioskSession.RunAsync(nameof(SecondChargeWhileFirstQueued_AlreadyRecorded_DoorOpensAndDoubleChargeLogged), output, async kiosk => {
				var payment = await PayWithFailedConfirmationAsync(kiosk);

				// server još ne radi: paket nije plaćen, pa kupac dobija ekran plaćanja i drugu prodaju na terminalu
				await RecipientPaymentTests.StartPaymentAsync(kiosk, payment.Package);
				await kiosk.Server.ClearFaultsAsync();
				kiosk.Trace("greške servera obrisane");
				await WaitForQueueAsync(kiosk, q => q.Count == 0, "prva potvrda poslata iz reda");
				Assert.True(Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == payment.Package.Barkod).Placeno);

				var mark = kiosk.Log.Mark();
				await kiosk.Locker.PresentCardAsync("MastercardContactless");
				kiosk.Trace("POS: kartica MastercardContactless (druga naplata)");
				var approved = await kiosk.WaitForStateAsync(WorkflowState.RecipientPackagePickup, mark, TimeSpan.FromSeconds(45));
				Assert.Equal("RecipientPaymentApproved", approved.Trigger);
				var doubleCharge = Assert.Single(kiosk.Log.LinesAfter(mark, "ova naplata je dupla"));
				Assert.Contains($"UnlockCode={payment.Package.KodZaOtvaranje}", doubleCharge);
				Assert.Empty(kiosk.Log.LinesAfter(mark, QueuedLine));
				await kiosk.WaitForDoorOpenAsync(payment.Package.BrojPozicije, "RecipientPackagePickupPage");
				await kiosk.CloseDoorAsync(payment.Package.BrojPozicije, WorkflowState.Start);
				await kiosk.Ui.WaitForPageAsync("StartPage");

				var confirmations = (await kiosk.Server.GetBusinessCallsAsync(payment.ServerMark)).Where(c => c.Endpoint == "PotvrdaPlacanja" && c.StatusCode != 503).ToList();
				Assert.Equal([200, 400], confirmations.Select(c => c.StatusCode).ToList());
				Assert.Equal(payment.Queued.TransactionNumber, confirmations[0].RequestValue("PosTransakcijaId"));
				var second = confirmations[1].RequestValue("PosTransakcijaId");
				Assert.NotEqual(payment.Queued.TransactionNumber, second);
				Assert.Contains("već evidentirana uplata", confirmations[1].ResponseMessage);
				Assert.Contains($"TransactionNumber={second}", doubleCharge);
				Assert.True(ReadQueue(kiosk) is null or { Count: 0 }, "Evidentirana uplata ne sme u red.");
				Assert.Equal(payment.Package.BrojPozicije, Assert.Single(await kiosk.Locker.GetUnlocksAsync(payment.UnlockMark)).Door);
			});

		/// <summary>
		/// Paket sa otkupom → plaćanje → kartica odobrena → PotvrdaPlacanja ne prođe (podrazumevano 503 do brisanja grešaka) →
		/// RecipientPaymentDeclined → Start; potvrda je u PaymentConfirmation.json, vrata se ne otvaraju, paket nije plaćen.
		/// </summary>
		private static async Task<FailedPayment> PayWithFailedConfirmationAsync(KioskSession kiosk, Func<Task>? arm = null)
		{
			var package = kiosk.Data.RecipientPackage(amount: Amount);
			await kiosk.Server.AddPackageAsync(package);
			var serverMark = await kiosk.Server.MarkAsync();
			var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
			// count 0: Polly u klijentu ponavlja 5xx, pa bi jedna greška nestala već u prvom pokušaju.
			await (arm ?? (() => kiosk.Server.ArmFaultAsync("PotvrdaPlacanja", status: 503, count: 0)))();

			var mark = await PayAsync(kiosk, package);
			Assert.NotEmpty(kiosk.Log.LinesAfter(mark, QueuedLine));

			var queue = await WaitForQueueAsync(kiosk, q => q.Count == 1, "potvrda u redu");
			var queued = queue[0];
			Assert.Equal(package.KodZaOtvaranje.ToString(), queued.UnlockCode);
			Assert.Equal(Amount, queued.Amount);
			Assert.False(string.IsNullOrEmpty(queued.TransactionNumber));
			Assert.NotNull(queued.CreatedUtc);
			Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
			kiosk.Trace($"potvrda u redu: {queued}");
			return new FailedPayment(package, queued, serverMark, unlockMark);
		}

		/// <summary>Plaćanje karticom koja je odobrena; čeka povratak na Start posle neuspele potvrde. Vraća kursor pre kartice.</summary>
		private static async Task<int> PayAsync(KioskSession kiosk, SimPackage package)
		{
			await RecipientPaymentTests.StartPaymentAsync(kiosk, package);
			var mark = kiosk.Log.Mark();
			await kiosk.Locker.PresentCardAsync("MastercardContactless");
			kiosk.Trace("POS: kartica MastercardContactless");
			var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(45));
			Assert.Equal(WorkflowState.RecipientPayment, toStart.From);
			Assert.Equal("RecipientPaymentDeclined", toStart.Trigger);
			await kiosk.Ui.WaitForPageAsync("StartPage");
			return mark;
		}

		/// <summary>
		/// Server proradi (greške obrisane) → red pošalje potvrdu i isprazni fajl → paket plaćen → kupac ponovo unese kod i
		/// telefon → vrata bez plaćanja → zatvaranje → Start. Otključavanje samo tada i samo vrata paketa; sve PotvrdaPlacanja
		/// nose istu transakciju sa terminala.
		/// </summary>
		/// <param name="drainDelay">
		/// Zadato kašnjenje servera: zahtev koji ga je već primio završava se i posle brisanja grešaka. Pre preuzimanja se
		/// čeka toliko, inače bi se stari zahtev završio posle preuzimanja i promakao proverama poziva.
		/// </param>
		private static async Task RecoverAndPickupAsync(KioskSession kiosk, FailedPayment payment, TimeSpan? drainDelay = null)
		{
			await kiosk.Server.ClearFaultsAsync();
			kiosk.Trace("greške servera obrisane");
			if (drainDelay is { } drain) {
				await Task.Delay(drain);	// namerno: završetak zahteva koji su već u kašnjenju
			}
			await WaitForQueueAsync(kiosk, q => q.Count == 0, "red ispražnjen");
			Assert.True(Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == payment.Package.Barkod).Placeno);

			await kiosk.StartRecipientPickupAsync();
			var mark = await kiosk.EnterCodeAndPhoneAsync(payment.Package);
			var toPickup = await kiosk.WaitForStateAsync(WorkflowState.RecipientPackagePickup, mark);
			Assert.Equal("RecipientNoPaymentNeeded", toPickup.Trigger);
			Assert.DoesNotContain(kiosk.Log.TransitionsAfter(mark), t => t.To == WorkflowState.RecipientPayment);
			await kiosk.WaitForDoorOpenAsync(payment.Package.BrojPozicije, "RecipientPackagePickupPage");
			await kiosk.CloseDoorAsync(payment.Package.BrojPozicije, WorkflowState.Start);
			await kiosk.Ui.WaitForPageAsync("StartPage");

			var calls = await kiosk.Server.GetBusinessCallsAsync(payment.ServerMark);
			Assert.Contains(calls, c => c.Endpoint == "PotvrdaPlacanja" && c.StatusCode == 200);
			Assert.All(calls.Where(c => c.Endpoint == "PotvrdaPlacanja"),
						c => Assert.Equal(payment.Queued.TransactionNumber, c.RequestValue("PosTransakcijaId")));
			Assert.Equal(200, Assert.Single(calls, c => c.Endpoint == "PrimaocPraznjenjePozicijePotvrda").StatusCode);
			var unlock = Assert.Single(await kiosk.Locker.GetUnlocksAsync(payment.UnlockMark));
			Assert.Equal(payment.Package.BrojPozicije, unlock.Door);
		}

		private static string QueuePath(KioskSession kiosk) => Path.Combine(kiosk.ClientDirectory, QueueFile);

		private static string[] CorruptFiles(KioskSession kiosk)
			=> Directory.GetFiles(kiosk.ClientDirectory, QueueFile + ".corrupt-*");

		/// <summary>
		/// Sadržaj reda na disku, ili null ako fajla nema (upis je atomičan, pa se ne čita poluupisan fajl). Čita se uz
		/// FileShare.Delete: bez toga otvoren fajl na Windows-u obara klijentov File.Move(.tmp, overwrite) — test bi sam
		/// izazvao neuspeo upis koji proverava.
		/// </summary>
		private static List<QueueEntry>? ReadQueue(KioskSession kiosk)
		{
			try {
				using var stream = new FileStream(QueuePath(kiosk), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				return JsonSerializer.Deserialize<List<QueueEntry>>(stream);
			}
			catch (FileNotFoundException) {
				return null;
			}
		}

		private static Task<List<QueueEntry>> WaitForQueueAsync(KioskSession kiosk, Func<List<QueueEntry>, bool> condition, string what)
			=> Poll.UntilAsync(() => Task.FromResult(ReadQueue(kiosk) is { } queue && condition(queue) ? queue : null),
								__queueTimeout, $"{QueueFile}: {what}", kiosk.EnsureClientAlive);

		/// <summary>Zapis iz PaymentConfirmation.json (PaymentConfirmation u klijentu).</summary>
		private sealed record QueueEntry(string UnlockCode, string TransactionNumber, string TidNumber, decimal Amount, DateTime? CreatedUtc);

		private sealed record FailedPayment(SimPackage Package, QueueEntry Queued, long ServerMark, long UnlockMark);
	}
}
