
using System.Net;
using System.Net.Sockets;
using Flatsoft.Common;
using Microsoft.Extensions.Logging;
using TopPos.LockController;

namespace TopPos.LockController.NCU24L
{
	/// <summary>
	/// Implementation of ILockControllerClient for NCU24L controller.
	/// Maps door numbers (1-240) to NCU addresses (0-9) and lock numbers (0-23).
	/// Handles TCP communication directly with NCU24L hardware.
	///
	/// NCU24L packet format: STX + ADDR + LOCKNUM + CMD + ASK + DATALEN + ETX + SUM + DATA
	/// Note: DATA comes AFTER SUM. Checksum includes DATA bytes.
	/// </summary>
	public class LockControllerClientNCU24L : ILockControllerClient, IDisposable
	{
		// TCP connection fields
		private readonly string _ipAddress;
		private readonly int _port;
		private TcpClient? _client;
		private NetworkStream? _stream;
		// Primljeni bajtovi koji još nisu deo pročitanog okvira; koristi se samo pod semaforom komandi.
		private readonly NCU24LFrameBuffer _rxBuffer;
		private readonly object _lock = new object();
		private readonly ILogger? _logger;
		private DateTime _nextAllowedTime = DateTime.MinValue;
		private readonly SemaphoreSlim _semafor = new SemaphoreSlim(1, 1);
		// Posle Dispose se komande ne izvršavaju (ne otvara se nova veza).
		private volatile bool _disposed;

		// Reconnection settings
		// Posle odbijene veze još jedan pokušaj: ZLG modul prima novu vezu tek ~1,5–2 s posle zatvaranja prethodne, a
		// Windows odbijanje prijavi posle ~2,04 s, pa je jedan pokušaj granično kratak (terenski test 2026-10-16).
		// Pokušaj posle 1 s pokriva oslobađanje do ~5 s. Ponavlja se samo odbijanje, ne i istek roka (NCU bez struje).
		private const int MAX_RECONNECT_ATTEMPTS = 2;
		private const int RECONNECT_DELAY_MS = 1000;
		// Koliko Dispose najduže čeka da tekuća komanda završi: povezivanje sa ponovnim pokušajem (~5 s),
		// pražnjenje (1 s), razmak između komandi i odgovor (1 s).
		private const int CLOSE_WAIT_MS = 8000;
		// Rok za TCP povezivanje: bez njega se, kad NCU nije dostupan (struja, kabl), čeka podrazumevani Windows timeout
		// (desetine sekundi) dok se drži semafor komandi. Na lokalnoj mreži ispravan NCU se poveže za nekoliko ms.
		// Rok mora biti duži od ~2 s: kad NCU odbije vezu (RST), Windows ponavlja SYN i odbijanje prijavi tek posle
		// ~2,04 s. Sa kraćim rokom „odbijeno" izgleda kao „nije odgovorio", a gubi se i poslednji ponovljeni SYN,
		// koji ZLG modul ponekad prihvati (oslobađa mesto nekoliko sekundi posle zatvaranja prethodne veze).
		private const int CONNECT_TIMEOUT_MS = 3000;
		// Rok za odgovor na komandu; u njemu moraju da stignu i tuđi okviri koji se preskaču.
		private const int RESPONSE_TIMEOUT_MS = 1000;
		// Posle oštećenog okvira: tišina na vezi pre nove komande. Najduže trajanje pražnjenja pre komande (i čekanja na tišinu).
		private const int RESYNC_QUIET_MS = 100;
		private const int RESYNC_MAX_WAIT_MS = 1000;

		/// <summary>
		/// NCU24L controller has 24 doors per NCU module
		/// </summary>
		public const int DOORS_PER_NCU = 24;

		/// <summary>
		/// Number of doors per controller module (NCU24L has 24)
		/// </summary>
		public int NumberOfDoorsPerController => DOORS_PER_NCU;

		private bool IsConnected => _client?.Connected ?? false;

		public LockControllerClientNCU24L(TcpControllerConfiguration tcpController, ILogger? logger = null)
		{
			_ipAddress = tcpController.IpAddress ?? throw new ArgumentNullException(nameof(tcpController.IpAddress));
			_port = tcpController.Port;
			if (_port < 1 || _port > 65535)
				throw new ArgumentOutOfRangeException(nameof(tcpController.Port));

			_logger = logger;
			_rxBuffer = new NCU24LFrameBuffer(
				message => _logger?.LogWarning($"{nameof(LockControllerClientNCU24L)}: {message}"),
				message => _logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}: {message}"));
			_logger?.LogInformation($"{nameof(LockControllerClientNCU24L)}:ctor: created for {tcpController}");
		}

