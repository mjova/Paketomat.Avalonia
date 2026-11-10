using System.IO;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TopPos.LockController.NCU24L;

namespace TopPos.LockerSimulator
{
	/// <summary>
	/// Simulates an NCU24L lock controller over TCP.
	/// Listens for incoming TCP connections and responds to NCU24L protocol commands.
	/// Uz <see cref="NetworkModuleConfiguration"/> se ponaša kao ZLG mrežni modul sa terena: jedna veza, odbijanje
	/// ostalih, pauza pre nove veze, gubitak komande poslate dok se prethodna obrađuje, prefiks 0x00 i blokada
	/// posle veze zatvorene dok odgovor čeka.
	/// </summary>
	public class TcpLockController
	{
		private readonly IPAddress _listenAddress;
		private readonly int _port;
		private TcpListener _listener;
		private readonly Func<byte, byte, NCU24LCommand, NCU24LResponseCode> _handleCommand;
		private readonly Func<byte, byte[]> _getLockStates;
		private readonly Func<byte, bool>? _isModuleOn;
		private readonly Func<bool>? _dropUnlockResponse;
		private readonly ILogger? _logger;
		private readonly NetworkModuleConfiguration _module;
		private readonly Task _listenTask;
		// Do kada modul odbija svaku vezu (posle veze zatvorene dok je odgovor čekao). v4: monotoni sat (Environment.TickCount64),
		// ne DateTime.Now — pomeranje sata ne sme da skrati ili produži blokadu.
		private long _blockedUntilMs;
		// v4: pauza pre nove veze koju reset (/_sim/reset) može da prekine
		private CancellationTokenSource? _pauseCts;
		private readonly Lock _pauseLock = new();

		public TcpLockController(
			IPAddress listenAddress,
			int port,
			Func<byte, byte, NCU24LCommand, NCU24LResponseCode> handleCommand,
			Func<byte, byte[]> getLockStates,
			CancellationToken cancellationToken,
			ILogger? logger = null,
			Func<byte, bool>? isModuleOn = null,
			NetworkModuleConfiguration? networkModule = null,
			Func<bool>? dropUnlockResponse = null)
		{
			_handleCommand = handleCommand;
			_getLockStates = getLockStates;
			_isModuleOn = isModuleOn;
			_dropUnlockResponse = dropUnlockResponse;
			_logger = logger;
			_module = networkModule ?? new NetworkModuleConfiguration { Enabled = false };

			_listenAddress = listenAddress;
			_listener = new TcpListener(listenAddress, port);
			_listener.Start();
			_port = ((IPEndPoint)_listener.LocalEndpoint).Port;	// i kad je zadat port 0
			_logger?.LogInformation($"TcpLockController: listening on {listenAddress}:{_port}"
				+ (_module.Enabled ? $" (ZLG modul: jedna veza={_module.SingleClient}, oslobađanje {_module.ReleaseDelayMs} ms, odgovor {_module.ResponseDelayMs} ms, blokada {_module.LockAfterAbandonedMs} ms)" : ""));

			_listenTask = AcceptClientsAsync(cancellationToken);
		}

		/// <summary>Port na kome se sluša (korisno kad je zadat port 0).</summary>
		public int Port => _port;

		/// <summary>
		/// v4: za <c>POST /_sim/reset</c> — skida blokadu posle napuštenog odgovora i prekida pauzu, pa listener odmah ponovo
		/// prima vezu (inače bi sledeći E2E scenario do <see cref="NetworkModuleConfiguration.LockAfterAbandonedMs"/> dobijao „refused").
		/// </summary>
		public void ResetNetworkModule()
		{
			Interlocked.Exchange(ref _blockedUntilMs, 0);
			lock (_pauseLock)
			{
				_pauseCts?.Cancel();
			}
		}

		/// <summary>v4: završava se posle otkazivanja tokena, kad su zatvoreni listener i veza (uredno gašenje simulatora).</summary>
		public Task Completion => _listenTask;

		private bool SingleClient => _module.Enabled && _module.SingleClient;

