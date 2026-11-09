
using System.Net;
using System.Net.Sockets;
using Flatsoft.Common;
using Microsoft.Extensions.Logging;
using TopPos.LockController;

namespace TopPos.LockController.NCU16L
{
	/// <summary>
	/// Implementation of ILockControllerClient for NCU16L controller
	/// Maps door numbers (1-256) to NCU addresses (0-15) and lock numbers (0-15)
	/// Handles TCP communication directly with NCU16L hardware
	/// </summary>
	public class LockControllerClientNCU16L : ILockControllerClient, IDisposable
	{
		// TCP connection fields
		private readonly string _ipAddress;
		private readonly int _port;
		private TcpClient? _client;
		private NetworkStream? _stream;
		// Primljeni bajtovi koji još nisu deo pročitanog okvira; koristi se samo pod semaforom komandi.
		private readonly NCU16LFrameBuffer _rxBuffer;
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
		/// NCU16L controller has 16 doors per NCU module
		/// </summary>
		public const int DOORS_PER_NCU = 16;

		/// <summary>
		/// Number of doors per controller module (NCU16L has 16)
		/// </summary>
		public int NumberOfDoorsPerController => DOORS_PER_NCU;

		private bool IsConnected => _client?.Connected ?? false;

		public LockControllerClientNCU16L(TcpControllerConfiguration tcpController, ILogger? logger = null)
		{
			_ipAddress = tcpController.IpAddress ?? throw new ArgumentNullException(nameof(tcpController.IpAddress));
			_port = tcpController.Port;
			if(_port < 1 || _port > 65535 )
				throw new ArgumentNullException(nameof(tcpController.Port));

			_logger = logger;
			_rxBuffer = new NCU16LFrameBuffer(
				message => _logger?.LogWarning($"{nameof(LockControllerClientNCU16L)}: {message}"),
				message => _logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}: {message}"));
			_logger?.LogInformation($"{nameof(LockControllerClientNCU16L)}:ctor: created for {tcpController}");
		}


		// ==================== TCP CONNECTION ====================

		private async Task ConnectAsync()
		{
			if (IsConnected)
			{
				_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(ConnectAsync)}: Already connected to {_ipAddress}:{_port}");
				return;
			}

			_logger?.LogInformation($"{nameof(LockControllerClientNCU16L)}:{nameof(ConnectAsync)}: Connecting to NCU16L controller at {_ipAddress}:{_port}");

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

