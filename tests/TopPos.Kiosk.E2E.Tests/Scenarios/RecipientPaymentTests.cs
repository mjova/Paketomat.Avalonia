using TopPos.Client.WPF.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Primalac plaća otkup na POS-u (doc/plan-e2e-ui-testing.md, F5 scenariji 2–4, F6 kvarovi POS-a). POS je LockerSimulator preko
	/// com0com para (pravi TopPos.ECR u klijentu); kupca vodi test (AutoCustomer=false). Izuzetak: S8 koristi POS simulator
	/// u klijentu (<see cref="KioskOptions.WithPosSimulator"/>), jer izuzetak iz IPosClient-a pravi POS ne može da izazove.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class RecipientPaymentTests(ITestOutputHelper output)
	{
		private const decimal Amount = 250m;
		/// <summary>FunctionTimeout iz e2e konfiguracije klijenta (60 s) uz rezervu za poruke i prelaze.</summary>
		private static readonly TimeSpan __posFunctionTimeout = TimeSpan.FromSeconds(90);

		/// <summary>
		/// Scenario 2: plaćanje odobreno → redosled RRPP (vrata 0, iznos) → PotvrdaPlacanja → RRPP (stvarna vrata, iznos 0)
		/// → otključavanje tih vrata → zatvaranje → potvrda preuzimanja → Start.
		/// </summary>
		[SkippableFact]
		public Task Approved_ConfirmsPayment_ThenOpensDoor()
			=> KioskSession.RunAsync(nameof(Approved_ConfirmsPayment_ThenOpensDoor), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: Amount);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				await StartPaymentAsync(kiosk, package);
				var mark = kiosk.Log.Mark();
				await kiosk.Locker.PresentCardAsync("MastercardContactless");
				kiosk.Trace("POS: kartica MastercardContactless");
				var approved = await kiosk.WaitForStateAsync(WorkflowState.RecipientPackagePickup, mark, TimeSpan.FromSeconds(45));
				Assert.Equal("RecipientPaymentApproved", approved.Trigger);
				await kiosk.WaitForDoorOpenAsync(package.BrojPozicije, "RecipientPackagePickupPage");
				await kiosk.CloseDoorAsync(package.BrojPozicije, WorkflowState.Start);
				await kiosk.Ui.WaitForPageAsync("StartPage");

				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				var sequence = calls.Select(c => c.Endpoint).Where(e => e is "PrimaocPraznjenjePozicije" or "PotvrdaPlacanja" or "PrimaocPraznjenjePozicijePotvrda").ToList();
				Assert.Equal(["PrimaocPraznjenjePozicije", "PotvrdaPlacanja", "PrimaocPraznjenjePozicije", "PrimaocPraznjenjePozicijePotvrda"], sequence);

				var rrpp = calls.Where(c => c.Endpoint == "PrimaocPraznjenjePozicije").ToList();
				Assert.Equal("0", rrpp[0].ResponseValue("BrojPozicije"));
				Assert.Equal(Amount, decimal.Parse(rrpp[0].ResponseValue("IznosZaUplatu")!, System.Globalization.CultureInfo.InvariantCulture));
				Assert.Equal(package.BrojPozicije.ToString(), rrpp[1].ResponseValue("BrojPozicije"));
				Assert.Equal("0", rrpp[1].ResponseValue("IznosZaUplatu"));

				var payment = Assert.Single(calls, c => c.Endpoint == "PotvrdaPlacanja");
				Assert.Equal(200, payment.StatusCode);
				Assert.Equal(package.KodZaOtvaranje.ToString(), payment.RequestValue("KodZaOtvaranje"));
				Assert.Equal(Amount, decimal.Parse(payment.RequestValue("IznosKartice")!, System.Globalization.CultureInfo.InvariantCulture));
				Assert.DoesNotContain(calls, c => c.IsUnexpectedError);

				// PosClient poredi završni odgovor terminala (10) sa zahtevom — samo u log
				Assert.Single(kiosk.Log.LinesAfter(mark, "Sale: odgovor terminala odgovara zahtevu"));
				Assert.Empty(kiosk.Log.LinesAfter(0, "odgovor terminala ne odgovara zahtevu"));

				await kiosk.AssertOnlyDoorUnlockedAsync(unlockMark, package.BrojPozicije);
			});

		/// <summary>
		/// Scenario 3: banka odbija karticu (DinacardDeclined) ili je kartica istekla (VisaExpired, K07) → poruka → Start;
		/// nema potvrde plaćanja ni otključavanja, paket ostaje neplaćen.
		/// </summary>
		[SkippableTheory]
		[InlineData("DinacardDeclined")]
		[InlineData("VisaExpired")]
		public Task Declined_NoConfirmation_NoDoor_ReturnsToStart(string card)
			=> KioskSession.RunAsync($"{nameof(Declined_NoConfirmation_NoDoor_ReturnsToStart)}-{card}", output, async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: Amount);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				await StartPaymentAsync(kiosk, package);
				var mark = kiosk.Log.Mark();
				await kiosk.Locker.PresentCardAsync(card);
				kiosk.Trace($"POS: kartica {card}");
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(45));
				Assert.Equal(WorkflowState.RecipientPayment, toStart.From);
				Assert.Equal("RecipientPaymentDeclined", toStart.Trigger);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "greška u toku POS transakcije (TransactionRefused)"));
				await kiosk.Ui.WaitForPageAsync("StartPage");

				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				Assert.DoesNotContain(calls, c => c.Endpoint is "PotvrdaPlacanja" or "PrimaocPraznjenjePozicijePotvrda");
				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				var onServer = Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod);
				Assert.False(onServer.Placeno);
				// i odbijena prodaja je završni odgovor 10 (flag 04): poredi se sa zahtevom
				Assert.Single(kiosk.Log.LinesAfter(mark, "Sale: odgovor terminala odgovara zahtevu"));
				Assert.Empty(kiosk.Log.LinesAfter(0, "odgovor terminala ne odgovara zahtevu"));
			});

		/// <summary>
		/// Scenario 4 (regresija 2026-06-17): Home pritisnut pravim mišem dok POS čeka karticu ne sme da prekine
		/// plaćanje — dugme je onemogućeno, mašina ostaje u RecipientPayment, a posle kartice vrata se otvaraju.
		/// Ranije je naređani Reset posle naplate preticao RecipientPaymentApproved: naplaćeno, vrata zatvorena.
		/// </summary>
		[SkippableFact]
		public Task HomeDuringPayment_IsIgnored_PaymentCompletesAndDoorOpens()
			=> KioskSession.RunAsync(nameof(HomeDuringPayment_IsIgnored_PaymentCompletesAndDoorOpens), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: Amount);
				await kiosk.Server.AddPackageAsync(package);
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				await StartPaymentAsync(kiosk, package);
				Assert.False(await kiosk.Ui.IsEnabledAsync("Header.Home"), "Home mora biti onemogućen tokom plaćanja");

				var mark = kiosk.Log.Mark();
				await kiosk.Ui.PressWithMouseAsync("Header.Home", requireEnabled: false);
				await kiosk.Ui.PressWithMouseAsync("Header.Home", requireEnabled: false);
				await Task.Delay(TimeSpan.FromSeconds(1.5));	// namerno: dokaz da se ništa NIJE desilo
				Assert.Empty(kiosk.Log.TransitionsAfter(mark));
				Assert.Equal("WaitingForCard", (await kiosk.Locker.GetPosAsync()).Phase);

				await kiosk.Locker.PresentCardAsync("MastercardContactless");
				kiosk.Trace("POS: kartica MastercardContactless");
				var approved = await kiosk.WaitForStateAsync(WorkflowState.RecipientPackagePickup, mark, TimeSpan.FromSeconds(45));
				Assert.Equal("RecipientPaymentApproved", approved.Trigger);
				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(mark), t => t.To == WorkflowState.Start && t.Line < approved.Line);
				await kiosk.WaitForDoorOpenAsync(package.BrojPozicije, "RecipientPackagePickupPage");
				await kiosk.CloseDoorAsync(package.BrojPozicije, WorkflowState.Start);

				await kiosk.AssertOnlyDoorUnlockedAsync(unlockMark, package.BrojPozicije);
			});

		/// <summary>
		/// F6: POS ne odgovara na Sale (bez ACK-a) → posle WaitForAckTimeout (5 s) poruka o grešci terminala → Start;
		/// nema potvrde plaćanja, drugog RRPP-a ni otključavanja. POS ne stiže do WaitingForCard, pa kvar mora biti
		/// zadat pre unosa telefona.
		/// </summary>
		[SkippableFact]
		public Task PosNoResponse_ErrorMessage_ReturnsToStartWithoutPayment()
			=> KioskSession.RunAsync(nameof(PosNoResponse_ErrorMessage_ReturnsToStartWithoutPayment), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: Amount);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.Locker.ArmPosFaultAsync("NoResponse");

				await kiosk.StartRecipientPickupAsync();
				var mark = await kiosk.EnterCodeAndPhoneAsync(package);
				await kiosk.WaitForStateAsync(WorkflowState.RecipientPayment, mark);
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(45));
				Assert.Equal(WorkflowState.RecipientPayment, toStart.From);
				Assert.Equal("RecipientPaymentDeclined", toStart.Trigger);
				// ECR posle isteka ACK-a zatvori port → ComUnavailable. Bez brisanja zaostalog readResult-a u PosClient-u
				// (nalaz C) ovde je bio PosError.
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "greška u radu POS terminala: status=ComUnavailable"));
				await kiosk.Ui.WaitForPageAsync("StartPage");

				await AssertNotPaidAsync(kiosk, package, serverMark, unlockMark);
			});

		/// <summary>
		/// F6: terminal nema vezu sa bankom (NoBankConnection: kartica prineta, završna poruka sa flag-om 06) → poruka
		/// „Transakcija nije uspela" → Start; nema potvrde plaćanja ni otključavanja.
		/// </summary>
		[SkippableFact]
		public Task PosNoBankConnection_PaymentFails_ReturnsToStartWithoutPayment()
			=> KioskSession.RunAsync(nameof(PosNoBankConnection_PaymentFails_ReturnsToStartWithoutPayment), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: Amount);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.Locker.ArmPosFaultAsync("NoBankConnection");

				await StartPaymentAsync(kiosk, package);
				var mark = kiosk.Log.Mark();
				await kiosk.Locker.PresentCardAsync("MastercardContactless");
				kiosk.Trace("POS: kartica MastercardContactless (kvar NoBankConnection)");
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(45));
				Assert.Equal(WorkflowState.RecipientPayment, toStart.From);
				Assert.Equal("RecipientPaymentDeclined", toStart.Trigger);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "greška u toku POS transakcije (CommError)"));
				await kiosk.Ui.WaitForPageAsync("StartPage");

				await AssertNotPaidAsync(kiosk, package, serverMark, unlockMark);
			});

		/// <summary>
		/// S8 (618c420): Sale baci izuzetak (SimulatedPosClient, scenario Throw — H2) → poruka greške terminala →
		/// RecipientPaymentDeclined → Start. Pre ispravke catch je slao Reset, koji RecipientPayment ignoriše, pa je kiosk
		/// zauvek ostajao na ekranu plaćanja. Nema potvrde plaćanja, otključavanja ni potvrde u redu za slanje.
		/// Izuzetak između naplate i potvrde ovaj simulator ne može da izazove (pokriveno pregledom koda).
		/// </summary>
		[SkippableFact]
		public Task PosThrows_ReturnsToStartWithoutPayment()
		{
			E2ESettings.RequireDebugClient("POS simulator (SimulatedPosClient)");	// pre starta sesije: u Release-u se procesi ne pokreću
			return KioskSession.RunAsync(nameof(PosThrows_ReturnsToStartWithoutPayment), output, new KioskOptions().WithPosSimulator("Throw"), async kiosk => {
				kiosk.AssertPosSimulator("Throw");
				var package = kiosk.Data.RecipientPackage(amount: Amount);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				await kiosk.StartRecipientPickupAsync();
				var mark = await kiosk.EnterCodeAndPhoneAsync(package);
				var toPayment = await kiosk.WaitForStateAsync(WorkflowState.RecipientPayment, mark);
				Assert.Equal("RecipientPaymentRequired", toPayment.Trigger);
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(10));
				Assert.Equal(WorkflowState.RecipientPayment, toStart.From);
				Assert.Equal("RecipientPaymentDeclined", toStart.Trigger);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "SimulatedPosClient: Sale -> izuzetak (simulirano)"));
				await kiosk.Ui.WaitForPageAsync("StartPage");

				await AssertNotPaidAsync(kiosk, package, serverMark, unlockMark);
				var confirmations = Path.Combine(kiosk.ClientDirectory, "PaymentConfirmation.json");
				Assert.True(!File.Exists(confirmations) || File.ReadAllText(confirmations).Trim() == "[]",
							$"Potvrda plaćanja ne sme biti u redu za slanje: {(File.Exists(confirmations) ? File.ReadAllText(confirmations) : "")}");
			});
		}

		/// <summary>
		/// K08: kupac odustane na POS terminalu dok terminal čeka karticu → terminal javi „PREKINUTO" → informativna poruka →
		/// Start; nema potvrde plaćanja ni otključavanja.
		/// </summary>
		[SkippableFact]
		public Task CustomerCancelsOnPos_NoPayment_ReturnsToStart()
			=> KioskSession.RunAsync(nameof(CustomerCancelsOnPos_NoPayment_ReturnsToStart), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: Amount);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				await StartPaymentAsync(kiosk, package);
				var mark = kiosk.Log.Mark();
				await kiosk.Locker.CancelOnPosAsync();
				kiosk.Trace("POS: kupac odustao na terminalu");
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(45));
				Assert.Equal(WorkflowState.RecipientPayment, toStart.From);
				Assert.Equal("RecipientPaymentDeclined", toStart.Trigger);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "korisnik je prekinuo plaćanje na POS terminalu"));
				Assert.Empty(kiosk.Log.LinesAfter(mark, "greška u radu POS terminala"));
				await kiosk.Ui.WaitForPageAsync("StartPage");

				await AssertNotPaidAsync(kiosk, package, serverMark, unlockMark);
			});

		/// <summary>
		/// F6: umesto ACK-a terminal pošalje pogrešan bajt, a na ponovljeni zahtev ćuti → greška terminala → Start; nema
		/// potvrde plaćanja ni otključavanja. Kao i kod NoResponse, POS ne stiže do čekanja kartice.
		/// </summary>
		[SkippableFact]
		public Task PosWrongAckByte_ErrorMessage_ReturnsToStartWithoutPayment()
			=> KioskSession.RunAsync(nameof(PosWrongAckByte_ErrorMessage_ReturnsToStartWithoutPayment), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: Amount);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.Locker.ArmPosFaultAsync("WrongAckByte");

				await kiosk.StartRecipientPickupAsync();
				var mark = await kiosk.EnterCodeAndPhoneAsync(package);
				await kiosk.WaitForStateAsync(WorkflowState.RecipientPayment, mark);
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(45));
				Assert.Equal(WorkflowState.RecipientPayment, toStart.From);
				Assert.Equal("RecipientPaymentDeclined", toStart.Trigger);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "greška u radu POS terminala: status="));
				await kiosk.Ui.WaitForPageAsync("StartPage");

				await AssertNotPaidAsync(kiosk, package, serverMark, unlockMark);
			});

		/// <summary>
		/// F6: terminal se zamrzne posle ACK-a → ECR posle FunctionTimeout-a (60 s u e2e konfiguraciji) ponovi isti zahtev; drugi
		/// put terminal radi → kartica odobrena → jedna potvrda plaćanja → vrata. Dugačak test (preko minut).
		/// </summary>
		[SkippableFact]
		public Task PosFreezesOnce_SameRequestRepeated_PaysOnceAndOpensDoor()
			=> KioskSession.RunAsync(nameof(PosFreezesOnce_SameRequestRepeated_PaysOnceAndOpensDoor), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: Amount);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.Locker.ArmPosFaultAsync("FreezeAfterAck");

				await kiosk.StartRecipientPickupAsync();
				var mark = await kiosk.EnterCodeAndPhoneAsync(package);
				await kiosk.WaitForStateAsync(WorkflowState.RecipientPayment, mark);
				await kiosk.WaitForLogLineAsync("DoTransactionV3 error: FunctionTimeout. Sending same same request.", mark, __posFunctionTimeout);
				await kiosk.WaitForPosPhaseAsync("WaitingForCard");
				var pos = await kiosk.Locker.GetPosAsync();
				Assert.Equal(("01", (long)(Amount * 100)), (pos.TransactionType, pos.Amount));

				var cardMark = kiosk.Log.Mark();
				await kiosk.Locker.PresentCardAsync("MastercardContactless");
				kiosk.Trace("POS: kartica MastercardContactless");
				var approved = await kiosk.WaitForStateAsync(WorkflowState.RecipientPackagePickup, cardMark, TimeSpan.FromSeconds(45));
				Assert.Equal("RecipientPaymentApproved", approved.Trigger);
				Assert.Empty(kiosk.Log.LinesAfter(mark, "Canceling transaction"));
				await kiosk.WaitForDoorOpenAsync(package.BrojPozicije, "RecipientPackagePickupPage");
				await kiosk.CloseDoorAsync(package.BrojPozicije, WorkflowState.Start);

				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				Assert.Equal(200, Assert.Single(calls, c => c.Endpoint == "PotvrdaPlacanja").StatusCode);
				Assert.True(Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod).Placeno);
				await kiosk.AssertOnlyDoorUnlockedAsync(unlockMark, package.BrojPozicije);
			});

		/// <summary>
		/// F6: terminal se zamrzne i na ponovljeni zahtev → kiosk šalje otkazivanje (11) → greška terminala → Start; nema
		/// potvrde plaćanja ni otključavanja, a ishod (seq, iznos) je zapisan kao nepoznat za poređenje sa izveštajem banke.
		/// Dugačak test (preko dva minuta).
		/// </summary>
		[SkippableFact]
		public Task PosFreezesTwice_CancelSent_UnknownOutcomeLogged_NoPayment()
			=> KioskSession.RunAsync(nameof(PosFreezesTwice_CancelSent_UnknownOutcomeLogged_NoPayment), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: Amount);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.Locker.ArmPosFaultAsync("FreezeAfterAck", count: 2);

				await kiosk.StartRecipientPickupAsync();
				var mark = await kiosk.EnterCodeAndPhoneAsync(package);
				await kiosk.WaitForStateAsync(WorkflowState.RecipientPayment, mark);
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, 2 * __posFunctionTimeout);
				Assert.Equal(WorkflowState.RecipientPayment, toStart.From);
				Assert.Equal("RecipientPaymentDeclined", toStart.Trigger);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "Sending same same request."));
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "Canceling transaction."));
				AssertUnknownOutcomeLogged(kiosk, mark);
				await kiosk.Ui.WaitForPageAsync("StartPage");

				await AssertNotPaidAsync(kiosk, package, serverMark, unlockMark);
			});

		/// <summary>
		/// F6, najgori POS slučaj: terminal naplati, a završni odgovor se izgubi → FunctionTimeout → isti zahtev ponovljen, kupac
		/// karticu više ne prinosi → terminal prekida → otkazivanje (11) → Start bez potvrde i bez vrata. Kupac je zadužen, a
		/// server to ne zna: test proverava da je ishod zapisan kao nepoznat (seq, iznos) za poređenje sa izveštajem banke.
		/// Dugačak test (FunctionTimeout 60 s + čekanje kartice 30 s).
		/// </summary>
		[SkippableFact]
		public Task PosChargesButFinalResponseLost_UnknownOutcomeLogged_NoDoor()
			=> KioskSession.RunAsync(nameof(PosChargesButFinalResponseLost_UnknownOutcomeLogged_NoDoor), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: Amount);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.Locker.ArmPosFaultAsync("SwallowFinalResponse");

				await StartPaymentAsync(kiosk, package);
				var mark = kiosk.Log.Mark();
				await kiosk.Locker.PresentCardAsync("MastercardContactless");
				kiosk.Trace("POS: kartica MastercardContactless (kvar SwallowFinalResponse)");
				await kiosk.WaitForLogLineAsync("Sending same same request.", mark, __posFunctionTimeout);
				await kiosk.WaitForPosPhaseAsync("WaitingForCard");
				kiosk.Trace("POS: ponovljen zahtev čeka karticu — kupac je ne prinosi");

				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, __posFunctionTimeout);
				Assert.Equal(WorkflowState.RecipientPayment, toStart.From);
				Assert.Equal("RecipientPaymentDeclined", toStart.Trigger);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "Canceling transaction."));
				AssertUnknownOutcomeLogged(kiosk, mark);
				await kiosk.Ui.WaitForPageAsync("StartPage");

				await AssertNotPaidAsync(kiosk, package, serverMark, unlockMark);
			});

		/// <summary>
		/// K11: kartica naplaćena i server je uplatu evidentirao, ali drugi RRPP (broj vrata) ne uspe → poruka → Start, vrata
		/// zatvorena. Kupac ponovo unese kod i telefon → bez nove naplate dobija vrata.
		/// </summary>
		[SkippableFact]
		public Task PaidButDoorNumberNotReceived_RetryWithoutNewCharge()
			=> KioskSession.RunAsync(nameof(PaidButDoorNumberNotReceived_RetryWithoutNewCharge), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: Amount);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				await StartPaymentAsync(kiosk, package);
				// prvi RRPP je već prošao (iznos za uplatu) — greška važi samo za drugi, posle potvrde plaćanja
				await kiosk.Server.ArmFaultAsync("PrimaocPraznjenjePozicije", error: "E2E: dodela vrata nije uspela");
				var mark = kiosk.Log.Mark();
				await kiosk.Locker.PresentCardAsync("MastercardContactless");
				kiosk.Trace("POS: kartica MastercardContactless");
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(45));
				Assert.Equal(WorkflowState.RecipientPayment, toStart.From);
				Assert.Equal("RecipientPaymentDeclined", toStart.Trigger);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "potvrda uplate je uspela"));
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "zahtev za preuzimanjem paketa nije uspeo"));
				await kiosk.Ui.WaitForPageAsync("StartPage");
				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				Assert.True(Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod).Placeno);

				await PickupWithoutPaymentAsync(kiosk, package);

				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				Assert.Equal(200, Assert.Single(calls, c => c.Endpoint == "PotvrdaPlacanja").StatusCode);
				var rrpp = calls.Where(c => c.Endpoint == "PrimaocPraznjenjePozicije").ToList();
				Assert.Equal([200, 400, 200], rrpp.Select(c => c.StatusCode).ToList());
				Assert.Equal(200, Assert.Single(calls, c => c.Endpoint == "PrimaocPraznjenjePozicijePotvrda").StatusCode);
				await kiosk.AssertOnlyDoorUnlockedAsync(unlockMark, package.BrojPozicije);
			});

		/// <summary>
		/// K13 posle naplate: kartica naplaćena, a vrata blokirana → dva pokušaja, poruka → Start; server dobija prijavu greške
		/// „N", ne i potvrdu preuzimanja — uplata ostaje evidentirana. Posle odblokiranja kupac ponovo unese kod i telefon →
		/// vrata bez nove naplate.
		/// </summary>
		[SkippableFact]
		public Task PaidButDoorBlocked_ReportsDoorError_RetryWithoutNewCharge()
			=> KioskSession.RunAsync(nameof(PaidButDoorBlocked_ReportsDoorError_RetryWithoutNewCharge), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: Amount);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.Locker.BlockDoorAsync(package.BrojPozicije);
				kiosk.Trace($"vrata {package.BrojPozicije} blokirana");

				await StartPaymentAsync(kiosk, package);
				var mark = kiosk.Log.Mark();
				await kiosk.Locker.PresentCardAsync("MastercardContactless");
				kiosk.Trace("POS: kartica MastercardContactless");
				var approved = await kiosk.WaitForStateAsync(WorkflowState.RecipientPackagePickup, mark, TimeSpan.FromSeconds(45));
				Assert.Equal("RecipientPaymentApproved", approved.Trigger);
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(30));
				Assert.Equal(WorkflowState.RecipientPackagePickup, toStart.From);
				Assert.Equal("RecipientPickupDoorActionFailed", toStart.Trigger);
				await kiosk.Ui.WaitForPageAsync("StartPage");

				var calls = await RecipientFailureTests.WaitForDoorErrorAsync(kiosk, serverMark, package.BrojPozicije, expectedError: "N");
				Assert.DoesNotContain(calls, c => c.Endpoint == "PrimaocPraznjenjePozicijePotvrda");
				Assert.All(await kiosk.Locker.GetUnlocksAsync(unlockMark), u => Assert.False(u.Unlocked));
				var onServer = Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod);
				Assert.True(onServer.Placeno);
				Assert.False(onServer.Preuzet);

				await kiosk.Locker.UnblockDoorAsync(package.BrojPozicije);
				kiosk.Trace($"vrata {package.BrojPozicije} odblokirana");
				await PickupWithoutPaymentAsync(kiosk, package);

				calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				Assert.Equal(200, Assert.Single(calls, c => c.Endpoint == "PotvrdaPlacanja").StatusCode);
				Assert.Equal(200, Assert.Single(calls, c => c.Endpoint == "PrimaocPraznjenjePozicijePotvrda").StatusCode);
				Assert.All(await kiosk.Locker.GetUnlocksAsync(unlockMark), u => Assert.Equal(package.BrojPozicije, u.Door));
			});

		/// <summary>
		/// K06: plaćanje na eksternom terminalu (TerminalInstalled=false) → paket sa otkupom: poruka → Start, bez ekrana
		/// plaćanja; POS se ne koristi, nema potvrde plaćanja ni otključavanja.
		/// </summary>
		[SkippableFact]
		public Task ExternalTerminal_PackageWithAmount_PaymentNotPossible()
		{
			var options = new KioskOptions {
				ConfigureClient = config => config["PosTerminal"]!["TerminalInstalled"] = false,
			};
			return KioskSession.RunAsync(nameof(ExternalTerminal_PackageWithAmount_PaymentNotPossible), output, options, async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: Amount);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				await kiosk.StartRecipientPickupAsync();
				var mark = await kiosk.EnterCodeAndPhoneAsync(package);
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(30));
				Assert.Equal(WorkflowState.RecipientValidating, toStart.From);
				Assert.Equal("RecipientPaymentNotPossible", toStart.Trigger);
				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(mark), t => t.To == WorkflowState.RecipientPayment);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "zahteva plaćanje na eksternom terminalu"));
				await kiosk.Ui.WaitForPageAsync("StartPage");

				var pos = await kiosk.Locker.GetPosAsync();
				Assert.Null(pos.SeqNumber);
				await AssertNotPaidAsync(kiosk, package, serverMark, unlockMark);
			});
		}

		/// <summary>PosClient posle otkazivanja zapisuje nepoznat ishod sa seq-om i iznosom (podaci za poređenje sa bankom).</summary>
		private static void AssertUnknownOutcomeLogged(KioskSession kiosk, int mark)
		{
			var line = Assert.Single(kiosk.Log.LinesAfter(mark, "transakcija Sale sa nepoznatim ishodom"));
			Assert.Contains("SeqNumber=", line);
			Assert.Contains($"Amount={Amount:0}", line);	// decimalni separator zavisi od kulture klijenta
		}

		/// <summary>
		/// Plaćen paket: kod + telefon → RecipientNoPaymentNeeded (bez ekrana plaćanja) → vrata → zatvaranje → Start.
		/// </summary>
		private static async Task PickupWithoutPaymentAsync(KioskSession kiosk, SimPackage package)
		{
			await kiosk.StartRecipientPickupAsync();
			var mark = await kiosk.EnterCodeAndPhoneAsync(package);
			var toPickup = await kiosk.WaitForStateAsync(WorkflowState.RecipientPackagePickup, mark);
			Assert.Equal("RecipientNoPaymentNeeded", toPickup.Trigger);
			Assert.DoesNotContain(kiosk.Log.TransitionsAfter(mark), t => t.To == WorkflowState.RecipientPayment);
			await kiosk.WaitForDoorOpenAsync(package.BrojPozicije, "RecipientPackagePickupPage");
			await kiosk.CloseDoorAsync(package.BrojPozicije, WorkflowState.Start);
			await kiosk.Ui.WaitForPageAsync("StartPage");
		}

		/// <summary>Neuspela naplata: jedan RRPP, bez potvrde plaćanja i preuzimanja, bez otključavanja, paket neplaćen.</summary>
		private static async Task AssertNotPaidAsync(KioskSession kiosk, SimPackage package, long serverMark, long unlockMark)
		{
			var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
			Assert.Single(calls, c => c.Endpoint == "PrimaocPraznjenjePozicije");
			Assert.DoesNotContain(calls, c => c.Endpoint is "PotvrdaPlacanja" or "PrimaocPraznjenjePozicijePotvrda");
			Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
			var onServer = Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod);
			Assert.False(onServer.Placeno);
		}

		/// <summary>
		/// Start → kod + telefon → RecipientPayment; čeka stranicu plaćanja i POS u fazi WaitingForCard. Proverava da je
		/// POS dobio prodaju (tip 01) na iznos otkupa u parama — PotvrdaPlacanja serveru šalje iznos nezavisno, pa bez
		/// ove provere pogrešna naplata na terminalu ne bi oborila test.
		/// </summary>
		internal static async Task StartPaymentAsync(KioskSession kiosk, SimPackage package)
		{
			await kiosk.StartRecipientPickupAsync();
			var mark = await kiosk.EnterCodeAndPhoneAsync(package);
			await kiosk.WaitForStateAsync(WorkflowState.RecipientPayment, mark);
			await kiosk.Ui.WaitForPageAsync("RecipientPaymentPage");
			await kiosk.WaitForPosPhaseAsync("WaitingForCard");

			var pos = await kiosk.Locker.GetPosAsync();
			Assert.Equal("01", pos.TransactionType);
			Assert.Equal((long)(package.IznosOtkupa * 100), pos.Amount);
		}
	}
}
