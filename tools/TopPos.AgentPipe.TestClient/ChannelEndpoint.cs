using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace TopPos.AgentPipe.TestClient
{
	/// <summary>
	/// v4: kraj kanala na strani Agenta — named pipe (Windows, kao v2) ili Unix socket (Linux, ugovor „Transport na Linuxu“).
	/// Povezivanje, ispis ko je server i lažni server koji zauzme ime pre kioska.
	/// </summary>
	internal abstract class ChannelEndpoint
	{
		/// <summary>Adresa kanala za ispis: ime pipe-a ili putanja socket-a.</summary>
		public abstract string Address { get; }

		/// <summary>Ko je ovaj proces (nalog), za ispis pre povezivanja.</summary>
		public abstract string DescribeClient();

		/// <summary>Otvorena veza ili <c>null</c> (razlog je ispisan).</summary>
		public abstract Task<Stream?> ConnectAsync();

		/// <summary>Ispisuje ko je server povezane veze; vraća PID ili <c>null</c> ako nije poznat.</summary>
		public abstract int? DescribeServer();

		/// <summary>Lažni server: zauzme ime kanala zadati broj sekundi. 0 = zauzeto, 2 = nije.</summary>
		public abstract Task<int> SquatAsync(int seconds);
	}

	/// <summary>Named pipe (v2): Agent pre prve poruke proverava da je server kiosk iz instalacije, pod kiosk nalogom.</summary>
	[SupportedOSPlatform("windows")]
	internal sealed class WindowsPipeEndpoint(string pipeName) : ChannelEndpoint
	{
		private NamedPipeClientStream? _pipe;

		public override string Address => pipeName;

		public override string DescribeClient()
		{
			using var identity = WindowsIdentity.GetCurrent();
			return $"{identity.Name} ({identity.User?.Value})";
		}

		public override async Task<Stream?> ConnectAsync()
		{
			var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
			try {
				await pipe.ConnectAsync(5000);
			}
			catch (Exception ex) {
				pipe.Dispose();
				Console.WriteLine($"Pipe {pipeName} nije otvoren: {ex.GetType().Name}: {ex.Message}");
				return null;
			}
			_pipe = pipe;
			return pipe;
		}

		public override int? DescribeServer()
		{
			if (_pipe == null || !GetNamedPipeServerProcessId(_pipe.SafePipeHandle, out uint processId)) {
				Console.WriteLine($"Server: PID nije dostupan ({new Win32Exception(Marshal.GetLastWin32Error()).Message})");
				return null;
			}
			string path;
			try {
				using var process = Process.GetProcessById((int) processId);
				path = process.MainModule?.FileName ?? "?";
			}
			catch (Exception ex) {
				path = $"? ({ex.Message})";
			}
			Console.WriteLine($"Server: PID {processId}, exe {path}");
			return (int) processId;
		}

		// Provera da kiosk tada ne pokreće kanal, i da ga Agent odbija.
		public override async Task<int> SquatAsync(int seconds)
		{
			try {
				using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
															PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
				Console.WriteLine($"Ime {pipeName} je zauzeto na {seconds} s; sada pokrenite kiosk.");
				await Task.Delay(TimeSpan.FromSeconds(seconds));
				return 0;
			}
			catch (Exception ex) {
				Console.WriteLine($"Ime {pipeName} nije zauzeto: {ex.GetType().Name}: {ex.Message}");
				return 2;
			}
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
	}

	/// <summary>
	/// Unix socket (ugovor „Transport na Linuxu“). Server se čita iz <c>SO_PEERCRED</c> povezanog socket-a i iz <c>/proc</c>;
	/// sa izjavom iz <c>status.lock</c> klijent ga proverava kao Agent (tačka 4) — sopstvenim kodom, ne kodom kioska.
	/// </summary>
	[SupportedOSPlatform("linux")]
	internal sealed partial class LinuxSocketEndpoint(string socketPath) : ChannelEndpoint
	{
		/// <summary>Kiosk nalog i izvršni fajl aktivnog izdanja u produkciji (ugovor tačka 4).</summary>
		public const string DefaultKioskUser = "toppos";
		public const string DefaultExecutable = "/opt/toppos-kiosk/current/TopPos.Kiosk";

		private const int __solSocket = 1;
		private const int __soPeerCred = 17;   // x86_64 i arm64 (asm-generic)
		private const int __lockExclusive = 2;
		private const int __lockNonBlocking = 4;
		private const int __openReadWrite = 2;
		private const int __openNonBlocking = 0x800;       // O_NONBLOCK: FIFO ne zadržava otvaranje ni čitanje
		private const int __openCloseOnExec = 0x80000;
		private const int __maxDeclarationBytes = 64 * 1024;

		private Socket? _socket;
		private ServerProcess? _server;

		public override string Address => socketPath;

		public override string DescribeClient() => $"{Environment.UserName} (uid {geteuid()})";

		public override async Task<Stream?> ConnectAsync()
		{
			var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
			try {
				using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
				await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cts.Token);
			}
			catch (Exception ex) {
				socket.Dispose();
				Console.WriteLine($"Socket {socketPath} nije otvoren: {ex.GetType().Name}: {ex.Message}");
				return null;
			}
			_socket = socket;
			return new NetworkStream(socket, ownsSocket: true);
		}

		public override int? DescribeServer()
		{
			if (_socket == null) {
				return null;
			}
			uint length = (uint) Marshal.SizeOf<UCred>();
			if (getsockopt((int) _socket.Handle, __solSocket, __soPeerCred, out var credentials, ref length) != 0) {
				Console.WriteLine($"Server: SO_PEERCRED nije pročitan (errno {Marshal.GetLastPInvokeError()})");
				return null;
			}
			_server = ServerProcess.Read(credentials);
			Console.WriteLine($"Server: PID {_server.Pid}, uid {_server.Uid}, gid {_server.Gid}, exe {_server.Executable ?? "?"}, "
							  + $"starttime {_server.StartTicks?.ToString() ?? "?"}, boot_id {_server.BootId ?? "?"}");
			return _server.Pid;
		}

		/// <summary>
		/// Provera servera kao Agent (ugovor tačka 4) uz izjavu iz <paramref name="statusLockPath"/>: PID, <c>boot_id</c> i
		/// <c>starttime</c> procesa jednaki izjavi, socket je onaj iz izjave, nalog i izvršni fajl su kiosk-ovi (podrazumevano
		/// <see cref="DefaultKioskUser"/> i <see cref="DefaultExecutable"/>, inače zadati — npr. E2E i testovi). Vraća opis
		/// provere i spisak nepoklapanja (prazan = server je proveren). Poziva se posle <see cref="DescribeServer"/>.
		/// </summary>
		public (string Expected, List<string> Errors) VerifyServer(string statusLockPath, uint? expectedUid, string? expectedExecutable)
		{
			var errors = new List<string>();
			uint? uid = expectedUid ?? UidOf(DefaultKioskUser);
			var executable = expectedExecutable ?? DefaultExecutable;
			var expected = $"uid {uid?.ToString() ?? "?"}, exe {executable}";
			if (_server == null) {
				errors.Add("identitet servera nije pročitan");
				return (expected, errors);
			}
			Declaration declaration;
			try {
				declaration = Declaration.Read(statusLockPath);
			}
			catch (Exception ex) {
				errors.Add($"izjava {statusLockPath} nije pročitana: {ex.Message}");
				return (expected, errors);
			}
			if (declaration.ProcessId != _server.Pid) {
				errors.Add($"PID servera {_server.Pid}, a izjava kaže {declaration.ProcessId?.ToString() ?? "-"}");
			}
			if (declaration.BootId == null || declaration.BootId != _server.BootId) {
				errors.Add($"boot_id {_server.BootId ?? "?"}, a izjava kaže {declaration.BootId ?? "-"}");
			}
			if (declaration.StartTicks == null || declaration.StartTicks != _server.StartTicks) {
				errors.Add($"starttime servera {_server.StartTicks?.ToString() ?? "?"}, a izjava kaže {declaration.StartTicks?.ToString() ?? "-"}");
			}
			if (declaration.SocketPath != socketPath) {
				errors.Add($"socket {socketPath}, a izjava kaže {declaration.SocketPath ?? "-"}");
			}
			if (uid == null) {
				errors.Add($"uid servera {_server.Uid}, a nalog {DefaultKioskUser} ne postoji");
			}
			else if (_server.Uid != uid) {
				errors.Add($"uid servera {_server.Uid}, očekuje se {uid}");
			}
			var realExecutable = RealPath(executable);
			if (realExecutable == null || realExecutable != _server.Executable) {
				errors.Add($"exe servera {_server.Executable ?? "?"}, očekuje se {realExecutable ?? executable + " (ne postoji)"}");
			}
			return (expected, errors);
		}

		/// <summary>
		/// flock na lock fajlu pored socket-a (kao kiosk): dok ga drži ovaj proces, kiosk ne pokreće kanal. Folder i lock pravi samo
		/// nalog koji nije root (kiosk nalog, folder <c>0700</c> kao kiosk); root ih ne pravi — kiosk ne bi mogao da ih koristi
		/// posle probe.
		/// </summary>
		public override async Task<int> SquatAsync(int seconds)
		{
			var lockPath = Path.ChangeExtension(socketPath, ".lock");
			bool root = geteuid() == 0;
			try {
				var folder = Path.GetDirectoryName(socketPath) ?? throw new IOException($"Putanja {socketPath} nema folder.");
				if (!root && !Directory.Exists(folder)) {
					Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
				}
				using var file = root ? OpenExistingLock(lockPath) : new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
				if (file == null) {
					return 2;
				}
				if (flock((int) file.SafeFileHandle.DangerousGetHandle(), __lockExclusive | __lockNonBlocking) != 0) {
					Console.WriteLine($"{lockPath} nije zaključan (errno {Marshal.GetLastPInvokeError()}).");
					return 2;
				}
				Console.WriteLine($"{lockPath} je zaključan na {seconds} s; sada pokrenite kiosk.");
				await Task.Delay(TimeSpan.FromSeconds(seconds));
				return 0;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				Console.WriteLine($"{lockPath} nije zaključan: {ex.GetType().Name}: {ex.Message}");
				return 2;
			}
		}

		// Root u folderu kiosk naloga: samo postojeći običan fajl, bez praćenja linka i bez pravljenja (kiosk nalog ne bi mogao
		// da otvori fajl koji napravi root).
		private static FileStream? OpenExistingLock(string lockPath)
		{
			int fd = open(lockPath, __openReadWrite | Declaration.OpenNoFollow | __openNonBlocking | __openCloseOnExec);
			if (fd < 0) {
				Console.WriteLine($"{lockPath} nije otvoren (errno {Marshal.GetLastPInvokeError()}); root ga ne pravi i ne prati link — "
								  + "pokrenite --squat kao kiosk nalog.");
				return null;
			}
			var file = new FileStream(new SafeFileHandle(fd, ownsHandle: true), FileAccess.ReadWrite, bufferSize: 0);
			if (!file.CanSeek) {
				file.Dispose();
				Console.WriteLine($"{lockPath} nije običan fajl.");
				return null;
			}
			return file;
		}

		// realpath(3): putanja bez linkova (npr. /opt/toppos-kiosk/current → izdanje), kao /proc/<pid>/exe.
		private static string? RealPath(string path)
		{
			var resolved = realpath(path, IntPtr.Zero);
			if (resolved == IntPtr.Zero) {
				return null;
			}
			try {
				return Marshal.PtrToStringUTF8(resolved);
			}
			finally {
				free(resolved);
			}
		}

		// getpwnam(3): pw_uid je posle dva pokazivača (pw_name, pw_passwd).
		private static uint? UidOf(string user)
		{
			var entry = getpwnam(user);
			return entry == IntPtr.Zero ? null : (uint) Marshal.ReadInt32(entry, 2 * IntPtr.Size);
		}

		/// <summary>Proces servera: <c>SO_PEERCRED</c> i podaci iz <c>/proc</c> (bez prava root-a deo može biti nedostupan).</summary>
		private sealed record ServerProcess(int Pid, uint Uid, uint Gid, string? Executable, long? StartTicks, string? BootId)
		{
			public static ServerProcess Read(UCred credentials)
				=> new(credentials.Pid, credentials.Uid, credentials.Gid,
						Try(() => new FileInfo($"/proc/{credentials.Pid}/exe").LinkTarget),
						Try<long?>(() => StartTicksOf(File.ReadAllText($"/proc/{credentials.Pid}/stat"))),
						Try(() => File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim()));

			// Polje 22 iz /proc/<pid>/stat; ime procesa (polje 2) sme da ima razmake i zagrade, pa se broji od poslednje ')'.
			private static long StartTicksOf(string stat)
			{
				var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
				return long.Parse(fields[22 - 3], System.Globalization.CultureInfo.InvariantCulture);
			}

			private static T? Try<T>(Func<T?> read)
			{
				try {
					return read();
				}
				catch (Exception) {
					return default;
				}
			}
		}

		/// <summary>Polja izjave iz <c>status.lock</c> koja Agent koristi na Linuxu (ugovor tačka 5); neispravno polje = nema ga.</summary>
		private sealed record Declaration(int? ProcessId, string? BootId, long? StartTicks, string? SocketPath)
		{
			// Fajl piše kiosk nalog: otvara se jednom, bez praćenja linka (O_NOFOLLOW) i bez čekanja na FIFO (O_NONBLOCK), a čita se
			// ograničena količina — sve drugo (link, folder, uređaj, preveliko) je greška.
			public static Declaration Read(string path)
			{
				int fd = open(path, OpenNoFollow | __openNonBlocking | __openCloseOnExec);
				if (fd < 0) {
					throw new IOException($"nije otvorena bez praćenja linka (errno {Marshal.GetLastPInvokeError()})");
				}
				using var handle = new SafeFileHandle(fd, ownsHandle: true);
				using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
				if (!stream.CanSeek) {
					throw new InvalidDataException("nije običan fajl");
				}
				var buffer = new byte[__maxDeclarationBytes + 1];
				int total = 0, read;
				while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) {
					total += read;
				}
				if (total > __maxDeclarationBytes) {
					throw new InvalidDataException($"veća od {__maxDeclarationBytes} bajtova");
				}
				using var document = JsonDocument.Parse(buffer.AsMemory(0, total));
				var root = document.RootElement;
				if (root.ValueKind != JsonValueKind.Object) {
					throw new InvalidDataException("koren nije JSON objekat");
				}
				return new(root.TryGetProperty("ProcessId", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var p) && p > 0 ? p : null,
							Text(root, "ProcessBootId"),
							root.TryGetProperty("ProcessStartTicks", out var start) && start.ValueKind == JsonValueKind.Number && start.TryGetInt64(out var s) ? s : null,
							root.TryGetProperty("AgentChannel", out var channel) && channel.ValueKind == JsonValueKind.Object ? Text(channel, "SocketPath") : null);
			}

			// O_NOFOLLOW zavisi od arhitekture (asm-generic za arm64, x86 svoj).
			public static int OpenNoFollow => RuntimeInformation.ProcessArchitecture switch {
				Architecture.X64 => 0x20000,
				Architecture.Arm64 => 0x8000,
				var other => throw new PlatformNotSupportedException($"O_NOFOLLOW za {other}"),
			};

			private static string? Text(JsonElement element, string name)
				=> element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct UCred
		{
			public int Pid;
			public uint Uid;
			public uint Gid;
		}

		[LibraryImport("libc", SetLastError = true)]
		private static partial int getsockopt(int socket, int level, int optionName, out UCred optionValue, ref uint optionLength);

		[LibraryImport("libc", SetLastError = true)]
		private static partial int flock(int fd, int operation);

		[LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
		private static partial int open(string path, int flags);

		[LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
		private static partial IntPtr realpath(string path, IntPtr resolvedPath);

		[LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
		private static partial IntPtr getpwnam(string name);

		[LibraryImport("libc")]
		private static partial void free(IntPtr pointer);

		[LibraryImport("libc")]
		private static partial uint geteuid();
	}
}