				_logger?.LogInformation($"{nameof(LockControllerClientNCU16L)}:{nameof(ConnectAsync)}: Successfully connected to {_ipAddress}:{_port}");
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, $"{nameof(LockControllerClientNCU16L)}:{nameof(ConnectAsync)}: Failed to connect to {_ipAddress}:{_port}");
				throw;
			}
		}

		private async Task DisconnectAsync()
		{
			_logger?.LogInformation($"{nameof(LockControllerClientNCU16L)}:{nameof(DisconnectAsync)}: Disconnecting from {_ipAddress}:{_port}...");

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

				_logger?.LogInformation($"{nameof(LockControllerClientNCU16L)}:{nameof(DisconnectAsync)}: Disconnected from {_ipAddress}:{_port}");
			}
			catch (Exception ex)
			{
				_logger?.LogWarning(ex, $"{nameof(LockControllerClientNCU16L)}:{nameof(DisconnectAsync)}: Error during disconnect from {_ipAddress}:{_port}");
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
				_logger?.LogDebug(ex, $"{nameof(LockControllerClientNCU16L)}:{nameof(CleanupConnection)}: Error during cleanup");
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
					_logger?.LogInformation($"{nameof(LockControllerClientNCU16L)}:{nameof(EnsureConnectedAsync)}: Connected to {_ipAddress}:{_port}");
					return;
				}
				catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused && attempt < MAX_RECONNECT_ATTEMPTS)
				{
					_logger?.LogWarning($"{nameof(LockControllerClientNCU16L)}:{nameof(EnsureConnectedAsync)}: {_ipAddress}:{_port} je odbio vezu (pokušaj {attempt}/{MAX_RECONNECT_ATTEMPTS}); modul možda još oslobađa prethodnu vezu, novi pokušaj za {RECONNECT_DELAY_MS} ms");
					await Task.Delay(RECONNECT_DELAY_MS);
				}
				catch (SocketException ex)
				{
					_logger?.LogWarning(ex, $"{nameof(LockControllerClientNCU16L)}:{nameof(EnsureConnectedAsync)}: BU {_ipAddress}:{_port} has failed to respond.");
					return;
				}
				catch (Exception ex)
				{
					_logger?.LogWarning($"{nameof(LockControllerClientNCU16L)}:{nameof(EnsureConnectedAsync)}: Connection attempt {attempt}/{MAX_RECONNECT_ATTEMPTS} to {_ipAddress}:{_port} failed ({ex.Message})");
					throw;
				}
			}
		}

		// ==================== NCU16L COMMANDS ====================

		private async Task<byte[]> GetStateAsync(byte ncuAddress)
		{
			_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(GetStateAsync)}: called for NCU address 0x{ncuAddress:X2}");

			ValidateAddress(ncuAddress);

			var command = BuildCommand((byte)(ncuAddress<<4), NCU16LCommand.GetState);
			var responses = await SendCommandAsyncWithDelay(command);

			if (responses.Count == 0)
			{
				_logger?.LogError($"{nameof(LockControllerClientNCU16L)}:{nameof(GetStateAsync)}: No response received from NCU 0x{ncuAddress:X2}");
				throw new InvalidOperationException("No response received from controller");
			}

			var lockStates = ParseLockStates(responses[0]);

			return lockStates;
		}

		private async Task UnlockAsync(byte ncuAddress, byte lockNumber, Action? onWrite = null)
		{
			_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(UnlockAsync)}: called for NCU 0x{ncuAddress:X2}, Lock {lockNumber + 1}");

			ValidateAddress(ncuAddress);
			if (lockNumber > 15)
				throw new ArgumentOutOfRangeException(nameof(lockNumber), "Lock number must be 0-15 (0x00-0x0F)");

			// Address format: high nibble = NCU address, low nibble = lock number
			byte addr = (byte)((ncuAddress << 4) | lockNumber);

			_logger?.LogInformation($"{nameof(LockControllerClientNCU16L)}:{nameof(UnlockAsync)}: Unlocking Lock {lockNumber + 1} on NCU 0x{ncuAddress:X2}");

			var command = BuildCommand(addr, NCU16LCommand.Unlock);
			await SendCommandAsyncWithDelay(command, expectResponse: false, onWrite: onWrite);

			_logger?.LogInformation($"{nameof(LockControllerClientNCU16L)}:{nameof(UnlockAsync)}: Unlock command sent for Lock {lockNumber + 1} on NCU 0x{ncuAddress:X2}");
		}

		private async Task<byte[]> OpenAllLocksAsync(byte ncuAddress)
		{
			ValidateAddress(ncuAddress);

			var command = BuildCommand(ncuAddress, NCU16LCommand.OpenAllLocks);
			var responses = await SendCommandAsyncWithDelay(command);

			if (responses.Count == 0)
				throw new InvalidOperationException("No response received from controller");

			return ParseLockStates(responses[0]);
		}

		// ==================== PROTOCOL HELPERS ====================

		private byte[] BuildCommand(byte address, NCU16LCommand cmd, byte[]? data = null)
		{
			var list = new List<byte>
			{
				NCU16LProtocol.STX,
				address,
				(byte)cmd
			};

			if (data != null && data.Length > 0)
			{
				list.AddRange(data);
			}

			list.Add(NCU16LProtocol.ETX);

			// Calculate checksum (low byte of sum)
			byte checksum = (byte)(list.Sum(b => (int)b) & 0xFF);
			list.Add(checksum);

			var commandBytes = list.ToArray();

			_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(BuildCommand)}: {cmd} to address 0x{address:X2}");
			_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(BuildCommand)}: STX=0x{NCU16LProtocol.STX:X2}, ADDR=0x{address:X2}, CMD=0x{(byte)cmd:X2}, ETX=0x{NCU16LProtocol.ETX:X2}, SUM=0x{checksum:X2}");
			_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(BuildCommand)}: Full packet: {ToHexString(commandBytes)}");

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

				// Ensure connection is established (lazy connect / auto-reconnect)
				await EnsureConnectedAsync();

				var now = DateTime.Now;
				if (now < _nextAllowedTime)
				{
					var delay = _nextAllowedTime - now;
					_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(SendCommandAsyncWithDelay)}: Delaying command send by {delay.TotalMilliseconds} ms to respect inter-command delay");
					await Task.Delay(delay);
				}
				var responses = await SendCommandAsync(command, expectResponse, multipleResponses, onWrite);
				_nextAllowedTime = DateTime.Now.AddMilliseconds(NCU16LProtocol.INTER_COMMAND_DELAY_MS);
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
				_logger?.LogError($"{nameof(LockControllerClientNCU16L)}:{nameof(SendCommandAsync)}: Not connected to controller");
				throw new InvalidOperationException("Not connected to NCU16L controller");
			}

			if (!await DiscardStaleInputAsync())
			{
				throw new InvalidOperationException("Prijem sa kontrolera nije sinhronizovan (posle oštećenog okvira nema tišine na vezi); komanda nije poslata");
			}

			_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(SendCommandAsync)}: TX → {ToHexString(command)} (expectResponse={expectResponse}, multipleResponses={multipleResponses})");

			onWrite?.Invoke();
			lock (_lock)
			{
				_stream.Write(command, 0, command.Length);
			}

			if (!expectResponse)
			{
				_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(SendCommandAsync)}: Command sent, no response expected");
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

				_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(SendCommandAsync)}: Received {responses.Count} response(s) total");
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
					_logger?.LogWarning($"{nameof(LockControllerClientNCU16L)}:{nameof(SendCommandAsync)}: RX timeout - odgovor na {ToHexString(command)} nije stigao za {RESPONSE_TIMEOUT_MS} ms{pending}");
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
					_logger?.LogWarning($"{nameof(LockControllerClientNCU16L)}:{nameof(DiscardStaleInputAsync)}: podaci sa kontrolera stižu bez prekida duže od {RESYNC_MAX_WAIT_MS} ms; komanda se ne šalje");
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
				_logger?.LogWarning($"{nameof(LockControllerClientNCU16L)}:{nameof(DiscardStaleInputAsync)}: granica okvira je izgubljena, a za {RESYNC_MAX_WAIT_MS} ms od početka pražnjenja nije nastupila tišina od {RESYNC_QUIET_MS} ms (odbačeno {discarded} bajtova); komanda se ne šalje");
				return false;
			}

			_rxBuffer.Resynchronize();
			_logger?.LogWarning($"{nameof(LockControllerClientNCU16L)}:{nameof(DiscardStaleInputAsync)}: granica okvira je bila izgubljena; posle tišine od {RESYNC_QUIET_MS} ms odbačeno još {discarded} bajtova, prijem počinje iz početka");
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
				_logger?.LogWarning($"{nameof(LockControllerClientNCU16L)}:{nameof(ReceiveResponseAsync)}: Stream is null");
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
						_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(ReceiveResponseAsync)}: RX ← {ToHexString(frame)} ({frame.Length} bytes)");
						return frame;
					}

					_logger?.LogWarning($"{nameof(LockControllerClientNCU16L)}:{nameof(ReceiveResponseAsync)}: odbačen okvir koji ne pripada komandi {ToHexString(command)}: {ToHexString(frame)}");
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
					_logger?.LogWarning($"{nameof(LockControllerClientNCU16L)}:{nameof(ReceiveResponseAsync)}: veza je zatvorena sa druge strane");
					return null;
				}
				_rxBuffer.Append(chunk, read);
			}
		}

		/// <summary>
		/// NCU16L ne ponavlja kod komande nego vraća svoj: na 0x30 (stanje) i 0x33 (otvori sve) odgovara sa 0x35,
		/// na 0x32 (stanje svih na magistrali) sa 0x36. Odgovor na stanje jednog NCU-a nosi i njegovu adresu.
		/// </summary>
		private static bool IsResponseTo(byte[] command, byte[] response)
		{
			NCU16LResponse? expectedCmd = (NCU16LCommand)command[2] switch
			{
				NCU16LCommand.GetState or NCU16LCommand.OpenAllLocks => NCU16LResponse.ReturnSingleData,
				NCU16LCommand.GetAllCUStates => NCU16LResponse.ReturnAllData,
				_ => null
			};

			return response.Length >= 3
				   && (expectedCmd == null || (NCU16LResponse)response[2] == expectedCmd)
				   && ((NCU16LCommand)command[2] != NCU16LCommand.GetState || response[1] == command[1]);
		}

		// ==================== PARSERS ====================

		private byte[] ParseLockStates(byte[] response)
		{
			_logger?.LogTrace($"{nameof(LockControllerClientNCU16L)}:{nameof(ParseLockStates)}: Parsing response {ToHexString(response)}");

			if (response == null || response.Length < 9)
			{
				_logger?.LogError($"{nameof(LockControllerClientNCU16L)}:{nameof(ParseLockStates)}: Invalid response length ({response?.Length ?? 0} bytes)");
				throw new InvalidOperationException("Invalid response length");
			}

			if (response[0] != NCU16LProtocol.STX)
			{
				_logger?.LogError($"{nameof(LockControllerClientNCU16L)}:{nameof(ParseLockStates)}: Invalid STX (expected 0x02, got 0x{response[0]:X2})");
				throw new InvalidOperationException("Invalid STX");
			}

			byte calculatedChecksum = (byte)(response.Take(response.Length - 1).Sum(b => (int)b) & 0xFF);
			if (calculatedChecksum != response[response.Length - 1])
			{
				_logger?.LogError($"{nameof(LockControllerClientNCU16L)}:{nameof(ParseLockStates)}: Checksum mismatch (calculated=0x{calculatedChecksum:X2}, received=0x{response[response.Length - 1]:X2})");
				throw new InvalidOperationException("Checksum mismatch");
			}

			// Extract lock states (2 bytes for 16 locks)
			var lockStates = new byte[] { response[3], response[4] };

			_logger?.LogTrace($"{nameof(LockControllerClientNCU16L)}:{nameof(ParseLockStates)}: NCU 0x{response[1]:X2}, LockStates=[0x{response[3]:X2}, 0x{response[4]:X2}], IR=[0x{response[5]:X2}, 0x{response[6]:X2}]");

			var closedLocks = GetClosedLocksList(lockStates);
			_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(ParseLockStates)}: {closedLocks.Count}/16 locks closed {(closedLocks.Count > 0 ? $"[{string.Join(",", closedLocks)}]" : "[]")}");

			return lockStates;
		}

		/// <summary>
		/// Check if a specific lock is closed based on lock state bytes
		/// </summary>
		/// <param name="lockStates">2 bytes representing 16 locks (locks 1-8, locks 9-16)</param>
		/// <param name="lockNumber">Lock number (1-16)</param>
		/// <returns>True if closed, false if open</returns>
		private bool IsLockClosed(byte[] lockStates, int lockNumber)
		{
			if (lockNumber < 1 || lockNumber > 16)
				throw new ArgumentOutOfRangeException(nameof(lockNumber), "Lock number must be 1-16");

			int byteIndex = (lockNumber - 1) / 8;
			int bitIndex = (lockNumber - 1) % 8;

			// Bit=1 means CLOSED, Bit=0 means OPEN (NC switch behavior)
			return (lockStates[byteIndex] & (1 << bitIndex)) != 0;
		}


		/// <summary>
		/// Get list of all closed locks
		/// </summary>
		private List<int> GetClosedLocksList(byte[] lockStates)
		{
			var closedLocks = new List<int>();
			for (int i = 1; i <= 16; i++)
			{
				if (IsLockClosed(lockStates, i))
					closedLocks.Add(i);
			}
			return closedLocks;
		}

		private void ValidateAddress(byte address)
		{
			if (address > 0x0F)		//  && address != NCU16LProtocol.BROADCAST_ADDRESS)
				throw new ArgumentOutOfRangeException(nameof(address), "NCU address must be 0x00-0x0F or 0xF0 (broadcast)");
		}

		private (byte ncuAddress, byte lockNumber) MapDoorNumber(int doorNumber)
		{
			if (doorNumber < 1 || doorNumber > 256)
			{
				throw new ArgumentOutOfRangeException(nameof(doorNumber), "Door number must be 1-256");
			}

			byte ncuAddress = (byte)((doorNumber - 1) / 16);
			byte lockNumber = (byte)((doorNumber - 1) % 16);

			_logger?.LogTrace($"{nameof(LockControllerClientNCU16L)}:{nameof(MapDoorNumber)}: doorNumber={doorNumber} > NCU={ncuAddress}, lock={lockNumber}");

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

				_logger?.LogInformation($"{nameof(LockControllerClientNCU16L)}:{nameof(OpenDoorSingle)}: doorNumber={doorNumber} (NCU={ncuAddress}, lock={lockNumber})");

				await UnlockAsync(ncuAddress, lockNumber);

				var lockStates = await GetStateAsync(ncuAddress);

				bool isClosed = IsLockClosed(lockStates, lockNumber + 1);

				var doorStatus = isClosed ? DoorStatusCode.Closed : DoorStatusCode.Open;

				_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(OpenDoorSingle)}: doorNumber={doorNumber} result={doorStatus}");

				return doorStatus;
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, $"{nameof(LockControllerClientNCU16L)}:{nameof(OpenDoorSingle)}: failed for doorNumber={doorNumber}");
				return null;
			}
		}

		// Unlock se šalje jednom (ovaj kontroler ga ionako ne ponavlja). Pad GetState posle poslatog Unlock-a daje
		// Sent=true i nepoznato stanje: vrata su možda otvorena. Rok i token se, kao u OpenDoorSingle, ne prosleđuju.
		public async ValueTask<DoorOpenAttempt> OpenDoorOnce(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
		{
			bool sent = false;
			try
			{
				var (ncuAddress, lockNumber) = MapDoorNumber(doorNumber);

				_logger?.LogInformation($"{nameof(LockControllerClientNCU16L)}:{nameof(OpenDoorOnce)}: doorNumber={doorNumber} (NCU={ncuAddress}, lock={lockNumber})");

				await UnlockAsync(ncuAddress, lockNumber, () => sent = true);

				var lockStates = await GetStateAsync(ncuAddress);

				var doorStatus = IsLockClosed(lockStates, lockNumber + 1) ? DoorStatusCode.Closed : DoorStatusCode.Open;

				_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(OpenDoorOnce)}: doorNumber={doorNumber} result={doorStatus}");

				return new DoorOpenAttempt(true, doorStatus);
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, $"{nameof(LockControllerClientNCU16L)}:{nameof(OpenDoorOnce)}: failed for doorNumber={doorNumber} (Unlock {(sent ? "poslat" : "nije poslat")})");
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

				_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(CheckDoorSingle)}: doorNumber={doorNumber} (NCU={ncuAddress}, lock={lockNumber})");

				var lockStates = await GetStateAsync(ncuAddress);
				bool isClosed = IsLockClosed(lockStates, lockNumber + 1);

				var doorStatus = isClosed ? DoorStatusCode.Closed : DoorStatusCode.Open;

				_logger?.LogTrace($"{nameof(LockControllerClientNCU16L)}:{nameof(CheckDoorSingle)}: doorNumber={doorNumber} status={doorStatus}");

				return doorStatus;
			}
			catch (Exception ex)
			{
				_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(CheckDoorSingle)}: failed for [{doorNumber}], {ex.Message}");
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

				// await GetAllCUStatesAsync();

				moduleNumber--;

				_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(CheckDoorMultiple)}: module {moduleNumber}");

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
				_logger?.LogWarning(ex, $"{nameof(LockControllerClientNCU16L)}:{nameof(CheckDoorMultiple)}: operation failed for [{moduleNumber}] - BU has failed to respond.");
				return null;
			}
			catch (Exception ex)
			{
				_logger?.LogWarning($"{nameof(LockControllerClientNCU16L)}:{nameof(CheckDoorMultiple)}: failed for [{moduleNumber}], {ex.Message}");
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
				_logger?.LogWarning($"{nameof(LockControllerClientNCU16L)}:{nameof(Dispose)}: komanda nije završena za {CLOSE_WAIT_MS} ms; veza se ipak zatvara");

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
					_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(WaitForQuietBeforeClose)}: pre zatvaranja veze odbačeno {discarded} bajtova");
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException)
			{
				_logger?.LogDebug($"{nameof(LockControllerClientNCU16L)}:{nameof(WaitForQuietBeforeClose)}: {ex.Message}");
			}
		}
	}
}
