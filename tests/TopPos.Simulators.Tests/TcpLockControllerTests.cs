using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Flatsoft.Common;
using TopPos.LockController;
using TopPos.LockController.NCU24L;
using Xunit;

namespace TopPos.LockerSimulator.Tests
{
	/// <summary>
	/// NCU24L simulator se ponaša kao ZLG mrežni modul sa terena (paketomat 112, 2026-10-16): jedna veza, pauza pre
	/// nove, gubitak komande poslate dok se prethodna obrađuje, prefiks 0x00 i blokada posle veze zatvorene dok
	/// odgovor čeka. Windows odbijenu vezu prijavi tek posle ~2 s (ponavlja SYN), pa testovi traju nekoliko sekundi.
	/// </summary>
	public sealed class TcpLockControllerTests : IDisposable
	{
		private static readonly byte[] GetState = [0x02, 0x00, 0x00, 0x80, 0x00, 0x00, 0x03, 0x85];
		private const int GetStateResponseLength = 11;

		private readonly CancellationTokenSource _cts = new();

		public void Dispose()
		{
			_cts.Cancel();
			_cts.Dispose();
		}

		[Fact]
		public async Task Second_connection_is_refused_while_first_is_open()
		{
			var simulator = Start(new NetworkModuleConfiguration { ReleaseDelayMs = 200 });
			using var first = new TcpClient();
			await first.ConnectAsync(IPAddress.Loopback, simulator.Port);
			await Task.Delay(200);	// simulator je prihvatio vezu i zaustavio listener

			using var second = new TcpClient();
			var ex = await Assert.ThrowsAsync<SocketException>(() => second.ConnectAsync(IPAddress.Loopback, simulator.Port));

			Assert.Equal(SocketError.ConnectionRefused, ex.SocketErrorCode);
		}

		[Fact]
		public async Task New_connection_is_accepted_only_after_release_delay()
		{
			var simulator = Start(new NetworkModuleConfiguration { ReleaseDelayMs = 800 });
			using (var first = new TcpClient())
			{
				await first.ConnectAsync(IPAddress.Loopback, simulator.Port);
				var stream = first.GetStream();
				stream.Write(GetState);
				await ReadAsync(stream, 1 + GetStateResponseLength);
			}

			var sw = Stopwatch.StartNew();
			using var second = new TcpClient();
			await second.ConnectAsync(IPAddress.Loopback, simulator.Port);

			Assert.True(sw.ElapsedMilliseconds >= 700, $"nova veza primljena posle {sw.ElapsedMilliseconds} ms");
		}

		[Fact]
		public async Task First_response_has_prefix_and_command_sent_while_busy_is_lost()
		{
			var simulator = Start(new NetworkModuleConfiguration { ReleaseDelayMs = 200 });
			using var client = new TcpClient();
			await client.ConnectAsync(IPAddress.Loopback, simulator.Port);
			var stream = client.GetStream();

			stream.Write([.. GetState, .. GetState]);
			var received = await ReadAllAsync(stream, TimeSpan.FromMilliseconds(500));

			Assert.Equal(1 + GetStateResponseLength, received.Length);
			Assert.Equal(0x00, received[0]);
			Assert.Equal(0x02, received[1]);

			// Prefiks samo pred prvim odgovorom na vezi
			stream.Write(GetState);
			var next = await ReadAllAsync(stream, TimeSpan.FromMilliseconds(500));
			Assert.Equal(GetStateResponseLength, next.Length);
			Assert.Equal(0x02, next[0]);
		}

