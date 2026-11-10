using TopPos.LockerSimulator.Pos;
using TopPos.LockerSimulator.Tests;
using Xunit;

namespace TopPos.Simulators.Tests
{
	/// <summary>
	/// <see cref="PosTerminalEngine.ResetAsync"/> (<c>POST /_sim/reset</c>): sledeći E2E scenario ne nasleđuje već aktiviran
	/// kvar — ignorisan zahtev posle WrongAckByte, zaleđenu sesiju posle FreezeAfterAck — ni podešavanja (Codex).
	/// </summary>
	public sealed class PosResetTests : IAsyncLifetime
	{
		private readonly FakeEcrWire _wire = new();
		private readonly CancellationTokenSource _cts = new();
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
			_run = _engine.RunAsync(_cts.Token);
			return Task.CompletedTask;
		}

		public async Task DisposeAsync()
		{
			_cts.Cancel();
			await _run;
			_cts.Dispose();
		}

		[Fact]
		public async Task Reset_after_wrong_ack_byte_answers_the_same_request_again()
		{
			_engine.Faults.Arm(PosFault.WrongAckByte, 1);
			byte[] request = Ecr.SerializeRequest(Ecr.NewTransaction("4072", "96", 100));
			_wire.SendFromEcr(request);
			await _wire.ExpectAsync("0x00");

			await _engine.ResetAsync();

			_wire.SendFromEcr(request);
			await _wire.ExpectAsync("ACK", "10:96/05");
		}

		[Fact]
		public async Task Reset_after_freeze_ends_the_frozen_session_and_restores_defaults()
		{
			_engine.Faults.Arm(PosFault.FreezeAfterAck, 2);
			_engine.AutoCustomer = true;
			_engine.AutoCard = SimCard.VisaChip;
			_wire.SendFromEcr(Ecr.SerializeRequest(Ecr.NewTransaction("0042", "01", 150000)));
			await _wire.ExpectAsync("ACK");
			await _wire.ExpectNothingAsync(TimeSpan.FromMilliseconds(150));

			await _engine.ResetAsync();

			Assert.Equal(PosTerminalPhase.Idle, _engine.Status.Phase);
			Assert.Empty(_engine.Faults.Snapshot());
			Assert.False(_engine.AutoCustomer);
			Assert.Equal(SimCard.MastercardContactless, _engine.AutoCard);
			// zaleđena sesija ne šalje ništa ni posle reset-a, a nov zahtev dobija odgovor (drugi FreezeAfterAck je obrisan)
			_wire.SendFromEcr(Ecr.SerializeRequest(Ecr.NewTransaction("0043", "96", 100)));
			await _wire.ExpectAsync("ACK", "10:96/05");
		}
	}
}
