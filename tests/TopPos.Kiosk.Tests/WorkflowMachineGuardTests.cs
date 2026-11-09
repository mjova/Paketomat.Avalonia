using TopPos.Kiosk.Workflow;
using Xunit;
using S = TopPos.Kiosk.Workflow.WorkflowState;
using T = TopPos.Kiosk.Workflow.WorkflowTrigger;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Atomski prijem nove operacije (<see cref="WorkflowMachine.TryReserveWhenIdle"/>) i uslov koji se proverava kad triger
	/// dođe na red (<see cref="WorkflowMachine.FireGuardedAsync(Func{bool}, WorkflowTrigger, Action?)"/>).
	/// </summary>
	public class WorkflowMachineGuardTests
	{
		private static readonly TimeSpan __wait = TimeSpan.FromSeconds(5);

		/// <summary>Start čiji ponovni ulazak (Reset) čeka <paramref name="gate"/> — mašina radi, a stanje je Start.</summary>
		private static async Task<(WorkflowMachine m, Task reentry)> StartReentryWaiting(TaskCompletionSource gate)
		{
			var m = await Mk.Started(new WorkflowContext());
			m.OnEntry(S.Start, async () => await gate.Task.ConfigureAwait(false));
			var reentry = m.FireAsync(T.Reset);
			Assert.False(reentry.IsCompleted);
			Assert.Equal(S.Start, m.State);
			return (m, reentry);
		}

		[Fact]
		public async Task TryReserveWhenIdle_accepts_only_idle_Start()
		{
			var calls = 0;
			bool Reserve() { calls++; return true; }

			var idle = Mk.Machine(new WorkflowContext());
			Assert.False(idle.TryReserveWhenIdle(Reserve));     // Idle: kiosk još nije spreman
			Assert.Equal(0, calls);

			var m = await Mk.Started(new WorkflowContext());
			Assert.False(m.TryReserveWhenIdle(() => { calls++; return false; }));   // koordinator odbio
			Assert.True(m.TryReserveWhenIdle(Reserve));
			Assert.Equal(2, calls);

			await m.FireAsync(T.RecipientPickupRequested);
			Assert.False(m.TryReserveWhenIdle(Reserve));        // sesija u toku
			Assert.Equal(2, calls);
		}

		[Fact]
		public async Task TryReserveWhenIdle_rejects_while_machine_is_firing_in_Start()
		{
			var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var (m, reentry) = await StartReentryWaiting(gate);
			var calls = 0;

			Assert.False(m.TryReserveWhenIdle(() => { calls++; return true; }));    // Start, ali OnEntry još radi
			var queued = m.FireAsync(T.Reset);                                        // red nije prazan
			Assert.False(m.TryReserveWhenIdle(() => { calls++; return true; }));
			Assert.Equal(0, calls);

			gate.SetResult();
			await Task.WhenAll(reentry, queued).WaitAsync(__wait);
			Assert.True(m.TryReserveWhenIdle(() => { calls++; return true; }));
			Assert.Equal(1, calls);
		}

		[Theory]
		[InlineData(false, true)]
		[InlineData(true, false)]
		public async Task FireGuardedAsync_checks_guard_when_trigger_runs_not_when_queued(bool allowedWhenQueued, bool allowedWhenRun)
		{
			var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var (m, reentry) = await StartReentryWaiting(gate);
			var allowed = allowedWhenQueued;
			var rejected = 0;

			var guarded = m.FireGuardedAsync(() => allowed, T.RecipientPickupRequested, () => rejected++);
			Assert.True(guarded.IsCompleted);   // upisan u red
			allowed = allowedWhenRun;
			gate.SetResult();
			await reentry.WaitAsync(__wait);

			Assert.Equal(allowedWhenRun ? S.RecipientCodeInput : S.Start, m.State);
			Assert.Equal(allowedWhenRun ? 0 : 1, rejected);
		}

		[Fact]
		public async Task FireGuardedAsync_runs_after_Reset_queued_ahead_of_it()
		{
			// Za razliku od FireIfInStateAsync (provera pri upisu u red), uslov vidi stanje posle Reset-a ispred njega.
			var server = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(T.RecipientPickupRequested);
			await m.FireAsync(m.RecipientCodeAccepted, "123456");
			m.OnEntry(S.RecipientValidating, async () => await server.Task.ConfigureAwait(false));
			var confirm = m.FireAsync(m.RecipientPhoneEntered, "0641234567");
			Assert.Equal(S.RecipientValidating, m.State);

			await m.FireAsync(T.Reset);
			await m.FireGuardedAsync(() => m.State == S.Start, m.CourierBarcodeScanned, "QR-123");
			server.SetResult();
			await confirm.WaitAsync(__wait);

			Assert.Equal(S.CourierAuthenticating, m.State);
		}

		[Fact]
		public async Task FireGuardedAsync_guard_exception_calls_onRejected_and_queue_continues()
		{
			// Rezervacija se oslobađa i kad uslov ne može da se proveri; Reset iza njega u redu se ipak izvršava, a izuzetak
			// dobija pozivalac koji prazni red.
			var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var m = await Mk.Started(new WorkflowContext());
			var startEntries = 0;
			m.OnEntry(S.Start, async () => { startEntries++; await gate.Task.ConfigureAwait(false); });
			var reentry = m.FireAsync(T.Reset);
			var rejected = 0;

			var guarded = m.FireGuardedAsync(() => throw new InvalidOperationException("uslov"), T.RecipientPickupRequested, () => rejected++);
			var queued = m.FireAsync(T.Reset);
			Assert.True(guarded.IsCompletedSuccessfully && queued.IsCompletedSuccessfully);

			gate.SetResult();
			var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reentry.WaitAsync(__wait));
			Assert.Equal("uslov", ex.Message);
			Assert.Equal(1, rejected);
			Assert.Equal(S.Start, m.State);
			Assert.Equal(2, startEntries);       // Reset iz reda je izvršen posle izuzetka uslova
		}

		[Fact]
		public async Task FireGuardedAsync_with_argument_rejected_leaves_state_and_calls_onRejected()
		{
			var m = await Mk.Started(new WorkflowContext());
			var rejected = 0;

			await m.FireGuardedAsync(() => false, m.CourierBarcodeScanned, "QR-123", () => rejected++);

			Assert.Equal(S.Start, m.State);
			Assert.Equal(1, rejected);
		}
	}
}