		[Fact]
		public async Task Connection_closed_before_response_blocks_module()
		{
			var simulator = Start(new NetworkModuleConfiguration { ReleaseDelayMs = 100, ResponseDelayMs = 100, LockAfterAbandonedMs = 4000 });
			var closed = Stopwatch.StartNew();
			using (var first = new TcpClient())
			{
				await first.ConnectAsync(IPAddress.Loopback, simulator.Port);
				first.GetStream().Write(GetState);
			}

			await Task.Delay(500);	// oslobađanje (100 ms) je prošlo, ali modul je blokiran
			using (var blocked = new TcpClient())
			{
				var ex = await Assert.ThrowsAsync<SocketException>(() => blocked.ConnectAsync(IPAddress.Loopback, simulator.Port));
				Assert.Equal(SocketError.ConnectionRefused, ex.SocketErrorCode);
			}

			await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, 4500 - closed.ElapsedMilliseconds)));
			using var after = new TcpClient();
			await after.ConnectAsync(IPAddress.Loopback, simulator.Port);
		}

		[Fact]
		public async Task Real_client_disposed_during_command_does_not_block_module()
		{
			// Teren: veza zatvorena usred očitavanja blokirala je modul. Klijent pri Dispose čeka odgovor.
			var simulator = Start(new NetworkModuleConfiguration { ReleaseDelayMs = 300, ResponseDelayMs = 300, LockAfterAbandonedMs = 60000 });
			var configuration = new TcpControllerConfiguration { IpAddress = "127.0.0.1", Port = simulator.Port };

			var client = new LockControllerClientNCU24L(configuration);
			var read = Task.Run(() => client.CheckDoorMultiple(1, CancellationToken.None).AsTask());
			await Task.Delay(150);	// komanda je poslata, odgovor kasni 300 ms
			client.Dispose();
			Assert.NotNull(await read);

			using var next = new LockControllerClientNCU24L(configuration);
			Assert.NotNull(await next.CheckDoorMultiple(1, CancellationToken.None));
		}

		[Fact]
		public async Task Dropped_unlock_response_is_not_resent_and_state_comes_from_GetState()
		{
			// Kvar DropOpenResponse: vrata su otključana, a potvrda Unlock-a ne stiže. NCU24L klijent potvrdu ne traži
			// (UnlockAsync proverava ASK samo ako odgovor stigne), pa stanje čita iz GetState; Unlock se ne ponavlja.
			int unlocks = 0;
			bool drop = true;
			var simulator = new TcpLockController(IPAddress.Loopback, 0,
				(addr, lockNumber, cmd) => { Interlocked.Increment(ref unlocks); return NCU24LResponseCode.Success; },
				addr => [0x00, 0x00, 0x00],
				_cts.Token,
				dropUnlockResponse: () => { var take = drop; drop = false; return take; });
			using var client = new LockControllerClientNCU24L(new TcpControllerConfiguration { IpAddress = "127.0.0.1", Port = simulator.Port });

			var lost = await client.OpenDoorOnce(1, null, CancellationToken.None);
			Assert.True(lost.Sent);
			Assert.Equal(DoorStatusCode.Open, lost.Status);
			Assert.Equal(1, unlocks);

			var next = await client.OpenDoorOnce(2, null, CancellationToken.None);
			Assert.Equal(DoorStatusCode.Open, next.Status);
			Assert.Equal(2, unlocks);
		}

		private TcpLockController Start(NetworkModuleConfiguration module)
			=> new TcpLockController(IPAddress.Loopback, 0,
				(addr, lockNumber, cmd) => NCU24LResponseCode.Success,
				addr => [0xFF, 0xFF, 0xFF],
				_cts.Token,
				networkModule: module);

		private static async Task<byte[]> ReadAsync(NetworkStream stream, int count)
		{
			var buffer = new byte[count];
			int read = 0;
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			while (read < count)
			{
				int n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
				Assert.True(n > 0, "veza je zatvorena");
				read += n;
			}
			return buffer;
		}

		/// <summary>Sve što stigne za zadato vreme.</summary>
		private static async Task<byte[]> ReadAllAsync(NetworkStream stream, TimeSpan duration)
		{
			var received = new List<byte>();
			var buffer = new byte[256];
			var sw = Stopwatch.StartNew();
			while (sw.Elapsed < duration)
			{
				if (!stream.DataAvailable)
				{
					await Task.Delay(5);
					continue;
				}
				int n = stream.Read(buffer, 0, buffer.Length);
				received.AddRange(buffer.AsSpan(0, n).ToArray());
			}
			return received.ToArray();
		}
	}
}
