using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace TopPos.Platform.Linux
{
	/// <summary>
	/// Unix domain socket sa putanjom (ugovor v2 <c>doc/notes/Info/Ugovor pipe komandi/Transport na Linuxu.md</c>, tačke 1–3):
	/// <list type="bullet">
	/// <item>folder socket-a mora biti pravi folder kiosk naloga sa pravima <c>0700</c> (u servisu ga pravi systemd
	/// <c>RuntimeDirectory=</c>, inače ga pravi kiosk); postojeći folder sa drugim vlasnikom ili širim pravima se ne menja,
	/// već se kanal ne pokreće;</item>
	/// <item>pre <c>bind</c> se uzima <c>flock(LOCK_EX | LOCK_NB)</c> na <c>agent.v1.lock</c> pored socket-a i drži dok kanal
	/// radi (zamena za <c>FirstPipeInstance</c>): zauzet → kanal se ne pokreće; dobijen → zaostali socket (posle pada) se
	/// zamenjuje — samo ako je to socket, a ne običan fajl ili link;</item>
	/// <item>socket se pravi pod privremenim imenom u istom folderu, dobija <c>0600</c> i tek onda se preimenuje u javno ime
	/// (<c>rename</c> atomski zamenjuje zaostali socket). Ugovor predlaže <c>umask</c> pre <c>bind</c>, ali <c>umask</c> važi za
	/// ceo proces; ovako socket pod javnim imenom od prvog trenutka ima <c>0600</c>. .NET pri zatvaranju briše putanju na
	/// kojoj je napravio socket — to je privremeno ime, pa ne može da obriše ništa pod javnim;</item>
	/// <item>klijent se proverava preko <c>SO_PEERCRED</c> odmah posle <c>accept</c>: prihvata se samo <c>uid 0</c> (Agent je root
	/// servis); odbijena veza se zatvara bez odgovora i ne zauzima mesto Agenta.</item>
	/// </list>
	/// Uredan izlaz briše socket ako je pod javnim imenom i dalje socket ove instance (isti inode); posle pada fajl ostaje, a
	/// Agent dobija <c>ECONNREFUSED</c> („kanal nije dostupan“).
	/// </summary>
	[SupportedOSPlatform("linux")]
	internal sealed partial class LinuxAgentSocketTransport(AgentChannelTransportOptions options, ILogger? logger) : IAgentChannelTransport
	{
		private const int __solSocket = 1;
		private const int __soPeerCred = 17;   // x86_64 i arm64 (asm-generic); kiosk drugu arhitekturu ne cilja
		private const int __lockExclusive = 2;
		private const int __lockNonBlocking = 4;
		private const int __eWouldBlock = 11;

		private const UnixFileMode __ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		private const UnixFileMode __ownerOnlyFolder = __ownerOnly | UnixFileMode.UserExecute;

		private readonly uint _allowedUid = options.AllowCurrentUser ? geteuid() : 0;
		private FileStream? _lock;
		private Socket? _listener;
		private ulong? _publishedInode;

		public string Address => options.SocketPath;

		public string AllowedClient => $"uid {_allowedUid}";

		/// <summary>Lock fajl pored socket-a: <c>agent.v1.sock</c> → <c>agent.v1.lock</c>.</summary>
		internal static string LockPathOf(string socketPath) => Path.ChangeExtension(socketPath, ".lock");

		public bool TryStart(out string error)
		{
			var socketPath = options.SocketPath;
			var lockPath = LockPathOf(socketPath);
			try {
				var folder = Path.GetDirectoryName(socketPath) ?? throw new IOException($"Putanja {socketPath} nema folder.");
				if (!Directory.Exists(folder) && LinuxFileStatus.Get(folder) == null) {
					Directory.CreateDirectory(folder, __ownerOnlyFolder);
				}
				if (CheckFolder(folder) is { } folderError) {
					error = folderError;
					return false;
				}
				if (!TryLock(lockPath, out error)) {
					return false;
				}
				// Jedini vlasnik: socket koji postoji ostao je posle pada (i privremeni posle pada pri startu). Sve drugo na tim
				// putanjama je greška podešavanja i ne dira se.
				var temporaryPath = Path.Combine(folder, $".agent.{Environment.ProcessId}");
				foreach (var path in new[] { socketPath, temporaryPath }) {
					var existing = LinuxFileStatus.Get(path);
					if (existing is { Type: not LinuxFileStatus.Socket }) {
						Release();
						error = $"na putanji {path} je fajl koji nije socket; ne briše se";
						return false;
					}
					if (existing != null && path == socketPath) {
						logger?.LogInformation("Agent socket: zaostali {SocketPath} se zamenjuje.", socketPath);
					}
				}
				File.Delete(temporaryPath);
				var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
				try {
					listener.Bind(new UnixDomainSocketEndPoint(temporaryPath));
					File.SetUnixFileMode(temporaryPath, __ownerOnly);
					listener.Listen(options.MaxConnections + 2);
					File.Move(temporaryPath, socketPath, overwrite: true);
					_publishedInode = LinuxFileStatus.Get(socketPath)?.Inode;
				}
				catch {
					listener.Dispose();
					throw;
				}
				_listener = listener;
				error = "";
				return true;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SocketException) {
				Release();
				error = ex.Message;
				return false;
			}
		}

		public async ValueTask<IAgentChannelConnection> AcceptAsync(CancellationToken cancellationToken)
		{
			var listener = _listener ?? throw new InvalidOperationException("Kanal nije pokrenut.");
			var delay = TimeSpan.FromMilliseconds(250);
			while (true) {
				Socket socket;
				try {
					socket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
				}
				catch (SocketException ex) when (!cancellationToken.IsCancellationRequested) {
					// Klijent je odustao pre prihvatanja ili je proces privremeno bez deskriptora: ponavlja se.
					logger?.LogWarning("Agent socket: neuspelo prihvatanje veze, ponavlja se za {Delay}: {Error}", delay, ex.Message);
					await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
					delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(5).Ticks));
					continue;
				}
				delay = TimeSpan.FromMilliseconds(250);

				// Identitet odmah posle accept (ugovor tačka 3): odbijen klijent ne zauzima mesto Agenta do isteka handshake roka.
				AgentChannelClient client;
				try {
					client = ReadPeer(socket, _allowedUid);
				}
				catch (InvalidOperationException ex) {
					logger?.LogWarning("Agent socket: identitet klijenta nije moguće utvrditi; veza se odbija: {Error}", ex.Message);
					socket.Dispose();
					continue;
				}
				if (!client.IsAllowed) {
					logger?.LogWarning("Agent socket: odbijen klijent {Client} (PID {ProcessId}); dozvoljen je samo {Allowed}.",
										client.Identity, client.ProcessId, AllowedClient);
					socket.Dispose();
					continue;
				}
				return new Connection(socket, client);
			}
		}

		public ValueTask DisposeAsync()
		{
			Release();
			return ValueTask.CompletedTask;
		}

		// Pravi folder kiosk naloga, bez prava za grupu i ostale; inače bi drugi nalog mogao da ukloni socket ili lock.
		private static string? CheckFolder(string folder)
		{
			var status = LinuxFileStatus.Get(folder);
			if (status is not { Type: LinuxFileStatus.Directory }) {
				return $"{folder} nije folder (ili je link)";
			}
			if (status.Value.Uid != geteuid() || status.Value.Permissions != Convert.ToUInt32("700", 8)) {
				return $"folder {folder} mora pripadati kiosk nalogu (uid {geteuid()}) sa pravima 0700, a ima uid {status.Value.Uid} i "
						+ $"prava {Convert.ToString(status.Value.Permissions, 8)}";
			}
			return null;
		}

		// flock se traži izričito: .NET pri otvaranju sa FileShare.None ignoriše neke greške zaključavanja (npr. ENOLCK) i kad
		// je zaključavanje isključeno (DOTNET_SYSTEM_IO_DISABLEFILELOCKING) otvara fajl bez njega.
		private bool TryLock(string lockPath, out string error)
		{
			try {
				_lock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			}
			catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException) {
				error = $"{lockPath} drži druga instanca ({ex.Message})";
				return false;
			}
			if (flock((int) _lock.SafeFileHandle.DangerousGetHandle(), __lockExclusive | __lockNonBlocking) != 0) {
				int errno = Marshal.GetLastPInvokeError();
				_lock.Dispose();
				_lock = null;
				error = errno == __eWouldBlock ? $"{lockPath} drži druga instanca" : $"{lockPath} nije zaključan (errno {errno})";
				return false;
			}
			error = "";
			return true;
		}

		// Socket se briše pre puštanja lock-a: posle toga ga druga instanca sme da napravi, pa ne sme biti obrisan. Briše se
		// samo socket ove instance: unos koji ga je u međuvremenu zamenio ostaje.
		private void Release()
		{
			try {
				_listener?.Dispose();
			}
			catch (Exception) { }
			_listener = null;
			if (_publishedInode is { } inode) {
				_publishedInode = null;
				try {
					if (LinuxFileStatus.Get(options.SocketPath) is { Type: LinuxFileStatus.Socket } current && current.Inode == inode) {
						File.Delete(options.SocketPath);
					}
					else {
						logger?.LogWarning("Agent socket: {SocketPath} više nije socket ove instance; ne briše se.", options.SocketPath);
					}
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
					logger?.LogWarning("Agent socket: {SocketPath} nije obrisan: {Error}", options.SocketPath, ex.Message);
				}
			}
			_lock?.Dispose();
			_lock = null;
		}

		private static unsafe AgentChannelClient ReadPeer(Socket socket, uint allowedUid)
		{
			UCred credentials;
			uint length = (uint) sizeof(UCred);
			if (getsockopt((int) socket.Handle, __solSocket, __soPeerCred, &credentials, ref length) != 0) {
				throw new InvalidOperationException($"SO_PEERCRED nije pročitan (errno {Marshal.GetLastPInvokeError()}).");
			}
			return new AgentChannelClient($"uid {credentials.Uid}", credentials.Pid, credentials.Uid == allowedUid);
		}

		private sealed class Connection(Socket socket, AgentChannelClient client) : IAgentChannelConnection
		{
			public Stream Stream { get; } = new NetworkStream(socket, ownsSocket: true);

			/// <summary>Identitet pročitan pri prihvatanju (samo dozvoljen klijent stiže do servera).</summary>
			public AgentChannelClient GetClient() => client;

			public ValueTask DisposeAsync() => Stream.DisposeAsync();
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct UCred
		{
			public int Pid;
			public uint Uid;
			public uint Gid;
		}

		[LibraryImport("libc", SetLastError = true)]
		private static unsafe partial int getsockopt(int socket, int level, int optionName, void* optionValue, ref uint optionLength);

		[LibraryImport("libc", SetLastError = true)]
		private static partial int flock(int fd, int operation);

		[LibraryImport("libc")]
		internal static partial uint geteuid();
	}
}
