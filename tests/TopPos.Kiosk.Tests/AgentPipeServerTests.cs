using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.AgentCommunication;
using TopPos.Kiosk.Workflow;
using TopPos.Platform.Windows;
using Xunit;
using S = TopPos.Kiosk.Workflow.WorkflowState;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Pipe server kanala ka Agentu (plan §4, §5, §10) sa pravim named pipe-om pod tekućim nalogom. Dozvoljeni klijent je u
	/// testovima tekući nalog; LocalSystem kao klijent i pristup pod drugim Windows nalozima proveravaju se ručno na VM-u.
	/// v4: isti testovi i nad Unix socket-om (Linux); testovi DACL-a i imena pipe-a samo na Windows-u.
	/// </summary>
	public class AgentPipeServerTests : IDisposable
	{
		// v4: socket-i testova u zasebnom folderu (Linux), obrisanom posle testa.
		private readonly string _socketRoot = Path.Combine(Path.GetTempPath(), $"toppos-agent-{Guid.NewGuid():N}"[..21]);

		public void Dispose()
		{
			if (Directory.Exists(_socketRoot)) {
				Directory.Delete(_socketRoot, recursive: true);
			}
		}

		private const string LockerId = "112";
		private static readonly TimeSpan __readTimeout = TimeSpan.FromSeconds(5);
		private static SecurityIdentifier CurrentUser => WindowsIdentity.GetCurrent().User!;
		private static SecurityIdentifier LocalSystem => new(WellKnownSidType.LocalSystemSid, null);

		[Fact]
		public async Task Get_status_after_handshake_returns_current_snapshot()
		{
			var source = ReadySource();
			await using var server = StartServer(source, out var pipeName);
			await using var client = await TestAgentClient.ConnectAsync(pipeName);

			var hello = await client.HandshakeAsync();
			var response = await client.RequestAsync(AgentCommands.GetStatus);

			Assert.Equal(LockerId, hello.LockerId);
			Assert.Equal(source.ClientInstanceId, hello.ClientInstanceId);
			Assert.Equal([AgentCommands.GetStatus, AgentCommands.SubscribeStatus], hello.SupportedCommands);
			Assert.Equal(AgentResultCodes.Completed, response.ResultCode);
			Assert.Equal(source.Current.Revision, response.Status!.StatusRevision);
			Assert.Equal("Start", response.Status.WorkflowState);
			Assert.False(response.Status.Busy);
			Assert.True(response.Status.Operational);
			Assert.Equal(KioskLifecycles.Ready, response.Status.Lifecycle);
			Assert.False(response.Status.CanAcceptOpenDoor);
			Assert.Equal(CannotAcceptReasons.NotSupported, response.Status.CannotAcceptReason);
		}

		[Fact]
		public async Task Subscription_gets_initial_snapshot_then_changes_then_heartbeats()
		{
			var source = ReadySource();
			await using var server = StartServer(source, out var pipeName, o => o with { HeartbeatInterval = TimeSpan.FromMilliseconds(150) });
			await using var client = await TestAgentClient.ConnectAsync(pipeName);
			await client.HandshakeAsync();

			var subscribed = await client.RequestAsync(AgentCommands.SubscribeStatus);
			long initial = subscribed.Status!.StatusRevision;
			source.SetWorkflowState(S.CourierAuthenticating);
			// v4: heartbeat sa starom revizijom može stići pre promene kad je mašina opterećena (Debian E2E 26.11.2026) — preskače se;
			// red ne gubi promenu (spajanje je zadržava) niti posle nje šalje stariju reviziju.
			var changed = await client.ReadEventAsync(e => e.Event == AgentEvents.StatusChanged);
			var heartbeat = await client.ReadEventAsync(e => e.Event == AgentEvents.Heartbeat);

			Assert.NotNull(subscribed.SubscriptionId);
			Assert.Equal(AgentEvents.StatusChanged, changed.Event);
			Assert.Equal(subscribed.SubscriptionId, changed.SubscriptionId);
			Assert.Equal(initial + 1, changed.Status!.StatusRevision);
			Assert.True(changed.Status.Busy);
			Assert.Equal("Courier", changed.Status.Role);
			Assert.Equal(changed.Status.StatusRevision, heartbeat.Status!.StatusRevision);   // heartbeat ne menja reviziju
			Assert.Equal(changed.Status.ChangedAtUtc, heartbeat.Status.ChangedAtUtc);
			Assert.True(heartbeat.Status.ObservedAtUtc > changed.Status.ObservedAtUtc);
		}

		/// <summary>Zaglavljena UI nit: heartbeat izostaje (Agentu status zastari), a stvarne promene i dalje stižu.</summary>
		[Fact]
		public async Task Heartbeat_is_withheld_while_ui_does_not_confirm()
		{
			var source = ReadySource();
			var uiStuck = new TaskCompletionSource<bool>();
			await using var server = StartServer(source, out var pipeName,
										o => o with { HeartbeatInterval = TimeSpan.FromMilliseconds(100), UiAckTimeout = TimeSpan.FromMilliseconds(50) },
										probe: _ => uiStuck.Task);
			await using var client = await TestAgentClient.ConnectAsync(pipeName);
			await client.HandshakeAsync();
			await client.RequestAsync(AgentCommands.SubscribeStatus);

			Assert.Null(await client.TryReadAsync(TimeSpan.FromMilliseconds(700)));

			source.SetWorkflowState(S.RecipientCodeInput);
			var changed = await client.ReadEventAsync();
			Assert.Equal(AgentEvents.StatusChanged, changed.Event);
			Assert.Null(await client.TryReadAsync(TimeSpan.FromMilliseconds(500)));
		}

		[Theory]
		[InlineData("999", null, AgentResultCodes.WrongInstance)]
		[InlineData(LockerId, "stari-proces", AgentResultCodes.WrongInstance)]
		public async Task Request_for_other_locker_or_instance_is_rejected(string lockerId, string? instanceId, string expected)
		{
			await using var server = StartServer(ReadySource(), out var pipeName);
			await using var client = await TestAgentClient.ConnectAsync(pipeName);
			await client.HandshakeAsync();

			var response = await client.RequestAsync(AgentCommands.GetStatus, lockerId: lockerId, clientInstanceId: instanceId);

			Assert.Equal(expected, response.ResultCode);
			Assert.Null(response.Status);
		}

		[Theory]
		[InlineData(AgentCommands.OpenDoor)]
		[InlineData(AgentCommands.GetCommandResult)]
		[InlineData("Restart")]
		public async Task Commands_other_than_status_are_not_supported(string command)
		{
			await using var server = StartServer(ReadySource(), out var pipeName);
			await using var client = await TestAgentClient.ConnectAsync(pipeName);
			await client.HandshakeAsync();

			var response = await client.RequestAsync(command);

			Assert.Equal(AgentResultCodes.UnsupportedCommand, response.ResultCode);
			// Veza ostaje upotrebljiva.
			Assert.Equal(AgentResultCodes.Completed, (await client.RequestAsync(AgentCommands.GetStatus)).ResultCode);
		}

		[Fact]
		public async Task Request_with_other_protocol_version_gets_unsupported_version()
		{
			await using var server = StartServer(ReadySource(), out var pipeName);
			await using var client = await TestAgentClient.ConnectAsync(pipeName);
			await client.HandshakeAsync();

			var response = await client.RequestAsync(AgentCommands.GetStatus, protocolVersion: 2);

			Assert.Equal(AgentResultCodes.UnsupportedVersion, response.ResultCode);
		}

		/// <summary>Ime je već zauzeto (npr. proces koji je preuzeo ime pre kioska): drugi server se ne pokreće, nema drugog imena.</summary>
		[Fact]
		public async Task Second_server_with_same_name_does_not_start()
		{
			await using var first = StartServer(ReadySource(), out var pipeName);
			await using var second = new AgentPipeServer(Options(pipeName), ReadySource(), AlwaysResponsive, logger: null);

			Assert.False(second.Start());
		}

		/// <summary>
		/// Druga linija odbrane: drugi proces kiosk naloga prolazi DACL (nalog procesa mora imati čitanje/pisanje da bi pravio
		/// instance), ali nije LocalSystem — veza se zatvara pre Hello, bez ijednog odgovora.
		/// </summary>
		// v4: dozvoljen je samo LocalSystem (Windows), odnosno root (Linux); test radi kao običan korisnik.
		[NotRootFact]
		public async Task Client_that_is_not_local_system_is_closed_before_hello()
		{
			await using var server = StartServer(ReadySource(), out var pipeName, o => o with { AllowCurrentUser = false });
			await using var client = await TestAgentClient.ConnectAsync(pipeName);

			try {
				await client.SendAsync(new ClientHello { AgentVersion = "test" });
			}
			catch (IOException) when (!OperatingSystem.IsWindows()) {
				// v4: Linux transport zatvara vezu odmah posle accept (SO_PEERCRED), pa upis može da naiđe na zatvorenu vezu.
			}

			Assert.Null(await client.ReadFrameAsync());
			// v4: klijent vidi kraj veze pre nego što je server ukloni iz spiska.
			Assert.True(SpinWait.SpinUntil(() => server.ConnectionCount == 0, __readTimeout));
		}

		/// <summary>
		/// v4: zaustavljanje i Dispose u isto vreme (dok se pretplaćena veza prazni) čekaju isto gašenje; posle njega adresa je
		/// slobodna, a zaustavljen server se ne pokreće ponovo.
		/// </summary>
		[Fact]
		public async Task Overlapping_stop_and_dispose_both_wait_and_release_the_address()
		{
			var server = StartServer(ReadySource(), out var address, o => o with { HeartbeatInterval = TimeSpan.FromMilliseconds(100) });
			await using var client = await TestAgentClient.ConnectAsync(address);
			await client.HandshakeAsync();
			await client.RequestAsync(AgentCommands.SubscribeStatus);

			var stop = server.StopAsync();
			var dispose = server.DisposeAsync().AsTask();
			await Task.WhenAll(stop, dispose).WaitAsync(__readTimeout);

			Assert.Throws<InvalidOperationException>(() => server.Start());
			await using var next = new AgentPipeServer(Options(address).ToServerOptions(), ReadySource(), AlwaysResponsive, logger: null);
			Assert.True(next.Start());
		}

		/// <summary>Kiosk nalog pravi i sledeće instance kad je dozvoljeni klijent drugi nalog (LocalSystem).</summary>
		[WindowsFact("named pipe i DACL postoje samo na Windows-u")]
		public void Kiosk_account_can_create_further_instances()
		{
			var pipeName = NewPipeName();
			using var first = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
														PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, AgentPipeSecurity.Create(LocalSystem));
			using var second = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
														PipeOptions.Asynchronous, 0, 0, AgentPipeSecurity.Create(LocalSystem));
			Assert.NotNull(second);
		}

		/// <summary>DACL: LocalSystem čita i piše, nalog procesa pravi instance, mreža je odbijena; nema širokih grupa.</summary>
		[WindowsFact("named pipe i DACL postoje samo na Windows-u")]
		public void Dacl_allows_only_local_system_and_process_account_and_denies_network()
		{
			var security = AgentPipeSecurity.Create(LocalSystem);
			var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
								.Cast<PipeAccessRule>()
								.ToList();

			Assert.True(security.AreAccessRulesProtected);
			var network = Assert.Single(rules, r => r.IdentityReference == new SecurityIdentifier(WellKnownSidType.NetworkSid, null));
			Assert.Equal(System.Security.AccessControl.AccessControlType.Deny, network.AccessControlType);
			Assert.Equal(PipeAccessRights.FullControl, network.PipeAccessRights & PipeAccessRights.FullControl);
			var system = Assert.Single(rules, r => r.IdentityReference == LocalSystem);
			Assert.True(system.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));
			Assert.False(system.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance));
			var owner = Assert.Single(rules, r => r.IdentityReference == CurrentUser);
			Assert.True(owner.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance));
			Assert.False(owner.PipeAccessRights.HasFlag(PipeAccessRights.ChangePermissions));
			foreach (var broad in new[] { WellKnownSidType.WorldSid, WellKnownSidType.AuthenticatedUserSid, WellKnownSidType.BuiltinUsersSid, WellKnownSidType.AnonymousSid }) {
				Assert.DoesNotContain(rules, r => r.IdentityReference == new SecurityIdentifier(broad, null));
			}
			Assert.Equal(3, rules.Count);
		}

		[Fact]
		public async Task Malformed_message_closes_only_that_connection()
		{
			await using var server = StartServer(ReadySource(), out var pipeName);
			await using (var bad = await TestAgentClient.ConnectAsync(pipeName)) {
				await bad.HandshakeAsync();
				await bad.SendRawAsync(Encoding.UTF8.GetBytes("nije json"));
				Assert.Null(await bad.ReadFrameAsync());
			}

			await using var good = await TestAgentClient.ConnectAsync(pipeName);
			await good.HandshakeAsync();
			Assert.Equal(AgentResultCodes.Completed, (await good.RequestAsync(AgentCommands.GetStatus)).ResultCode);
		}

		[Fact]
		public async Task Oversized_frame_closes_connection()
		{
			await using var server = StartServer(ReadySource(), out var pipeName, o => o with { MaxMessageBytes = 4096 });
			await using var client = await TestAgentClient.ConnectAsync(pipeName);
			await client.HandshakeAsync();

			var header = new byte[4];
			BinaryPrimitives.WriteInt32LittleEndian(header, 1_000_000);
			await client.Pipe.WriteAsync(header);

			Assert.Null(await client.ReadFrameAsync());
		}

		[Fact]
		public async Task Client_that_sends_nothing_is_dropped_after_handshake_timeout()
		{
			await using var server = StartServer(ReadySource(), out var pipeName, o => o with { HandshakeTimeout = TimeSpan.FromMilliseconds(300) });
			await using var client = await TestAgentClient.ConnectAsync(pipeName);

			Assert.Null(await client.ReadFrameAsync());
		}

		[Fact]
		public async Task Connection_over_limit_is_refused()
		{
			await using var server = StartServer(ReadySource(), out var pipeName);
			await using var first = await TestAgentClient.ConnectAsync(pipeName);
			await first.HandshakeAsync();
			await using var second = await TestAgentClient.ConnectAsync(pipeName);
			await second.HandshakeAsync();

			await using var third = await TestAgentClient.ConnectAsync(pipeName);
			try {
				await third.SendAsync(new ClientHello());
			}
			catch (IOException) {
				// server je vezu već zatvorio
			}
			Assert.Null(await third.ReadFrameAsync());

			// Postojeće veze rade dalje.
			Assert.Equal(AgentResultCodes.Completed, (await first.RequestAsync(AgentCommands.GetStatus)).ResultCode);
			Assert.Equal(AgentResultCodes.Completed, (await second.RequestAsync(AgentCommands.GetStatus)).ResultCode);
		}

		[Fact]
		public async Task Repeated_subscribe_keeps_one_subscription()
		{
			var source = ReadySource();
			await using var server = StartServer(source, out var pipeName);
			await using var client = await TestAgentClient.ConnectAsync(pipeName);
			await client.HandshakeAsync();

			var first = await client.RequestAsync(AgentCommands.SubscribeStatus);
			var again = await client.RequestAsync(AgentCommands.SubscribeStatus);
			source.SetWorkflowState(S.RecipientCodeInput);
			var changed = await client.ReadEventAsync();

			Assert.Equal(first.SubscriptionId, again.SubscriptionId);
			Assert.Equal(again.Status!.StatusRevision + 1, changed.Status!.StatusRevision);
			Assert.Null(await client.TryReadAsync(TimeSpan.FromMilliseconds(300)));   // bez duplikata
		}

		/// <summary>Uredno gašenje: Agent dobija status ShuttingDown pre zatvaranja veze.</summary>
		[Fact]
		public async Task Shutdown_status_reaches_subscriber_before_close()
		{
			var source = ReadySource();
			var server = StartServer(source, out var pipeName);
			await using var client = await TestAgentClient.ConnectAsync(pipeName);
			await client.HandshakeAsync();
			await client.RequestAsync(AgentCommands.SubscribeStatus);

			source.SetLifecycle(KioskLifecycles.ShuttingDown);
			await server.DisposeAsync();

			var final = await client.ReadEventAsync();
			Assert.Equal(KioskLifecycles.ShuttingDown, final.Status!.Lifecycle);
			Assert.Equal(CannotAcceptReasons.ShuttingDown, final.Status.CannotAcceptReason);
			Assert.Null(await client.ReadFrameAsync());
		}

		/// <summary>
		/// Klijenti koji se povežu i odmah prekinu (i pre nego što server stigne da prihvati vezu): server radi dalje, a ime
		/// pipe-a ni u jednom trenutku ne ostaje slobodno — proces koji bi ga tada zauzeo sa FirstPipeInstance ne uspeva.
		/// </summary>
		[WindowsFact("named pipe i DACL postoje samo na Windows-u")]
		public async Task Abandoned_connections_never_release_the_pipe_name()
		{
			await using var server = StartServer(ReadySource(), out var pipeName);
			using var stop = new CancellationTokenSource();
			int squatAttempts = 0, squatSuccesses = 0;
			var squatter = Task.Run(() => {
				while (!stop.IsCancellationRequested) {
					try {
						using var s = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
																PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
						Interlocked.Increment(ref squatSuccesses);
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
					}
					Interlocked.Increment(ref squatAttempts);
				}
			});

			for (int i = 0; i < 200; i++) {
				using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
				await pipe.ConnectAsync(5000);
			}
			stop.Cancel();
			await squatter;

			Assert.True(squatAttempts > 0);
			Assert.Equal(0, squatSuccesses);
			await using var client = await TestAgentClient.ConnectAsync(pipeName);
			await client.HandshakeAsync();
			Assert.Equal(AgentResultCodes.Completed, (await client.RequestAsync(AgentCommands.GetStatus)).ResultCode);
		}

		[Fact]
		public async Task Server_accepts_new_connection_after_client_disconnects()
		{
			await using var server = StartServer(ReadySource(), out var pipeName);
			for (int i = 0; i < 5; i++) {
				await using var client = await TestAgentClient.ConnectAsync(pipeName);
				await client.HandshakeAsync();
				Assert.Equal(AgentResultCodes.Completed, (await client.RequestAsync(AgentCommands.GetStatus)).ResultCode);
			}
		}

		// v4: test sonde za heartbeat je u DispatcherLivenessProbeTests (menja UiThread.Dispatcher, pa ide u kolekciju UI niti).

		// ─── Pomoćno ───────────────────────────────────────────────────────────

		private static readonly Func<CancellationToken, Task<bool>> AlwaysResponsive = _ => Task.FromResult(true);

		private static string NewPipeName() => $"TopPos.Test.{Guid.NewGuid():N}";

		/// <summary>v4: adresa kanala za server i klijenta — ime pipe-a (Windows) ili putanja socket-a (Linux).</summary>
		private string NewAddress() => OperatingSystem.IsWindows()
											? NewPipeName()
											: Path.Combine(_socketRoot, $"{Guid.NewGuid():N}"[..8], "agent.v1.sock");

		private static KioskStatusSource ReadySource()
		{
			var source = new KioskStatusSource(LockerId, logger: null);
			source.SetLifecycle(KioskLifecycles.Ready);
			source.SetOperational(true);
			source.SetWorkflowState(S.Start);
			return source;
		}

		private static TestOptions Options(string address) => new() {
			PipeName = address,
			SocketPath = address,
		};

		private AgentPipeServer StartServer(KioskStatusSource source, out string pipeName,
													Func<TestOptions, TestOptions>? configure = null,
													Func<CancellationToken, Task<bool>>? probe = null)
		{
			pipeName = NewAddress();
			var options = (configure ?? (o => o))(Options(pipeName));
			var server = new AgentPipeServer(options.ToServerOptions(), source, probe ?? AlwaysResponsive, logger: null);
			Assert.True(server.Start());
			return server;
		}

		/// <summary>Promenljiva kopija opcija za <c>with</c> u testovima.</summary>
		private sealed record TestOptions
		{
			public string PipeName { get; init; } = "";
			public string SocketPath { get; init; } = "";
			public bool AllowCurrentUser { get; init; } = true;
			public int MaxMessageBytes { get; init; } = AgentProtocol.MaxMessageBytes;
			public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);
			public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
			public TimeSpan UiAckTimeout { get; init; } = TimeSpan.FromSeconds(2);

			public static implicit operator AgentPipeServerOptions(TestOptions o) => o.ToServerOptions();

			public AgentPipeServerOptions ToServerOptions() => new() {
				PipeName = PipeName,
				SocketPath = SocketPath,
				AllowCurrentUser = AllowCurrentUser,
				MaxMessageBytes = MaxMessageBytes,
				HandshakeTimeout = HandshakeTimeout,
				HeartbeatInterval = HeartbeatInterval,
				UiAckTimeout = UiAckTimeout,
				WriteTimeout = TimeSpan.FromSeconds(5),
				ShutdownDrainTimeout = TimeSpan.FromSeconds(2),
			};
		}
	}
}
