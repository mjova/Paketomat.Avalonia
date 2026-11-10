using TopPos.LockerSimulator.Pos;
using Xunit;

namespace TopPos.LockerSimulator.Tests
{
	/// <summary>Scenariji simulatora sa lažnom ECR stranom (bez porta). Zahtevi se prave pravim ECR serijalizatorom.</summary>
	public sealed class PosTerminalEngineTests : IAsyncLifetime
	{
		private const long Amount = 150000;

		private readonly FakeEcrWire _wire = new();
		private readonly CancellationTokenSource _cts = new();
		private readonly List<PosLogEntry> _log = new();
		private PosTerminalEngine _engine = default!;
		private Task _run = Task.CompletedTask;

		public Task InitializeAsync()
		{
			_engine = new PosTerminalEngine(_wire, new PosSimulatorOptions {
				SpeedFactor = 0,
				AckTimeout = TimeSpan.FromMilliseconds(500),
				CardWaitTimeout = TimeSpan.FromSeconds(5),
				AutoCustomerDelay = TimeSpan.FromMilliseconds(20),
			});
			_engine.Log += e => { lock (_log) _log.Add(e); };
			_run = _engine.RunAsync(_cts.Token);
			return Task.CompletedTask;
		}

		public async Task DisposeAsync()
		{
			_cts.Cancel();
			await _run;
			_cts.Dispose();
		}

		private void SendSale(string seq = "0042", long amount = Amount)
			=> _wire.SendFromEcr(Ecr.SerializeRequest(Ecr.NewTransaction(seq, "01", amount)));

		private bool LogContains(string text)
		{
			lock (_log) {
				return _log.Any(e => e.Text.Contains(text));
			}
		}

		[Fact]
		public async Task ChipCard_WithCorrectPin_IsApproved()
		{
			SendSale();
			await _wire.ExpectAsync("ACK", "25:51:PROVUCI/UBACI KARTICU");

			_engine.PresentCard(SimCard.VisaChip);
			await _wire.ExpectAsync("25:54:OBRADA KARTICE", "25:55:UNOS PIN-a");

			_engine.EnterPin("1234");
			await _wire.ExpectAsync("25:56:PIN OK", "25:57:MOLIM SACEKAJ", "25:50:MOLIM SACEKAJ", "25:52:IZVADI KARTICU");

			var final = await _wire.NextAsync();
			var tp = Ecr.Parse(final).ApplyTo(Ecr.NewTransaction("0042", "01", Amount));
			Assert.Equal("02", tp.TransactionFlag);
			Assert.Equal("000001", tp.TransactionNumber);
			Assert.Equal("SIMT0001", tp.TidNumber);
		}

		[Fact]
		public async Task ContactlessCard_IsApprovedWithoutPin()
		{
			SendSale();
			await _wire.ExpectAsync("ACK", "25:51:PROVUCI/UBACI KARTICU");

			_engine.PresentCard(SimCard.MastercardContactless);

			await _wire.ExpectAsync("25:54:OBRADA KARTICE", "25:57:MOLIM SACEKAJ", "25:50:MOLIM SACEKAJ", "10:01/02");
		}

		[Theory]
		[InlineData("DINACARD", "ODBIJENO")]
		[InlineData("VISA — istekla", "KARTICA ISTEKLA")]
		public async Task RefusedCard_EndsWithFlag04(string cardNamePrefix, string text)
		{
			SendSale();
			await _wire.ExpectAsync("ACK", "25:51:PROVUCI/UBACI KARTICU");

			_engine.PresentCard(SimCard.Catalog.First(c => c.Name.StartsWith(cardNamePrefix)));
			await _wire.ExpectAsync("25:54:OBRADA KARTICE", "25:57:MOLIM SACEKAJ", "25:50:MOLIM SACEKAJ");

			var message = Ecr.Parse(await _wire.NextAsync());
			Assert.Equal("04", message.ApplyTo(Ecr.NewTransaction("0042", "01", Amount)).TransactionFlag);
			Assert.Equal(text, message.Field("displayMessage")?.Trim());
		}

