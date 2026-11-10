using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TopPos.Simulators.Serial
{
	/// <summary>
	/// Virtuelni serijski par na Linuxu (kao com0com na Windows-u): dva pseudo-terminala čije master strane simulator
	/// povezuje. Kiosk otvara <see cref="KioskPortName"/> (symlink na stabilnoj putanji), simulator otvara
	/// <see cref="SimulatorPortName"/> svojim kodom iz v2 (<c>SerialPort</c>, <c>SerialClient</c>) — bez izmene tog koda.
	/// Bajtovi idu samo dok su obe strane otvorene: pty bez otvorenog slave-a javlja POLLHUP, a ono što bi u njega
	/// bilo upisano odbacuje se (kao kabl bez uređaja na drugom kraju; inače bi kiosk pri otvaranju pročitao zaostale bajtove).
	/// Signalne linije (DTR/RTS/CTS) pty nema — kiosk ih ne koristi.
	/// Ono što kiosk upiše pre nego što pumpa vidi otvorenu stranu simulatora se gubi (kao uređaj koji još nije uključen);
	/// zato simulator otvara svoju stranu pre nego što javi da je spreman (<see cref="WaitForSimulator"/>).
	/// </summary>
	public sealed class PtySerialPair : IDisposable
	{
		private const int O_RDWR = 0x2, O_NOCTTY = 0x100, O_NONBLOCK = 0x800;
		private const short POLLIN = 0x1, POLLHUP = 0x10;
		private const int EAGAIN = 11, EINTR = 4;
		private static readonly TimeSpan __writeLimit = TimeSpan.FromSeconds(1);

		private readonly Pty _kiosk;
		private readonly Pty _simulator;
		private readonly ILogger? _logger;
		private readonly Thread _pump;
		private volatile bool _stopping;
		private readonly ManualResetEventSlim _simulatorSeenOpen = new();

		/// <param name="kioskPortName">Putanja symlink-a koji otvara kiosk; postojeći symlink se zamenjuje, a običan fajl ne.</param>
		[SupportedOSPlatform("linux")]
		public PtySerialPair(string kioskPortName, ILogger? logger)
		{
			_logger = logger;
			KioskPortName = Path.GetFullPath(kioskPortName);
			if (File.Exists(KioskPortName) && new FileInfo(KioskPortName).LinkTarget == null) {
				throw new IOException($"{KioskPortName} postoji i nije symlink — ne zamenjuje se");
			}

			_kiosk = Pty.Open();
			try {
				_simulator = Pty.Open();
				Directory.CreateDirectory(Path.GetDirectoryName(KioskPortName)!);
				File.Delete(KioskPortName);
				File.CreateSymbolicLink(KioskPortName, _kiosk.SlaveName);
			}
			catch {
				_kiosk.Dispose();
				_simulator?.Dispose();
				throw;
			}

			_pump = new Thread(Pump) { IsBackground = true, Name = $"pty {Path.GetFileName(KioskPortName)}" };
			_pump.Start();
			_logger?.LogInformation("Serijski par: kiosk {Kiosk} → {KioskSlave}, simulator {Simulator}", KioskPortName, _kiosk.SlaveName, _simulator.SlaveName);
		}

		/// <summary>Port koji otvara kiosk (symlink).</summary>
		public string KioskPortName { get; }

		/// <summary>Port koji otvara simulator (/dev/pts/N).</summary>
		public string SimulatorPortName => _simulator.SlaveName;

		/// <summary>
		/// Bajtovi idu ka strani samo ako ju je pumpa već videla otvorenu (i sveža provera pre svakog upisa kaže da je još
		/// otvorena); inače se odbacuju. Kad pumpa vidi da se strana upravo otvorila, prvo odbacuje ono što je za nju čekalo u
		/// masteru druge strane, pa tek onda prosleđuje (Codex: inače bi bajt poslat dok je kiosk bio zatvoren stigao posle
		/// otvaranja, a ECR bi ga pročitao kao odgovor na sledeću komandu). Zatvorena strana (POLLHUP) je van poll-a — POLLHUP
		/// bi ga budio odmah — i proverava se na 20 ms.
		/// Granica: pumpa ne može da dokaže da strana nije zatvorena i ponovo otvorena između dve njene provere (ako nit ne dobije
		/// procesor tokom celog niza zatvori–pošalji–otvori). ECR između transakcija drži port zatvoren sekundama i prazni ulaz
		/// pre svakog upisa; isti prozor postoji i na pravom portu (zakasneo okvir koji stigne odmah posle otvaranja se čita).
		/// </summary>
		private void Pump()
		{
			var fds = new PollFd[2];
			var buffer = new byte[4096];
			bool kioskWasOpen = false, simulatorWasOpen = false;
			while (!_stopping) {
				bool kioskOpen = IsOpen(_kiosk.Master);
				bool simulatorOpen = IsOpen(_simulator.Master);
				if (kioskOpen && !kioskWasOpen) {
					DiscardPending(_simulator.Master, buffer);
				}
				if (simulatorOpen && !simulatorWasOpen) {
					DiscardPending(_kiosk.Master, buffer);
					_simulatorSeenOpen.Set();
				}
				(kioskWasOpen, simulatorWasOpen) = (kioskOpen, simulatorOpen);

				fds[0] = new PollFd { Fd = kioskOpen ? _kiosk.Master : -1, Events = POLLIN };
				fds[1] = new PollFd { Fd = simulatorOpen ? _simulator.Master : -1, Events = POLLIN };
				if (poll(fds, 2, kioskOpen && simulatorOpen ? 100 : 20) < 0 && Marshal.GetLastPInvokeError() != EINTR) {
					_logger?.LogError("Serijski par {Port}: poll nije uspeo ({Errno})", KioskPortName, Marshal.GetLastPInvokeError());
					Thread.Sleep(100);
					continue;
				}
				// zatvaranje viđeno u ovom poll-u važi odmah: ništa ka toj strani, a sledeće otvaranje opet prazni zaostale bajtove
				if ((fds[0].Revents & POLLHUP) != 0) {
					kioskOpen = kioskWasOpen = false;
				}
				if ((fds[1].Revents & POLLHUP) != 0) {
					simulatorOpen = simulatorWasOpen = false;
				}
				Forward(_kiosk.Master, _simulator.Master, simulatorOpen, buffer);
				Forward(_simulator.Master, _kiosk.Master, kioskOpen, buffer);
			}
		}

		/// <summary>
		/// Čeka da pumpa vidi otvorenu stranu simulatora. Do tada bi bajtovi kioska bili odbačeni, pa simulator javlja da je
		/// spreman (READY) tek posle ovoga.
		/// </summary>
		public bool WaitForSimulator(TimeSpan timeout) => _simulatorSeenOpen.Wait(timeout);

		/// <summary>Odbacuje bajtove koji čekaju u masteru izvora (namenjeni strani koja se upravo otvorila).</summary>
		private static void DiscardPending(int sourceMaster, byte[] buffer)
		{
			while (read(sourceMaster, buffer, buffer.Length) > 0) {
			}
		}

		private static bool IsOpen(int master)
		{
			var fds = new PollFd[] { new() { Fd = master } };
			return poll(fds, 1, 0) >= 0 && (fds[0].Revents & POLLHUP) == 0;
		}

		/// <summary>
		/// Sve što je sad u <paramref name="from"/> ide u <paramref name="to"/>, ako je pumpa odredište videla otvoreno
		/// (<paramref name="destinationSeenOpen"/>) i ono je još otvoreno; inače se odbacuje.
		/// </summary>
		private void Forward(int from, int to, bool destinationSeenOpen, byte[] buffer)
		{
			nint n;
			while ((n = read(from, buffer, buffer.Length)) > 0) {
				int offset = 0;
				long deadline = Environment.TickCount64 + (long)__writeLimit.TotalMilliseconds;
				// odredište se može zatvoriti i dok upis čeka — ostatak bi inače ostao u njegovom ulazu do sledećeg otvaranja
				while (offset < n && destinationSeenOpen && IsOpen(to)) {
					nint written = write(to, ref buffer[offset], (nint)(n - offset));
					if (written > 0) {
						offset += (int)written;
						continue;
					}
					int errno = Marshal.GetLastPInvokeError();
					if (written < 0 && errno != EAGAIN && errno != EINTR || Environment.TickCount64 > deadline) {
						_logger?.LogWarning("Serijski par {Port}: odbačeno {Count} B (druga strana ne čita, errno {Errno})", KioskPortName, n - offset, errno);
						break;
					}
					Thread.Sleep(5);
				}
			}
		}

		public void Dispose()
		{
			_stopping = true;
			_pump.Join();
			try {
				if (new FileInfo(KioskPortName).LinkTarget == _kiosk.SlaveName) {
					File.Delete(KioskPortName);
				}
			}
			catch (IOException) { }
			_kiosk.Dispose();
			_simulator.Dispose();
			_simulatorSeenOpen.Dispose();
		}

		/// <summary>Jedan pseudo-terminal: master (ostaje kod simulatora) i ime slave strane, u raw režimu.</summary>
		private sealed class Pty : IDisposable
		{
			private Pty(int master, string slaveName)
			{
				Master = master;
				SlaveName = slaveName;
			}

			public int Master { get; }

			public string SlaveName { get; }

			public static Pty Open()
			{
				int master = open("/dev/ptmx", O_RDWR | O_NOCTTY | O_NONBLOCK);
				if (master < 0) {
					throw new IOException($"/dev/ptmx: errno {Marshal.GetLastPInvokeError()}");
				}
				try {
					if (grantpt(master) != 0 || unlockpt(master) != 0) {
						throw new IOException($"grantpt/unlockpt: errno {Marshal.GetLastPInvokeError()}");
					}
					var name = new byte[128];
					int error = ptsname_r(master, name, name.Length);
					if (error != 0) {
						throw new IOException($"ptsname_r: {error}");
					}
					var slaveName = Encoding.ASCII.GetString(name, 0, Array.IndexOf(name, (byte)0));
					MakeRaw(slaveName);
					return new Pty(master, slaveName);
				}
				catch {
					close(master);
					throw;
				}
			}

			/// <summary>
			/// Raw režim (bez eha i prevođenja CR/LF) od početka: <c>SerialPort.Open</c> ga postavlja i sam, ali bajt koji stigne
			/// pre toga ne sme da prođe kroz line discipline. termios je neproziran bafer — menja ga samo <c>cfmakeraw</c>.
			/// </summary>
			private static void MakeRaw(string slaveName)
			{
				int slave = open(slaveName, O_RDWR | O_NOCTTY | O_NONBLOCK);
				if (slave < 0) {
					throw new IOException($"{slaveName}: errno {Marshal.GetLastPInvokeError()}");
				}
				try {
					var termios = new byte[256];
					if (tcgetattr(slave, termios) != 0) {
						throw new IOException($"tcgetattr {slaveName}: errno {Marshal.GetLastPInvokeError()}");
					}
					cfmakeraw(termios);
					if (tcsetattr(slave, 0, termios) != 0) {
						throw new IOException($"tcsetattr {slaveName}: errno {Marshal.GetLastPInvokeError()}");
					}
				}
				finally {
					close(slave);	// posle zatvaranja master javlja POLLHUP dok ga neko ne otvori
				}
			}

			public void Dispose() => close(Master);
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct PollFd
		{
			public int Fd;
			public short Events;
			public short Revents;
		}

		[DllImport("libc", SetLastError = true)]
		private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

		[DllImport("libc", SetLastError = true)]
		private static extern int close(int fd);

		[DllImport("libc", SetLastError = true)]
		private static extern int grantpt(int fd);

		[DllImport("libc", SetLastError = true)]
		private static extern int unlockpt(int fd);

		[DllImport("libc")]
		private static extern int ptsname_r(int fd, byte[] buf, nint len);

		[DllImport("libc", SetLastError = true)]
		private static extern int tcgetattr(int fd, byte[] termios);

		[DllImport("libc", SetLastError = true)]
		private static extern int tcsetattr(int fd, int optionalActions, byte[] termios);

		[DllImport("libc")]
		private static extern void cfmakeraw(byte[] termios);

		[DllImport("libc", SetLastError = true)]
		private static extern int poll([In, Out] PollFd[] fds, nuint nfds, int timeout);

		[DllImport("libc", SetLastError = true)]
		private static extern nint read(int fd, byte[] buf, nint count);

		[DllImport("libc", SetLastError = true)]
		private static extern nint write(int fd, ref byte buf, nint count);
	}
}