		private async Task AcceptClientsAsync(CancellationToken ct)
		{
			try
			{
				while (!ct.IsCancellationRequested)
				{
					try
					{
						var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
						_logger?.LogInformation($"TcpLockController: client connected from {client.Client.RemoteEndPoint}");

						if (!SingleClient)
						{
							_ = HandleClientAsync(client, ct);
							continue;
						}

						// Jedna veza: dok traje, listener je zaustavljen, pa OS odbija nove veze — klijent vidi „refused",
						// kao na terenu. Posle zatvaranja mesto se oslobađa tek posle pauze (ili blokade).
						_listener.Stop();
						await HandleClientAsync(client, ct).ConfigureAwait(false);

						var pause = TimeSpan.FromMilliseconds(_module.ReleaseDelayMs);
						var blocked = TimeSpan.FromMilliseconds(_blockedUntilMs - Environment.TickCount64);
						if (blocked > pause)
							pause = blocked;
						_logger?.LogInformation($"TcpLockController: nova veza se prima za {pause.TotalMilliseconds:0} ms");
						await PauseAsync(pause, ct).ConfigureAwait(false);
						await RestartListenerAsync(ct).ConfigureAwait(false);
					}
					catch (OperationCanceledException) { break; }
					catch (Exception ex)
					{
						_logger?.LogError(ex, "TcpLockController: error accepting client");
						// v4: npr. listener nije mogao ponovo da se pokrene — bez pauze bi se petlja vrtela
						await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
					}
				}
			}
			finally
			{
				// v4: uredno gašenje — port se oslobađa odmah, ne kad GC pokupi listener
				_listener.Stop();
			}
		}

		/// <summary>v4: pauza koju prekida <see cref="ResetNetworkModule"/> (ili gašenje — tada <see cref="OperationCanceledException"/>).</summary>
		private async Task PauseAsync(TimeSpan pause, CancellationToken ct)
		{
			using var pauseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			lock (_pauseLock)
			{
				_pauseCts = pauseCts;
			}
			try
			{
				await Task.Delay(pause, pauseCts.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
				_logger?.LogInformation("TcpLockController: pauza je prekinuta (reset)");
			}
			finally
			{
				lock (_pauseLock)
				{
					_pauseCts = null;
				}
			}
		}

		/// <summary>v4: posle pauze port može biti zauzet (npr. drugi proces) — pokušava se ponovo, bez izlaska iz petlje.</summary>
		private async Task RestartListenerAsync(CancellationToken ct)
		{
			for (;;)
			{
				try
				{
					_listener = new TcpListener(_listenAddress, _port);
					_listener.Start();
					return;
				}
				catch (SocketException ex)
				{
					_logger?.LogWarning($"TcpLockController: port {_port} nije slobodan ({ex.SocketErrorCode}), novi pokušaj za 1 s");
					await Task.Delay(1000, ct).ConfigureAwait(false);
				}
			}
		}

		private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
		{
			// v4: ReadByte ne prima token — pri gašenju se veza zatvara, pa čitanje odmah završava
			using var closeOnCancel = ct.Register(client.Close);
			using (client)
			await using (var stream = client.GetStream())
			{
				bool prefixPending = _module.Enabled && _module.FirstResponsePrefix;

				while (!ct.IsCancellationRequested && client.Connected)
				{
					try
					{
						// Read until we find STX
						int b;
						do
						{
							b = stream.ReadByte();
							if (b == -1) return; // Client disconnected
						} while (b != NCU24LProtocol.STX);

						// Read remaining 7 header bytes: ADDR + LOCKNUM + CMD + ASK + DATALEN + ETX + SUM
						var header = new byte[7];
						int read = 0;
						while (read < 7)
						{
							int n = await stream.ReadAsync(header, read, 7 - read, ct).ConfigureAwait(false);
							if (n == 0) return;
							read += n;
						}

						byte addr = header[0];
						byte lockNum = header[1];
						var cmd = (NCU24LCommand)header[2];
						byte ask = header[3];
						byte dataLen = header[4];
						byte etx = header[5];
						byte checksum = header[6];

						// Read DATA bytes if any
						byte[]? data = null;
						if (dataLen > 0)
						{
							data = new byte[dataLen];
							read = 0;
							while (read < dataLen)
							{
								int n = await stream.ReadAsync(data, read, dataLen - read, ct).ConfigureAwait(false);
								if (n == 0) return;
								read += n;
							}
						}

						_logger?.LogDebug($"TcpLockController: RX CMD=0x{(byte)cmd:X2} ADDR=0x{addr:X2} LOCKNUM=0x{lockNum:X2} DATALEN={dataLen}");

						if (_module.Enabled && !await ProcessLikeNetworkModuleAsync(client, stream, ct).ConfigureAwait(false))
							return;

						// Build and send response
						var response = BuildResponse(addr, lockNum, cmd, data);
						if (response != null)
						{
							if (prefixPending)
							{
								response = [0x00, .. response];
								prefixPending = false;
							}

							try
							{
								await stream.WriteAsync(response, 0, response.Length, ct).ConfigureAwait(false);
							}
							catch (IOException)
							{
								BlockAfterAbandonedResponse();
								return;
							}
							_logger?.LogDebug($"TcpLockController: TX {BitConverter.ToString(response)}");
						}
					}
					catch (OperationCanceledException) { break; }
					catch (IOException) { break; } // Client disconnected
					catch (Exception ex)
					{
						_logger?.LogError(ex, "TcpLockController: error processing command");
					}
				}
			}
			_logger?.LogInformation("TcpLockController: client disconnected");
		}

		/// <summary>
		/// Obrada komande kao iza ZLG modula: odgovor kasni, komanda stigla u međuvremenu se gubi, a ako je klijent
		/// u međuvremenu zatvorio vezu, odgovor se ne šalje i modul se blokira. False = veza je zatvorena.
		/// </summary>
		private async Task<bool> ProcessLikeNetworkModuleAsync(TcpClient client, NetworkStream stream, CancellationToken ct)
		{
			if (_module.ResponseDelayMs > 0)
				await Task.Delay(_module.ResponseDelayMs, ct).ConfigureAwait(false);

			if (_module.DropCommandWhileBusy && client.Available > 0)
			{
				var dropped = new byte[client.Available];
				int n = stream.Read(dropped, 0, dropped.Length);
				_logger?.LogInformation($"TcpLockController: izgubljeno {n} B — komanda je stigla dok se prethodna obrađivala: {BitConverter.ToString(dropped, 0, n)}");
			}

			// Klijent je zatvorio vezu (FIN ili RST), a nepročitanih bajtova nema
			if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0)
			{
				BlockAfterAbandonedResponse();
				return false;
			}

			return true;
		}