		[Fact]
		public async Task WrongPinThreeTimes_BlocksPin_AndRefuses()
		{
			SendSale();
			await _wire.ExpectAsync("ACK", "25:51:PROVUCI/UBACI KARTICU");
			_engine.PresentCard(SimCard.VisaChip);
			await _wire.ExpectAsync("25:54:OBRADA KARTICE", "25:55:UNOS PIN-a");

			_engine.EnterPin("0000");
			await _wire.ExpectAsync("25:61:POGRESAN PIN", "25:55:UNOS PIN-a");
			_engine.EnterPin("1111");
			await _wire.ExpectAsync("25:61:POGRESAN PIN", "25:55:UNOS PIN-a");
			_engine.EnterPin("2222");

			await _wire.ExpectAsync("25:61:PIN BLOKIRAN", "25:50:MOLIM SACEKAJ", "25:52:IZVADI KARTICU", "10:01/04");
		}

		[Fact]
		public async Task CustomerCancelBeforeCard_SendsPrekinutoError()
		{
			SendSale();
			await _wire.ExpectAsync("ACK", "25:51:PROVUCI/UBACI KARTICU");

			_engine.CustomerCancel();

			await _wire.ExpectAsync("26:09:PREKINUTO");
		}

		[Fact]
		public async Task CustomerCancelAfterCardRead_SendsFlag06Prekinuto()
		{
			SendSale();
			await _wire.ExpectAsync("ACK", "25:51:PROVUCI/UBACI KARTICU");
			_engine.PresentCard(SimCard.VisaChip);
			await _wire.ExpectAsync("25:54:OBRADA KARTICE", "25:55:UNOS PIN-a");

			_engine.CustomerCancel();

			var message = Ecr.Parse(await _wire.NextAsync());
			Assert.Equal("06", message.ApplyTo(Ecr.NewTransaction("0042", "01", Amount)).TransactionFlag);
			Assert.Equal("PREKINUTO", message.Field("displayMessage")?.Trim());
		}

		[Fact]
		public async Task CardNotPresentedInTime_TerminalSendsPrekinuto()
		{
			// sopstveni simulator sa kratkim čekanjem kartice
			using var cts = new CancellationTokenSource();
			var wire = new FakeEcrWire();
			var engine = new PosTerminalEngine(wire, new PosSimulatorOptions { SpeedFactor = 0, CardWaitTimeout = TimeSpan.FromMilliseconds(200) });
			var run = engine.RunAsync(cts.Token);

			wire.SendFromEcr(Ecr.SerializeRequest(Ecr.NewTransaction("0042", "01", Amount)));

			await wire.ExpectAsync("ACK", "25:51:PROVUCI/UBACI KARTICU", "26:09:PREKINUTO");
			cts.Cancel();
			await run;
		}

		[Fact]
		public async Task AutoCustomer_PresentsCardAndEntersPin()
		{
			_engine.AutoCustomer = true;
			_engine.AutoCard = SimCard.VisaChip;
			SendSale();

			await _wire.ExpectAsync("ACK", "25:51:PROVUCI/UBACI KARTICU", "25:54:OBRADA KARTICE", "25:55:UNOS PIN-a", "25:56:PIN OK",
									"25:57:MOLIM SACEKAJ", "25:50:MOLIM SACEKAJ", "25:52:IZVADI KARTICU", "10:01/02");
		}

		[Fact]
		public async Task NoBankConnection_EndsWithFlag06()
		{
			_engine.Faults.Arm(PosFault.NoBankConnection, 1);
			_engine.AutoCustomer = true;
			SendSale();

			await _wire.ExpectAsync("ACK", "25:51:PROVUCI/UBACI KARTICU", "25:54:OBRADA KARTICE", "25:57:MOLIM SACEKAJ", "25:50:MOLIM SACEKAJ", "10:01/06");
		}

