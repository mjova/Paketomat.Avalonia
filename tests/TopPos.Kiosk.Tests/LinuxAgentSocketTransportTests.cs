using System.Net.Sockets;
using System.Runtime.Versioning;
using TopPos.Platform;
using TopPos.Platform.Linux;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// v4: Unix socket kanala ka Agentu po ugovoru „Transport na Linuxu“ (tačke 1–3): folder <c>0700</c>, socket <c>0600</c>,
	/// <c>agent.v1.lock</c> umesto <c>FirstPipeInstance</c>, zaostali socket posle pada se briše, uredan izlaz ga briše.
	/// </summary>
	[SupportedOSPlatform("linux")]
	public sealed class LinuxAgentSocketTransportTests : IDisposable
	{
		private const UnixFileMode __ownerOnlyFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

		private readonly string _root = Path.Combine(Path.GetTempPath(), $"toppos-sock-{Guid.NewGuid():N}"[..20]);

		private string SocketPath => Path.Combine(_root, "run", "agent.v1.sock");

		public void Dispose()
		{
			if (Directory.Exists(_root)) {
				Directory.Delete(_root, recursive: true);
			}
		}

		[LinuxFact]
		public async Task Start_creates_owner_only_folder_and_socket_and_exit_removes_the_socket()
		{
			var transport = Create();

			Assert.True(transport.TryStart(out string error), error);
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(SocketPath)!));
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SocketPath));
			Assert.True(File.Exists(LinuxAgentSocketTransport.LockPathOf(SocketPath)));
			Assert.EndsWith("/run/agent.v1.lock", LinuxAgentSocketTransport.LockPathOf(SocketPath));

			await transport.DisposeAsync();

			Assert.False(File.Exists(SocketPath));
		}

		/// <summary>Izlaz briše samo socket ove instance: unos koji ga je u međuvremenu zamenio ostaje.</summary>
		[LinuxFact]
		public async Task Exit_keeps_an_entry_that_replaced_the_socket()
		{
			var transport = Create();
			Assert.True(transport.TryStart(out string error), error);
			File.Delete(SocketPath);
			File.WriteAllText(SocketPath, "zamena");

			await transport.DisposeAsync();

			Assert.Equal("zamena", File.ReadAllText(SocketPath));
		}

		[LinuxFact]
		public async Task Second_instance_does_not_start_while_the_lock_is_held()
		{
			await using var first = Create();
			Assert.True(first.TryStart(out _));

			await using var second = Create();
			Assert.False(second.TryStart(out string error));
			Assert.Contains("agent.v1.lock", error);

			// Prvi i dalje prima veze: drugi nije obrisao njegov socket.
			using var client = await ConnectAsync();
		}

		[LinuxFact]
		public async Task Stale_socket_left_after_crash_is_replaced()
		{
			// Posle pada socket fajl ostaje. .NET ga pri urednom zatvaranju briše sam, pa se ovde premesti na putanju kanala pre
			// zatvaranja (rename radi i za socket) — ostaje pravi socket na kome niko ne sluša.
			Directory.CreateDirectory(Path.GetDirectoryName(SocketPath)!, __ownerOnlyFolder);
			var elsewhere = Path.Combine(Path.GetDirectoryName(SocketPath)!, "crashed.sock");
			using (var crashed = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)) {
				crashed.Bind(new UnixDomainSocketEndPoint(elsewhere));
				File.Move(elsewhere, SocketPath);
			}
			Assert.Equal(LinuxFileStatus.Socket, LinuxFileStatus.Get(SocketPath)?.Type);
			ulong staleInode = LinuxFileStatus.Get(SocketPath)!.Value.Inode;

			await using var transport = Create();
			Assert.True(transport.TryStart(out string error), error);

			Assert.NotEqual(staleInode, LinuxFileStatus.Get(SocketPath)!.Value.Inode);
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SocketPath));
			using var client = await ConnectAsync();
			await using var connection = await transport.AcceptAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
			Assert.NotNull(connection.Stream);
			Assert.Equal(["agent.v1.lock", "agent.v1.sock"], Directory.GetFileSystemEntries(Path.GetDirectoryName(SocketPath)!).Select(Path.GetFileName).Order());
		}

		[LinuxFact]
		public async Task Regular_file_or_link_at_the_socket_path_is_kept_and_the_channel_does_not_start()
		{
			Directory.CreateDirectory(Path.GetDirectoryName(SocketPath)!, __ownerOnlyFolder);
			File.WriteAllText(SocketPath, "podaci");

			await using (var transport = Create()) {
				Assert.False(transport.TryStart(out string error));
				Assert.Contains("nije socket", error);
			}
			Assert.Equal("podaci", File.ReadAllText(SocketPath));

			File.Delete(SocketPath);
			var target = Path.Combine(_root, "target");
			File.WriteAllText(target, "cilj");
			File.CreateSymbolicLink(SocketPath, target);
			await using (var transport = Create()) {
				Assert.False(transport.TryStart(out _));
			}
			Assert.Equal("cilj", File.ReadAllText(target));
			Assert.NotNull(new FileInfo(SocketPath).LinkTarget);
		}

		[LinuxTheory]
		[InlineData("755")]    // otvoren drugima
		[InlineData("300")]    // samo vlasnik, ali nije 0700
		public async Task Existing_folder_other_than_0700_is_refused_and_left_unchanged(string octal)
		{
			var folder = Path.GetDirectoryName(SocketPath)!;
			var shared = (UnixFileMode) Convert.ToInt32(octal, 8);
			Directory.CreateDirectory(folder);
			File.SetUnixFileMode(folder, shared);

			await using var transport = Create();
			Assert.False(transport.TryStart(out string error));

			Assert.Contains("0700", error);
			Assert.Equal(shared, File.GetUnixFileMode(folder));
			File.SetUnixFileMode(folder, __ownerOnlyFolder);    // da se vidi sadržaj i obriše posle testa
			Assert.False(File.Exists(LinuxAgentSocketTransport.LockPathOf(SocketPath)));
		}

		/// <summary>
		/// Ugovor tačka 3: identitet odmah posle accept. Klijent koji nije root zatvara se bez odgovora i pre nego što pošalje
		/// ijedan bajt, pa ne zauzima mesto Agenta do isteka handshake roka.
		/// </summary>
		[NotRootFact(LinuxOnly = true)]
		public async Task Client_that_is_not_root_is_closed_right_after_accept()
		{
			await using var transport = Create(allowCurrentUser: false);
			Assert.True(transport.TryStart(out _));
			using var acceptCts = new CancellationTokenSource();
			var accept = transport.AcceptAsync(acceptCts.Token).AsTask();

			using var client = await ConnectAsync();
			var buffer = new byte[1];
			using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			int read = await client.ReceiveAsync(buffer, SocketFlags.None, readCts.Token);

			Assert.Equal(0, read);     // server je zatvorio vezu
			Assert.False(accept.IsCompleted);   // odbijena veza ne stiže do servera
			acceptCts.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accept);
		}

		[LinuxFact]
		public async Task Peer_credentials_identify_the_client_process_and_user()
		{
			await using var transport = Create();
			Assert.True(transport.TryStart(out _));

			using var client = await ConnectAsync();
			await using var connection = await transport.AcceptAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
			var peer = connection.GetClient();

			Assert.Equal(Environment.ProcessId, peer.ProcessId);
			Assert.True(peer.IsAllowed);    // AllowCurrentUser
			Assert.StartsWith("uid ", peer.Identity);
		}

		private LinuxAgentSocketTransport Create(bool allowCurrentUser = true) => new(new AgentChannelTransportOptions {
																								PipeName = "",
																								SocketPath = SocketPath,
																								AllowCurrentUser = allowCurrentUser,
																							}, logger: null);

		private async Task<Socket> ConnectAsync()
		{
			var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), cts.Token);
			return socket;
		}
	}
}