		private void BlockAfterAbandonedResponse()
		{
			if (!SingleClient || _module.LockAfterAbandonedMs <= 0)
				return;

			_blockedUntilMs = Environment.TickCount64 + _module.LockAfterAbandonedMs;
			_logger?.LogWarning($"TcpLockController: veza je zatvorena dok je odgovor čekao — modul odbija veze {_module.LockAfterAbandonedMs / 1000.0:0.#} s (ZLG na terenu, 2026-10-16)");
		}

		private byte[]? BuildResponse(byte addr, byte lockNum, NCU24LCommand cmd, byte[]? requestData)
		{
			if (_isModuleOn != null && !_isModuleOn(addr))
			{
				_logger?.LogDebug($"TcpLockController: module {addr} is OFF, not responding");
				return null;
			}

			switch (cmd)
			{
				case NCU24LCommand.GetState:
					return BuildGetStateResponse(addr);

				case NCU24LCommand.Unlock:
				{
					var result = _handleCommand(addr, lockNum, cmd);
					// Kvar za E2E (DropOpenResponse): komanda je izvršena, a odgovor se ne šalje.
					if (result == NCU24LResponseCode.Success && _dropUnlockResponse?.Invoke() == true)
					{
						_logger?.LogWarning($"TcpLockController: Unlock ADDR=0x{addr:X2} LOCKNUM=0x{lockNum:X2} izvršen, odgovor se ne šalje (kvar DropOpenResponse)");
						return null;
					}
					return BuildSimpleResponse(addr, lockNum, cmd, result);
				}

				default:
					_logger?.LogWarning($"TcpLockController: unsupported command 0x{(byte)cmd:X2}");
					return BuildSimpleResponse(addr, lockNum, cmd, NCU24LResponseCode.UnknownCommand);
			}
		}

		private byte[] BuildGetStateResponse(byte addr)
		{
			var lockStates = _getLockStates(addr);
			return BuildPacket(addr, 0x00, NCU24LCommand.GetState, NCU24LResponseCode.Success, lockStates);
		}

		private byte[] BuildSimpleResponse(byte addr, byte lockNum, NCU24LCommand cmd, NCU24LResponseCode askCode)
		{
			return BuildPacket(addr, lockNum, cmd, askCode, null);
		}

		private static byte[] BuildPacket(byte addr, byte lockNum, NCU24LCommand cmd, NCU24LResponseCode ask, byte[]? data)
		{
			byte dataLen = (byte)(data?.Length ?? 0);

			var header = new byte[]
			{
				NCU24LProtocol.STX,
				addr,
				lockNum,
				(byte)cmd,
				(byte)ask,
				dataLen,
				NCU24LProtocol.ETX
			};

			int sum = 0;
			foreach (var b in header) sum += b;
			if (data != null)
				foreach (var b in data) sum += b;
			byte checksum = (byte)(sum & 0xFF);

			var packet = new List<byte>(header) { checksum };
			if (data != null)
				packet.AddRange(data);

			return packet.ToArray();
		}
	}
}