		// ==================== TCP CONNECTION ====================

		private async Task ConnectAsync()
		{
			if (IsConnected)
			{
				_logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}:{nameof(ConnectAsync)}: Already connected to {_ipAddress}:{_port}");
				return;
			}

			_logger?.LogInformation($"{nameof(LockControllerClientNCU24L)}:{nameof(ConnectAsync)}: Connecting to NCU24L controller at {_ipAddress}:{_port}");

			try
			{
				_client = new TcpClient();
				using (var ctsConnect = new CancellationTokenSource(CONNECT_TIMEOUT_MS)) {
					try {
						await _client.ConnectAsync(_ipAddress, _port, ctsConnect.Token);
					}
					catch (OperationCanceledException) when (ctsConnect.IsCancellationRequested) {
						throw new TimeoutException($"NCU {_ipAddress}:{_port} nije odgovorio na povezivanje za {CONNECT_TIMEOUT_MS} ms.");
					}
				}
				_stream = _client.GetStream();

				_logger?.LogInformation($"{nameof(LockControllerClientNCU24L)}:{nameof(ConnectAsync)}: Successfully connected to {_ipAddress}:{_port}");
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, $"{nameof(LockControllerClientNCU24L)}:{nameof(ConnectAsync)}: Failed to connect to {_ipAddress}:{_port}");
				throw;
			}
		}

		private async Task DisconnectAsync()
		{
			_logger?.LogInformation($"{nameof(LockControllerClientNCU24L)}:{nameof(DisconnectAsync)}: Disconnecting from {_ipAddress}:{_port}...");

			try
			{
				if (_stream != null)
				{
					await _stream.FlushAsync();
					_stream.Close();
					_stream = null;
				}

				if (_client != null)
				{
					_client.Close();
					_client = null;
				}

				_logger?.LogInformation($"{nameof(LockControllerClientNCU24L)}:{nameof(DisconnectAsync)}: Disconnected from {_ipAddress}:{_port}");
			}
			catch (Exception ex)
			{
				_logger?.LogWarning(ex, $"{nameof(LockControllerClientNCU24L)}:{nameof(DisconnectAsync)}: Error during disconnect from {_ipAddress}:{_port}");
			}
		}

		private void CleanupConnection()
		{
			try
			{
				_stream?.Close();
				_stream?.Dispose();
				_client?.Close();
				_client?.Dispose();
			}
			catch (Exception ex)
			{
				_logger?.LogDebug(ex, $"{nameof(LockControllerClientNCU24L)}:{nameof(CleanupConnection)}: Error during cleanup");
			}
			finally
			{
				_stream = null;
				_client = null;
				// Bajtovi sa stare veze ne pripadaju odgovorima na novoj.
				_rxBuffer.Clear();
			}
		}

		private async Task EnsureConnectedAsync()
		{
			if (IsConnected && _stream != null)
				return;

			for (int attempt = 1; attempt <= MAX_RECONNECT_ATTEMPTS; attempt++)
			{
				try
				{
					CleanupConnection();
					await ConnectAsync();
					_logger?.LogInformation($"{nameof(LockControllerClientNCU24L)}:{nameof(EnsureConnectedAsync)}: Connected to {_ipAddress}:{_port}");
					return;
				}
				catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused && attempt < MAX_RECONNECT_ATTEMPTS)
				{
					_logger?.LogWarning($"{nameof(LockControllerClientNCU24L)}:{nameof(EnsureConnectedAsync)}: {_ipAddress}:{_port} je odbio vezu (pokušaj {attempt}/{MAX_RECONNECT_ATTEMPTS}); modul možda još oslobađa prethodnu vezu, novi pokušaj za {RECONNECT_DELAY_MS} ms");
					await Task.Delay(RECONNECT_DELAY_MS);
				}
				catch (SocketException ex)
				{
					_logger?.LogWarning(ex, $"{nameof(LockControllerClientNCU24L)}:{nameof(EnsureConnectedAsync)}: BU {_ipAddress}:{_port} has failed to respond.");
					return;
				}
				catch (Exception ex)
				{
					_logger?.LogWarning($"{nameof(LockControllerClientNCU24L)}:{nameof(EnsureConnectedAsync)}: Connection attempt {attempt}/{MAX_RECONNECT_ATTEMPTS} to {_ipAddress}:{_port} failed ({ex.Message})");
					throw;
				}
			}
		}

		// ==================== NCU24L COMMANDS ====================

		/// <summary>
		/// Get lock states for a single NCU24L (command 0x80)
		/// Returns 3 bytes representing 24 lock states
		/// </summary>
		private async Task<byte[]> GetStateAsync(byte ncuAddress)
		{
			_logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}:{nameof(GetStateAsync)}: called for NCU address 0x{ncuAddress:X2}");

			ValidateAddress(ncuAddress);

			var command = BuildCommand(ncuAddress, 0x00, NCU24LCommand.GetState);
			var responses = await SendCommandAsyncWithDelay(command);

			if (responses.Count == 0)
			{
				_logger?.LogError($"{nameof(LockControllerClientNCU24L)}:{nameof(GetStateAsync)}: No response received from NCU 0x{ncuAddress:X2}");
				throw new InvalidOperationException("No response received from controller");
			}

			var lockStates = ParseLockStates(responses[0]);
			return lockStates;
		}

		/// <summary>
		/// Unlock a specific lock (command 0x81)
		/// </summary>
		private async Task UnlockAsync(byte ncuAddress, byte lockNumber, Action? onWrite = null)
		{
			_logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}:{nameof(UnlockAsync)}: called for NCU 0x{ncuAddress:X2}, Lock {lockNumber + 1}");

			ValidateAddress(ncuAddress);
			if (lockNumber > 23)
				throw new ArgumentOutOfRangeException(nameof(lockNumber), "Lock number must be 0-23");

			_logger?.LogInformation($"{nameof(LockControllerClientNCU24L)}:{nameof(UnlockAsync)}: Unlocking Lock {lockNumber + 1} on NCU 0x{ncuAddress:X2}");

			var command = BuildCommand(ncuAddress, lockNumber, NCU24LCommand.Unlock);
			var responses = await SendCommandAsyncWithDelay(command, onWrite: onWrite);

			// Validate response ASK code if we got a response
			if (responses.Count > 0)
			{
				ValidateResponseAsk(responses[0]);
			}

			_logger?.LogInformation($"{nameof(LockControllerClientNCU24L)}:{nameof(UnlockAsync)}: Unlock command sent for Lock {lockNumber + 1} on NCU 0x{ncuAddress:X2}");
		}

		/// <summary>
		/// Open all 24 locks on a single NCU24L (command 0x81 with LOCKNUM=0x64)
		/// </summary>
		private async Task OpenAllLocksAsync(byte ncuAddress)
		{
			ValidateAddress(ncuAddress);

			_logger?.LogInformation($"{nameof(LockControllerClientNCU24L)}:{nameof(OpenAllLocksAsync)}: Opening all locks on NCU 0x{ncuAddress:X2}");

			var command = BuildCommand(ncuAddress, NCU24LProtocol.ALL_LOCKS, NCU24LCommand.Unlock);
			var responses = await SendCommandAsyncWithDelay(command);

			if (responses.Count > 0)
			{
				ValidateResponseAsk(responses[0]);
			}
		}

		// ==================== PROTOCOL HELPERS ====================

		/// <summary>
		/// Build NCU24L command packet.
		/// Format: STX + ADDR + LOCKNUM + CMD + ASK(0x00) + DATALEN + ETX + SUM + DATA
		/// Checksum = low byte of (STX + ADDR + LOCKNUM + CMD + ASK + DATALEN + ETX + all DATA bytes)
		/// </summary>
		private byte[] BuildCommand(byte address, byte lockNumber, NCU24LCommand cmd, byte[]? data = null)
		{
			byte dataLen = (byte)(data?.Length ?? 0);

			// Header: STX + ADDR + LOCKNUM + CMD + ASK + DATALEN + ETX
			var header = new byte[]
			{
				NCU24LProtocol.STX,
				address,
				lockNumber,
				(byte)cmd,
				NCU24LProtocol.ASK_DEFAULT,
				dataLen,
				NCU24LProtocol.ETX
			};

			// Checksum = low byte of (header bytes + data bytes)
			int sum = 0;
			foreach (var b in header)
				sum += b;
			if (data != null)
			{
				foreach (var b in data)
					sum += b;
			}
			byte checksum = (byte)(sum & 0xFF);

			// Final packet: header + SUM + DATA
			var packet = new List<byte>(header);
			packet.Add(checksum);
			if (data != null)
				packet.AddRange(data);

			var commandBytes = packet.ToArray();

			_logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}:{nameof(BuildCommand)}: {cmd} to ADDR=0x{address:X2} LOCKNUM=0x{lockNumber:X2}");
			_logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}:{nameof(BuildCommand)}: Full packet: {ToHexString(commandBytes)}");

			return commandBytes;
		}

		private static string ToHexString(byte[] bytes)
		{
			return BitConverter.ToString(bytes).Replace("-", " ");
		}

		private async Task<List<byte[]>> SendCommandAsyncWithDelay(byte[] command, bool expectResponse = true, bool multipleResponses = false, Action? onWrite = null)
		{
			await _semafor.WaitAsync();
			try
			{
				// Komanda koja je čekala na semafor dok je Dispose zatvarao vezu ne sme ponovo da poveže klijenta.
				ObjectDisposedException.ThrowIf(_disposed, this);

				await EnsureConnectedAsync();

				var now = DateTime.Now;
				if (now < _nextAllowedTime)
				{
					var delay = _nextAllowedTime - now;
					_logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}:{nameof(SendCommandAsyncWithDelay)}: Delaying command send by {delay.TotalMilliseconds} ms");
					await Task.Delay(delay);
				}
				var responses = await SendCommandAsync(command, expectResponse, multipleResponses, onWrite);
				_nextAllowedTime = DateTime.Now.AddMilliseconds(NCU24LProtocol.INTER_COMMAND_DELAY_MS);
				return responses;
			}
			finally
			{
				_semafor.Release();
			}
		}

		// onWrite se poziva neposredno pre upisa komande na vezu (posle provere veze i prijema).
		private async Task<List<byte[]>> SendCommandAsync(byte[] command, bool expectResponse = true, bool multipleResponses = false, Action? onWrite = null)
		{
			if (!IsConnected || _stream == null)
			{
				_logger?.LogError($"{nameof(LockControllerClientNCU24L)}:{nameof(SendCommandAsync)}: Not connected to controller");
				throw new InvalidOperationException("Not connected to NCU24L controller");
			}

			if (!await DiscardStaleInputAsync())
			{
				throw new InvalidOperationException("Prijem sa kontrolera nije sinhronizovan (posle oštećenog okvira nema tišine na vezi); komanda nije poslata");
			}

			_logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}:{nameof(SendCommandAsync)}: TX > {ToHexString(command)}");

			onWrite?.Invoke();
			lock (_lock)
			{
				_stream.Write(command, 0, command.Length);
			}

			if (!expectResponse)
			{
				_logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}:{nameof(SendCommandAsync)}: Command sent, no response expected");
				return new List<byte[]>();
			}

			var responses = new List<byte[]>();
			var deadline = DateTime.Now.AddMilliseconds(RESPONSE_TIMEOUT_MS);

			if (multipleResponses)
			{
				while (true)
				{
					var response = await ReceiveResponseAsync(command, deadline);
					if (response == null)
						break;

					responses.Add(response);
					deadline = DateTime.Now.AddMilliseconds(200);
				}

				_logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}:{nameof(SendCommandAsync)}: Received {responses.Count} response(s) total");
			}
			else
			{
				var response = await ReceiveResponseAsync(command, deadline);
				if (response != null)
				{
					responses.Add(response);
				}
				else
				{
					var pending = _rxBuffer.Count > 0 ? $"; u baferu: {ToHexString(_rxBuffer.ToArray())}" : "";
					_logger?.LogWarning($"{nameof(LockControllerClientNCU24L)}:{nameof(SendCommandAsync)}: RX timeout - odgovor na {ToHexString(command)} nije stigao za {RESPONSE_TIMEOUT_MS} ms{pending}");
				}
			}

			return responses;
		}

		/// <summary>
		/// Pre slanja komande odbacuje sve što je već primljeno: zakasnele ili duple odgovore na ranije komande
		/// (npr. ZLG modul isporuči odgovor sa prekinute veze). Ostavljeni, bili bi pročitani kao odgovor na sledeću
		/// komandu. Okvir od koga je stigao samo početak bafer odbacuje kao celinu, kad stigne ceo.
		/// Ako je granica okvira izgubljena (oštećen okvir), čeka se tišina na vezi i bafer se čisti — ostaci oštećene
		/// razmene ne smeju da se nađu ispred sledećeg odgovora. Vraća false ako tišina nije nastupila: tada komanda
		/// ne sme da se pošalje.
		/// </summary>
		private async Task<bool> DiscardStaleInputAsync()
		{
			if (_stream == null)
				return false;

			// Jedan rok za celo pražnjenje: dok podaci stižu bez prekida, petlje se ne smeju vrteti bez kraja —
			// ovo se radi pod semaforom komandi, pa bi stajalo i otvaranje vrata.
			var giveUp = DateTime.Now.AddMilliseconds(RESYNC_MAX_WAIT_MS);
			var buffer = new byte[256];
			while (_stream.DataAvailable)
			{
				if (DateTime.Now >= giveUp)
				{
					_logger?.LogWarning($"{nameof(LockControllerClientNCU24L)}:{nameof(DiscardStaleInputAsync)}: podaci sa kontrolera stižu bez prekida duže od {RESYNC_MAX_WAIT_MS} ms; komanda se ne šalje");
					return false;
				}

				int read = _stream.Read(buffer, 0, buffer.Length);
				if (read <= 0)
					break;
				_rxBuffer.Append(buffer, read);
			}

			_rxBuffer.DiscardStale();

			if (!_rxBuffer.IsDesynchronized)
				return true;

			int discarded = _rxBuffer.Count;
			var lastData = DateTime.Now;
			bool quiet = false;
			while (DateTime.Now < giveUp)
			{
				// Prvo soket: tišina važi samo ako u trenutku provere u njemu nema ničega — podatak koji stigne
				// tik pred istek tišine inače bi ostao u soketu i bio pročitan kao odgovor na sledeću komandu.
				if (_stream.DataAvailable)
				{
					int read = _stream.Read(buffer, 0, buffer.Length);
					if (read <= 0)
						break;
					discarded += read;
					lastData = DateTime.Now;
					continue;
				}

				if (DateTime.Now - lastData >= TimeSpan.FromMilliseconds(RESYNC_QUIET_MS))
				{
					quiet = true;
					break;
				}

				await Task.Delay(5);
			}

			if (!quiet)
			{
				_logger?.LogWarning($"{nameof(LockControllerClientNCU24L)}:{nameof(DiscardStaleInputAsync)}: granica okvira je izgubljena, a za {RESYNC_MAX_WAIT_MS} ms od početka pražnjenja nije nastupila tišina od {RESYNC_QUIET_MS} ms (odbačeno {discarded} bajtova); komanda se ne šalje");
				return false;
			}

			_rxBuffer.Resynchronize();
			_logger?.LogWarning($"{nameof(LockControllerClientNCU24L)}:{nameof(DiscardStaleInputAsync)}: granica okvira je bila izgubljena; posle tišine od {RESYNC_QUIET_MS} ms odbačeno još {discarded} bajtova, prijem počinje iz početka");
			return true;
		}

		/// <summary>
		/// Čita okvire redom dok ne stigne odgovor na komandu, najduže do roka. Okvir koji pripada drugoj komandi se
		/// preskače. Odgovor može da stigne u više TCP segmenata. Null ako odgovor ne stigne do roka.
		/// </summary>
		private async Task<byte[]?> ReceiveResponseAsync(byte[] command, DateTime deadline)
		{
			if (_stream == null)
			{
				_logger?.LogWarning($"{nameof(LockControllerClientNCU24L)}:{nameof(ReceiveResponseAsync)}: Stream is null");
				return null;
			}

			var chunk = new byte[256];
			while (true)
			{
				var frame = _rxBuffer.TakeNextFrame();
				if (_rxBuffer.IsDesynchronized)
				{
					// Oštećen okvir: razmena se prekida bez odgovora, oporavak je pre sledeće komande.
					return null;
				}

				if (frame != null)
				{
					if (IsResponseTo(command, frame))
					{
						_logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}:{nameof(ReceiveResponseAsync)}: RX < {ToHexString(frame)} ({frame.Length} bytes)");
						return frame;
					}

					_logger?.LogWarning($"{nameof(LockControllerClientNCU24L)}:{nameof(ReceiveResponseAsync)}: odbačen okvir koji ne pripada komandi {ToHexString(command)}: {ToHexString(frame)}");
					continue;
				}

				if (DateTime.Now >= deadline)
					return null;

				if (!_stream.DataAvailable)
				{
					await Task.Delay(5);
					continue;
				}

				int read = await _stream.ReadAsync(chunk, 0, chunk.Length);
				if (read == 0)
				{
					_logger?.LogWarning($"{nameof(LockControllerClientNCU24L)}:{nameof(ReceiveResponseAsync)}: veza je zatvorena sa druge strane");
					return null;
				}
				_rxBuffer.Append(chunk, read);
			}
		}

		/// <summary>
		/// Odgovor NCU24L ponavlja LOCKNUM i CMD komande, a ADDR je adresa NCU-a koji odgovara
		/// (na broadcast 0x64 odgovara svaki NCU svojom adresom).
		/// </summary>
		private static bool IsResponseTo(byte[] command, byte[] response)
			=> response.Length >= NCU24LProtocol.MIN_PACKET_LENGTH
			   && response[3] == command[3]
			   && response[2] == command[2]
			   && (command[1] == NCU24LProtocol.BROADCAST_ADDRESS || response[1] == command[1]);

		// ==================== PARSERS ====================

		/// <summary>
		/// Parse lock states from NCU24L response.
		/// Response format: STX(0) + ADDR(1) + LOCKNUM(2) + CMD(3) + ASK(4) + DATALEN(5) + ETX(6) + SUM(7) + DATA(8...)
		/// Lock states are 3 DATA bytes for 24 locks.
		/// </summary>
		private byte[] ParseLockStates(byte[] response)
		{
			_logger?.LogTrace($"{nameof(LockControllerClientNCU24L)}:{nameof(ParseLockStates)}: Parsing response {ToHexString(response)}");

			if (response == null || response.Length < NCU24LProtocol.MIN_PACKET_LENGTH)
			{
				_logger?.LogError($"{nameof(LockControllerClientNCU24L)}:{nameof(ParseLockStates)}: Invalid response length ({response?.Length ?? 0} bytes)");
				throw new InvalidOperationException("Invalid response length");
			}

			// Check ASK code
			ValidateResponseAsk(response);

			byte dataLen = response[5];

			if (dataLen < 3 || response.Length < 8 + dataLen)
			{
				_logger?.LogError($"{nameof(LockControllerClientNCU24L)}:{nameof(ParseLockStates)}: Expected 3 data bytes for lock states, got DATALEN={dataLen}");
				throw new InvalidOperationException($"Expected 3 data bytes for lock states, got {dataLen}");
			}

			// Extract lock states (3 bytes for 24 locks, DATA starts at index 8)
			var lockStates = new byte[] { response[8], response[9], response[10] };

			_logger?.LogTrace($"{nameof(LockControllerClientNCU24L)}:{nameof(ParseLockStates)}: NCU 0x{response[1]:X2}, LockStates=[0x{lockStates[0]:X2}, 0x{lockStates[1]:X2}, 0x{lockStates[2]:X2}]");

			var closedLocks = GetClosedLocksList(lockStates);
			_logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}:{nameof(ParseLockStates)}: {closedLocks.Count}/24 locks closed {(closedLocks.Count > 0 ? $"[{string.Join(",", closedLocks)}]" : "[]")}");

			return lockStates;
		}

		/// <summary>
		/// Validate the ASK byte in a response
		/// </summary>
		private void ValidateResponseAsk(byte[] response)
		{
			if (response.Length < 5) return;

			byte ask = response[4];
			var askCode = (NCU24LResponseCode)ask;

			switch (askCode)
			{
				case NCU24LResponseCode.Success:
				case NCU24LResponseCode.Default:
					return;
				case NCU24LResponseCode.Failed:
					throw new InvalidOperationException("NCU24L: Command execution failed (ASK=0x11)");
				case NCU24LResponseCode.Timeout:
					throw new InvalidOperationException("NCU24L: Command timed out (ASK=0x12)");
				case NCU24LResponseCode.UnknownCommand:
					throw new InvalidOperationException("NCU24L: Unknown command (ASK=0x13)");
				case NCU24LResponseCode.DataVerificationFailure:
					throw new InvalidOperationException("NCU24L: Data verification failure (ASK=0x14)");
				default:
					_logger?.LogWarning($"{nameof(LockControllerClientNCU24L)}: Unknown ASK code 0x{ask:X2}");
					return;
			}
		}

		/// <summary>
		/// Check if a specific lock is closed based on lock state bytes
		/// </summary>
		/// <param name="lockStates">3 bytes representing 24 locks</param>
		/// <param name="lockNumber">Lock number (1-24)</param>
		/// <returns>True if closed, false if open</returns>
		private bool IsLockClosed(byte[] lockStates, int lockNumber)
		{
			if (lockNumber < 1 || lockNumber > 24)
				throw new ArgumentOutOfRangeException(nameof(lockNumber), "Lock number must be 1-24");

			int byteIndex = (lockNumber - 1) / 8;
			int bitIndex = (lockNumber - 1) % 8;

			return (lockStates[byteIndex] & (1 << bitIndex)) != 0;
		}

		/// <summary>
		/// Get list of all closed locks
		/// </summary>
		private List<int> GetClosedLocksList(byte[] lockStates)
		{
			var closedLocks = new List<int>();
			for (int i = 1; i <= 24; i++)
			{
				if (IsLockClosed(lockStates, i))
					closedLocks.Add(i);
			}
			return closedLocks;
		}

		private void ValidateAddress(byte address)
		{
			if (address > 0x09 && address != NCU24LProtocol.BROADCAST_ADDRESS)
				throw new ArgumentOutOfRangeException(nameof(address), "NCU address must be 0x00-0x09 or 0x64 (broadcast)");
		}

		/// <summary>
		/// Convert doorNumber (1-240) to NCU address (0-9) and lock number (0-23).
		/// doorNumber 1-24 -> NCU 0, locks 0-23
		/// doorNumber 25-48 -> NCU 1, locks 0-23
		/// ...
		/// doorNumber 217-240 -> NCU 9, locks 0-23
		/// </summary>
		private (byte ncuAddress, byte lockNumber) MapDoorNumber(int doorNumber)
		{
			if (doorNumber < 1 || doorNumber > 240)
			{
				throw new ArgumentOutOfRangeException(nameof(doorNumber), "Door number must be 1-240");
			}

			byte ncuAddress = (byte)((doorNumber - 1) / 24);
			byte lockNumber = (byte)((doorNumber - 1) % 24);

			_logger?.LogTrace($"{nameof(LockControllerClientNCU24L)}:{nameof(MapDoorNumber)}: doorNumber={doorNumber} > NCU={ncuAddress}, lock={lockNumber}");

			return (ncuAddress, lockNumber);
		}

		// ==================== ILockControllerClient IMPLEMENTATION ====================

		public async ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, CancellationToken cancellationToken)
		{
			return await OpenDoorSingle(doorNumber, null, null, cancellationToken);
		}

		public async ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
		{
			return await OpenDoorSingle(doorNumber, null, receiveTimeout, cancellationToken);
		}

		public async ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
		{
			try
			{
				var (ncuAddress, lockNumber) = MapDoorNumber(doorNumber);

				_logger?.LogInformation($"{nameof(LockControllerClientNCU24L)}:{nameof(OpenDoorSingle)}: doorNumber={doorNumber} (NCU={ncuAddress}, lock={lockNumber})");

				await UnlockAsync(ncuAddress, lockNumber);

				var lockStates = await GetStateAsync(ncuAddress);

				bool isClosed = IsLockClosed(lockStates, lockNumber + 1);

				var doorStatus = isClosed ? DoorStatusCode.Closed : DoorStatusCode.Open;

				_logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}:{nameof(OpenDoorSingle)}: doorNumber={doorNumber} result={doorStatus}");

				return doorStatus;
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, $"{nameof(LockControllerClientNCU24L)}:{nameof(OpenDoorSingle)}: failed for doorNumber={doorNumber}");
				return null;
			}
		}

		// Unlock se šalje jednom (ovaj kontroler ga ionako ne ponavlja). Pad potvrde ili GetState posle poslatog Unlock-a
		// daje Sent=true i nepoznato stanje: vrata su možda otvorena. Rok i token se, kao u OpenDoorSingle, ne prosleđuju.
		public async ValueTask<DoorOpenAttempt> OpenDoorOnce(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
		{
			bool sent = false;
			try
			{
				var (ncuAddress, lockNumber) = MapDoorNumber(doorNumber);

				_logger?.LogInformation($"{nameof(LockControllerClientNCU24L)}:{nameof(OpenDoorOnce)}: doorNumber={doorNumber} (NCU={ncuAddress}, lock={lockNumber})");

				await UnlockAsync(ncuAddress, lockNumber, () => sent = true);

				var lockStates = await GetStateAsync(ncuAddress);

				var doorStatus = IsLockClosed(lockStates, lockNumber + 1) ? DoorStatusCode.Closed : DoorStatusCode.Open;

				_logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}:{nameof(OpenDoorOnce)}: doorNumber={doorNumber} result={doorStatus}");

				return new DoorOpenAttempt(true, doorStatus);
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, $"{nameof(LockControllerClientNCU24L)}:{nameof(OpenDoorOnce)}: failed for doorNumber={doorNumber} (Unlock {(sent ? "poslat" : "nije poslat")})");
				return new DoorOpenAttempt(sent, null);
			}
		}

		public async ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, CancellationToken cancellationToken)
		{
			return await CheckDoorSingle(doorNumber, null, null, cancellationToken);
		}

		public async ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
		{
			return await CheckDoorSingle(doorNumber, null, receiveTimeout, cancellationToken);
		}

		public async ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
		{
			try
			{
				var (ncuAddress, lockNumber) = MapDoorNumber(doorNumber);

				_logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}:{nameof(CheckDoorSingle)}: doorNumber={doorNumber} (NCU={ncuAddress}, lock={lockNumber})");

				var lockStates = await GetStateAsync(ncuAddress);
				bool isClosed = IsLockClosed(lockStates, lockNumber + 1);

				var doorStatus = isClosed ? DoorStatusCode.Closed : DoorStatusCode.Open;

				_logger?.LogTrace($"{nameof(LockControllerClientNCU24L)}:{nameof(CheckDoorSingle)}: doorNumber={doorNumber} status={doorStatus}");

				return doorStatus;
			}
			catch (Exception ex)
			{
				_logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}:{nameof(CheckDoorSingle)}: failed for [{doorNumber}], {ex.Message}");
				return null;
			}
		}

		public async ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, CancellationToken cancellationToken)
		{
			return await CheckDoorMultiple(moduleNumber, null, null, cancellationToken);
		}

		public async ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
		{
			return await CheckDoorMultiple(moduleNumber, null, receiveTimeout, cancellationToken);
		}

		public async ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
		{
			try
			{
				moduleNumber--;

				_logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}:{nameof(CheckDoorMultiple)}: module {moduleNumber}");

				var lockStates = await GetStateAsync((byte)moduleNumber);

				var doorStatuses = new DoorStatusCode[NumberOfDoorsPerController];
				for (int i = 0; i < NumberOfDoorsPerController; i++)
				{
					bool isClosed = IsLockClosed(lockStates, i + 1);
					doorStatuses[i] = isClosed ? DoorStatusCode.Closed : DoorStatusCode.Open;
				}

				return doorStatuses;
			}
			catch (SocketException ex)
			{
				_logger?.LogWarning(ex, $"{nameof(LockControllerClientNCU24L)}:{nameof(CheckDoorMultiple)}: operation failed for [{moduleNumber}] - BU has failed to respond.");
				return null;
			}
			catch (Exception ex)
			{
				_logger?.LogWarning($"{nameof(LockControllerClientNCU24L)}:{nameof(CheckDoorMultiple)}: failed for [{moduleNumber}], {ex.Message}");
				return null;
			}
		}

		// ==================== IDisposable ====================

		/// <summary>
		/// Veza se zatvara tek kad tekuća komanda završi i na vezi nastupi tišina: ZLG modul, kad mu se veza zatvori
		/// dok odgovor još stiže, minutima odbija svaku novu vezu (terenski test 2026-10-16) — paketomat tada ostaje
		/// bez kontrolera. Komande koje stignu posle poziva se ne izvršavaju.
		/// </summary>
		public void Dispose()
		{
			_disposed = true;
			bool acquired = _semafor.Wait(CLOSE_WAIT_MS);
			if (!acquired)
				_logger?.LogWarning($"{nameof(LockControllerClientNCU24L)}:{nameof(Dispose)}: komanda nije završena za {CLOSE_WAIT_MS} ms; veza se ipak zatvara");

			try
			{
				WaitForQuietBeforeClose();
				DisconnectAsync().Wait();
			}
			finally
			{
				if (acquired)
					_semafor.Release();
			}
		}

		/// <summary>
		/// Pre zatvaranja čita i odbacuje sve što još stiže, dok na vezi ne nastupi tišina (najduže
		/// <see cref="RESYNC_MAX_WAIT_MS"/>). I nepročitani bajtovi bi zatvaranje veze pretvorili u RST.
		/// </summary>
		private void WaitForQuietBeforeClose()
		{
			if (_stream == null || !IsConnected)
				return;

			try
			{
				var buffer = new byte[256];
				int discarded = 0;
				var lastData = DateTime.Now;
				var giveUp = DateTime.Now.AddMilliseconds(RESYNC_MAX_WAIT_MS);
				while (DateTime.Now < giveUp)
				{
					if (_stream.DataAvailable)
					{
						int read = _stream.Read(buffer, 0, buffer.Length);
						if (read <= 0)
							break;
						discarded += read;
						lastData = DateTime.Now;
						continue;
					}

					if (DateTime.Now - lastData >= TimeSpan.FromMilliseconds(RESYNC_QUIET_MS))
						break;

					Thread.Sleep(5);
				}

				if (discarded > 0)
					_logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}:{nameof(WaitForQuietBeforeClose)}: pre zatvaranja veze odbačeno {discarded} bajtova");
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException)
			{
				_logger?.LogDebug($"{nameof(LockControllerClientNCU24L)}:{nameof(WaitForQuietBeforeClose)}: {ex.Message}");
			}
		}
	}
}
