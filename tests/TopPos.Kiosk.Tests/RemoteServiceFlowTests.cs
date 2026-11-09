using System.IO;
using System.Text.Json;
using TopPos.Kiosk.Workflow;
using Xunit;
using S = TopPos.Kiosk.Workflow.WorkflowState;
using T = TopPos.Kiosk.Workflow.WorkflowTrigger;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Servisna sesija Agenta (<see cref="S.RemoteService"/>): počinje samo sa slobodnog Start-a, a završava se samo kroz
	/// <see cref="T.RemoteServiceEnded"/> — Reset je ne prekida, jer serviser možda radi sa otvorenim vratima.
	/// </summary>
	public class RemoteServiceFlowTests
	{
		[Fact]
		public async Task RemoteServiceStarted_in_Start_goes_to_RemoteService()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(m.RemoteServiceStarted, "s1");
			Assert.Equal(S.RemoteService, m.State);
		}

		[Fact]
		public async Task RemoteServiceEnded_goes_back_to_Start()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(m.RemoteServiceStarted, "s1");
			await m.FireAsync(T.RemoteServiceEnded);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task Reset_is_ignored_during_RemoteService()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(m.RemoteServiceStarted, "s1");
			await m.FireAsync(T.Reset);
			Assert.Equal(S.RemoteService, m.State);
		}

		[Fact]
		public void Home_is_disabled_during_RemoteService()
			=> Assert.True(WorkflowMachine.IsHomeDisabled(S.RemoteService));

		public static TheoryData<S> AllStates() => new(Enum.GetValues<S>());

		[Theory]
		[MemberData(nameof(AllStates))]
		public void Only_RemoteService_page_takes_the_whole_window(S state)
			=> Assert.Equal(state == S.RemoteService, WorkflowMachine.IsFullScreen(state));

		[Theory]
		[InlineData(T.RecipientPickupRequested)]
		[InlineData(T.SenderDepositRequested)]
		[InlineData(T.ServiceBarcodeScanned)]
		public async Task Start_inputs_are_ignored_during_RemoteService(T trigger)
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(m.RemoteServiceStarted, "s1");
			await m.FireIfInStateAsync(S.Start, trigger);
			Assert.Equal(S.RemoteService, m.State);
		}

		[Fact]
		public async Task RemoteServiceStarted_is_ignored_outside_Start()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(T.RecipientPickupRequested);
			await m.FireAsync(m.RemoteServiceStarted, "s1");
			Assert.Equal(S.RecipientCodeInput, m.State);
		}

		[Fact]
		public async Task Late_RemoteServiceEnded_does_not_abort_a_flow_started_after_the_session()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(m.RemoteServiceStarted, "s1");
			await m.FireAsync(T.RemoteServiceEnded);
			await m.FireAsync(T.RecipientPickupRequested);

			await m.FireAsync(T.RemoteServiceEnded);
			Assert.Equal(S.RecipientCodeInput, m.State);
		}

		[Fact]
		public async Task Session_id_travels_with_the_trigger_to_OnEntry()
		{
			var m = await Mk.Started(new WorkflowContext());
			string? entered = null;
			m.OnEntryFrom(S.RemoteService, m.RemoteServiceStarted, id => { entered = id; return ValueTask.CompletedTask; });

			await m.FireAsync(m.RemoteServiceStarted, "s1");

			Assert.Equal("s1", entered);
		}

		[Fact]
		public void Role_of_RemoteService_is_Agent_in_status_lock()
		{
			Assert.Equal(WorkflowStatusRules.AgentRole, WorkflowStatusRules.ResolveRole(S.RemoteService));

			var dir = Directory.CreateTempSubdirectory("toppos-status-");
			try {
				var semaphore = new StatusSemaphore(dir.FullName, logger: null);
				semaphore.SetBusy(S.RemoteService);
				Assert.True(semaphore.WaitForPendingWrites(TimeSpan.FromSeconds(5)));
				using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "status.lock")));
				Assert.Equal(WorkflowStatusRules.AgentRole, doc.RootElement.GetProperty("Role").GetString());
				Assert.True(doc.RootElement.GetProperty("Busy").GetBoolean());
			}
			finally {
				dir.Delete(recursive: true);
			}
		}
	}
}
