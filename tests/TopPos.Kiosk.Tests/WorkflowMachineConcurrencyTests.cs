using TopPos.Kiosk.Workflow;
using Xunit;
using S = TopPos.Kiosk.Workflow.WorkflowState;
using T = TopPos.Kiosk.Workflow.WorkflowTrigger;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Mašini se pristupa iz više niti: UI (tasteri, Home), pool (nastavak OnEntry-ja posle ConfigureAwait(false),
	/// tajmer neaktivnosti, skener, nadzor vrata). Red naređanih trigera mora da izdrži istovremene pozive.
	/// </summary>
	public class WorkflowMachineConcurrencyTests
	{
		[Fact]
		public async Task Reset_concurrent_with_end_of_validation_is_neither_lost_nor_crashes()
		{
			// Home pritisnut tačno kad provera na serveru završava: ishod se fire-uje sa niti iz pool-a, Reset sa druge
			// niti. Kojim god redom se izvrše, kiosk mora da završi na Start-u (Reset pre ishoda → ishod ignorisan na
			// Start-u; Reset posle ishoda → Start iz RecipientPackagePickup). Izgubljen Reset ostavio bi mašinu u
			// RecipientPackagePickup (i Reset u redu za sledeću sesiju); nezaštićen red je bacao NullReferenceException.
			const int iterations = 5000;
			for (int i = 0; i < iterations; i++) {
				var m = await Mk.Started(new WorkflowContext());
				await m.FireAsync(T.RecipientPickupRequested);
				await m.FireAsync(m.RecipientCodeAccepted, "123456");

				var server = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				async ValueTask Validate()
				{
					await server.Task.ConfigureAwait(false);
					await m.FireAsync(T.RecipientNoPaymentNeeded).ConfigureAwait(false);
				}
				m.OnEntry(S.RecipientValidating, Validate);
				var confirm = m.FireAsync(m.RecipientPhoneEntered, "0641234567");
				Assert.Equal(S.RecipientValidating, m.State);

				using var barrier = new Barrier(2);
				var answer = Task.Run(() => { barrier.SignalAndWait(); server.SetResult(); });
				var home = Task.Run(async () => { barrier.SignalAndWait(); await m.FireAsync(T.Reset).ConfigureAwait(false); });

				await Task.WhenAll(answer, home, confirm);
				Assert.True(m.State == S.Start, $"Iteracija {i}: stanje {m.State}, očekivano Start (Reset izgubljen).");
			}
		}

		/// <summary>
		/// Machine u RecipientValidating čiji OnEntry čeka "server" (<paramref name="server"/>) pa baci izuzetak.
		/// Vraća task potvrde telefona — pozivalac koji je zatekao mašinu slobodnu i prazni red.
		/// </summary>
		private static async Task<(WorkflowMachine m, Task confirm)> ValidatingThatThrows(TaskCompletionSource server)
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(T.RecipientPickupRequested);
			await m.FireAsync(m.RecipientCodeAccepted, "123456");
			async ValueTask Validate()
			{
				await server.Task;
				throw new InvalidOperationException("prvi");
			}
			m.OnEntry(S.RecipientValidating, Validate);
			var confirm = m.FireAsync(m.RecipientPhoneEntered, "0641234567");
			Assert.Equal(S.RecipientValidating, m.State);
			return (m, confirm);
		}

		[Fact]
		public async Task Exception_in_OnEntry_still_runs_queued_Reset_and_reaches_the_caller()
		{
			// Izuzetak koji probije OnEntry ne sme da ostavi naređan Reset u redu (izvršio bi se tek u sledećoj sesiji)
			// niti da zaključa mašinu. Izuzetak dobija pozivalac koji je pokrenuo triger.
			var server = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var (m, confirm) = await ValidatingThatThrows(server);

			var home = m.FireAsync(T.Reset);                // naređan dok OnEntry čeka server
			Assert.True(home.IsCompletedSuccessfully);      // poziv u redu se odmah završava, bez izuzetka

			server.SetResult();
			var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => confirm);
			Assert.Equal("prvi", ex.Message);
			Assert.Equal(S.Start, m.State);                 // naređan Reset je izvršen

			await m.FireIfInStateAsync(S.Start, T.RecipientPickupRequested);
			Assert.Equal(S.RecipientCodeInput, m.State);    // mašina nije ostala zaključana
		}

		[Fact]
		public async Task Exception_from_queued_trigger_does_not_replace_the_first_one()
		{
			// I naređan triger može da baci izuzetak (ovde OnEntry Start-a); red se prazni do kraja, a pozivalac dobija
			// prvi izuzetak — onaj koji je nastao u njegovom trigeru.
			var server = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var (m, confirm) = await ValidatingThatThrows(server);
			m.OnEntry(S.Start, () => throw new InvalidOperationException("drugi"));

			await m.FireAsync(T.Reset);
			server.SetResult();
			var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => confirm);
			Assert.Equal("prvi", ex.Message);
			Assert.Equal(S.Start, m.State);
		}
	}
}