		[Fact]
		public async Task TidRequest_IsAnsweredWithFlag05AndTid()
		{
			_wire.SendFromEcr(Ecr.SerializeRequest(Ecr.NewTransaction("4072", "96", 100)));
			await _wire.ExpectAsync("ACK");

			var tp = Ecr.Parse(await _wire.NextAsync()).ApplyTo(Ecr.NewTransaction("4072", "96", 100));

			Assert.Equal("05", tp.TransactionFlag);
			Assert.Equal("SIMT0001", tp.TidNumber);
		}

		[Fact]
		public async Task TidThenSale_BothServed()
		{
			_wire.SendFromEcr(Ecr.SerializeRequest(Ecr.NewTransaction("4072", "96", 100)));
			await _wire.ExpectAsync("ACK", "10:96/05");

			_engine.AutoCustomer = true;
			SendSale("4073");

			await _wire.ExpectAsync("ACK", "25:51:PROVUCI/UBACI KARTICU", "25:54:OBRADA KARTICE", "25:57:MOLIM SACEKAJ", "25:50:MOLIM SACEKAJ", "10:01/02");
		}

		[Theory]
		[InlineData("205", "96")]
		[InlineData("99", "01")]
		public async Task ShortSeqRequest_IsAckedThenRejectedAsPogresanUnos(string seq, string type)
		{
			_wire.SendFromEcr(Ecr.SerializeRequest(Ecr.NewTransaction(seq, type, 100)));

			await _wire.ExpectAsync("ACK", "26:06:POGRESAN UNOS");
		}

		[Fact]
		public async Task NoResponseFault_SendsNothing_ThenNextRequestIsServed()
		{
			_engine.Faults.Arm(PosFault.NoResponse, 1);
			_wire.SendFromEcr(Ecr.SerializeRequest(Ecr.NewTransaction("4072", "96", 100)));
			await _wire.ExpectNothingAsync(TimeSpan.FromMilliseconds(200));

			_wire.SendFromEcr(Ecr.SerializeRequest(Ecr.NewTransaction("4073", "96", 100)));
			await _wire.ExpectAsync("ACK", "10:96/05");
		}

		[Fact]
		public async Task WrongAckByteFault_SendsZeroOnce_AndIgnoresRepeatsOfSameRequest()
		{
			_engine.Faults.Arm(PosFault.WrongAckByte, 1);
			byte[] request = Ecr.SerializeRequest(Ecr.NewTransaction("4072", "96", 100));

			_wire.SendFromEcr(request);
			await _wire.ExpectAsync("0x00");
			_wire.SendFromEcr(request);	// ECR odmah ponavlja isti zahtev
			_wire.SendFromEcr(request);
			await _wire.ExpectNothingAsync(TimeSpan.FromMilliseconds(200));

			_wire.SendFromEcr(Ecr.SerializeRequest(Ecr.NewTransaction("4073", "96", 100)));
			await _wire.ExpectAsync("ACK", "10:96/05");
		}

		[Fact]
		public async Task FreezeTwice_ThenCancel11_IsRejectedByDefault()
		{
			// Kiosk: FunctionTimeout → isti zahtev ponovo → opet timeout → Cancel 11 (PosClient.DoTransaction).
			_engine.Faults.Arm(PosFault.FreezeAfterAck, 2);
			SendSale("0042");
			await _wire.ExpectAsync("ACK");
			await _wire.ExpectNothingAsync(TimeSpan.FromMilliseconds(150));
			SendSale("0042");
			await _wire.ExpectAsync("ACK");
			await _wire.ExpectNothingAsync(TimeSpan.FromMilliseconds(150));

			_wire.SendFromEcr(Ecr.SerializeRequest(Ecr.NewTransaction("0042", "11", Amount)));

			await _wire.ExpectAsync("ACK", "26:06:POGRESAN UNOS");
		}

		[Fact]
		public async Task Cancel11_CanBeAccepted_WithFlag01()
		{
			_engine.Cancel11Response = Cancel11Response.Accept;

			_wire.SendFromEcr(Ecr.SerializeRequest(Ecr.NewTransaction("0042", "11", Amount)));

			await _wire.ExpectAsync("ACK", "10:11/01");
		}

		[Fact]
		public async Task SwallowedFinalResponse_ThenRetryOfSameSeq_IsReportedAsDoubleCharge()
		{
			_engine.Faults.Arm(PosFault.SwallowFinalResponse, 1);
			_engine.AutoCustomer = true;
			SendSale("0042");
			await _wire.ExpectAsync("ACK", "25:51:PROVUCI/UBACI KARTICU", "25:54:OBRADA KARTICE", "25:57:MOLIM SACEKAJ", "25:50:MOLIM SACEKAJ");
			await _wire.ExpectNothingAsync(TimeSpan.FromMilliseconds(150));

			SendSale("0042");
			await _wire.ExpectAsync("ACK", "25:51:PROVUCI/UBACI KARTICU", "25:54:OBRADA KARTICE", "25:57:MOLIM SACEKAJ", "25:50:MOLIM SACEKAJ", "10:01/02");

			Assert.True(LogContains("DVOSTRUKA NAPLATA"));
		}

		[Fact]
		public async Task EcrCancelWhileWaitingForCard_IsAckedAndConfirmedWith24()
		{
			SendSale();
			await _wire.ExpectAsync("ACK", "25:51:PROVUCI/UBACI KARTICU");

			_wire.SendCancelFromEcr();

			await _wire.ExpectAsync("ACK", "24:PREKINUTO");
		}

		[Fact]
		public async Task EcrCancelAfterCardProcessingStarted_IsStillConfirmed()
		{
			// ECR može da pošalje 23 i dok terminal šalje/potvrđuje 54 (Codex review #3).
			SendSale();
			await _wire.ExpectAsync("ACK", "25:51:PROVUCI/UBACI KARTICU");
			_engine.PresentCard(SimCard.VisaChip);
			await _wire.ExpectAsync("25:54:OBRADA KARTICE", "25:55:UNOS PIN-a");

			_wire.SendCancelFromEcr();

			await _wire.ExpectAsync("ACK", "24:PREKINUTO");
			_engine.EnterPin("1234");  // stara sesija je prekinuta — PIN se zanemaruje
			await _wire.ExpectNothingAsync(TimeSpan.FromMilliseconds(150));
		}

		[Fact]
		public async Task NewRequestDuringSession_AbortsOldSession()
		{
			SendSale("0042");
			await _wire.ExpectAsync("ACK", "25:51:PROVUCI/UBACI KARTICU");

			SendSale("0043");
			await _wire.ExpectAsync("ACK", "25:51:PROVUCI/UBACI KARTICU");

			_engine.PresentCard(SimCard.MastercardContactless);
			await _wire.ExpectAsync("25:54:OBRADA KARTICE", "25:57:MOLIM SACEKAJ", "25:50:MOLIM SACEKAJ");
			var tp = Ecr.Parse(await _wire.NextAsync()).ApplyTo(Ecr.NewTransaction("0043", "01", Amount));
			Assert.Equal("02", tp.TransactionFlag);
			await _wire.ExpectNothingAsync(TimeSpan.FromMilliseconds(150));
		}

		[Fact]
		public async Task NakFromEcr_FrameIsRepeated()
		{
			_wire.ReplyOverrides.Enqueue(PosFrame.NAK);
			SendSale();

			await _wire.ExpectAsync("ACK", "25:51:PROVUCI/UBACI KARTICU", "25:51:PROVUCI/UBACI KARTICU");
		}

		[Fact]
		public async Task NoAckFromEcr_AbortsSession()
		{
			_wire.ReplyOverrides.Enqueue(null);
			SendSale();

			await _wire.ExpectAsync("ACK", "25:51:PROVUCI/UBACI KARTICU");
			await _wire.ExpectNothingAsync(TimeSpan.FromMilliseconds(800));
			Assert.True(LogContains("nema ACK-a"));
			Assert.Equal(PosTerminalPhase.Idle, _engine.Status.Phase);
		}
	}
}
