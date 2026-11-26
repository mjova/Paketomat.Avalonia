using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Flatsoft.Common;
using Microsoft.Extensions.Logging;
using Flatsoft.Common.UI;
using TopPos.LockController;
using TopPos.LockController.NCU16L;
using TopPos.LockController.NCU24L;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// NCU klijenti ne smeju da pročitaju tuđi odgovor kao odgovor na svoju komandu. Na terenu (paketomat 112, 2026-10-16)
	/// ZLG modul je isporučio dupli odgovor na GetState; ostao je u baferu, Unlock ga je pročitao kao svoj, a provera stanja
	/// posle otvaranja pročitala je odgovor na Unlock → „kontroler nije odgovorio", iako su se vrata otvorila.
	/// Klijent zato pre slanja prazni ulaz i preskače okvire koji ne pripadaju poslatoj komandi.
	/// </summary>
	// v4: jedan test postavlja UiThread.Dispatcher, pa klasa ide u kolekciju sa ostalim testovima UI niti.
	[Collection(nameof(UiThreadCollection))]
	public class NcuStaleResponseTests : IDisposable
	{
		private readonly IUiDispatcher _original = UiThread.Dispatcher;

		public void Dispose() => UiThread.Dispatcher = _original;

		// ==================== NCU24L ====================

		[Fact]
		public async Task Ncu24l_duplicate_response_left_in_buffer_does_not_break_door_open()
		{
			await using var server = new FakeNcuServer(requestLength: 8, (request, index) => index switch {
				0 => [Ncu24lState(0x00, 0xFF, 0xFF, 0xFF), Ncu24lState(0x00, 0xFF, 0xFF, 0xFF)],	// GetState + zakasneli duplikat
				1 => [Ncu24lUnlock(0x00, request[2])],													// Unlock vrata 3
				_ => [Ncu24lState(0x00, 0xFB, 0xFF, 0xFF)]												// vrata 3 otvorena
			});
			using var client = new LockControllerClientNCU24L(server.Configuration);

			var before = await client.CheckDoorMultiple(1, CancellationToken.None);
			var opened = await client.OpenDoorSingle(3, CancellationToken.None);

			Assert.NotNull(before);
			Assert.All(before!, status => Assert.True(status.IsClosed));
			Assert.NotNull(opened);
			Assert.True(opened!.Value.IsOpen);
		}

		[Fact]
		public async Task Ncu24l_foreign_frame_before_response_is_skipped()
		{
			await using var server = new FakeNcuServer(requestLength: 8, (request, index) =>
				[Ncu24lUnlock(0x00, 0x04), Ncu24lState(0x00, 0x01, 0x00, 0x00)]);	// tuđi Unlock odgovor, pa pravi GetState
			using var client = new LockControllerClientNCU24L(server.Configuration);

			var statuses = await client.CheckDoorMultiple(1, CancellationToken.None);

			Assert.NotNull(statuses);
			Assert.True(statuses![0].IsClosed);
			Assert.All(statuses.Skip(1), status => Assert.True(status.IsOpen));
		}

		[Fact]
		public async Task Ncu24l_fragmented_duplicate_rest_arriving_after_next_command_does_not_break_door_open()
		{
			// Duplikat sa 0x02 u podacima: posle pražnjenja ostatak sadrži lažne STX ispred pravog odgovora.
			var duplicate = Ncu24lState(0x00, 0x02, 0x02, 0xFF);
			await using var server = new FakeNcuServer(requestLength: 8, (request, index) => index switch {
				0 => [Ncu24lState(0x00, 0xFF, 0xFF, 0xFF), duplicate[..1]],		// odgovor + samo STX duplikata
				1 => [duplicate[1..], Ncu24lUnlock(0x00, request[2])],			// ostatak duplikata, pa odgovor na Unlock
				_ => [Ncu24lState(0x00, 0xFB, 0xFF, 0xFF)]
			});
			using var client = new LockControllerClientNCU24L(server.Configuration);

			var before = await client.CheckDoorMultiple(1, CancellationToken.None);
			var opened = await client.OpenDoorSingle(3, CancellationToken.None);

			Assert.NotNull(before);
			Assert.NotNull(opened);
			Assert.True(opened!.Value.IsOpen);
		}

		[Fact]
		public async Task Ncu24l_response_split_across_tcp_segments_is_assembled()
		{
			var response = Ncu24lState(0x00, 0x01, 0x00, 0x00);
			await using var server = new FakeNcuServer(requestLength: 8, (request, index) => [response[..5], response[5..]]);
			using var client = new LockControllerClientNCU24L(server.Configuration);

			var statuses = await client.CheckDoorMultiple(1, CancellationToken.None);

			Assert.NotNull(statuses);
			Assert.True(statuses![0].IsClosed);
			Assert.All(statuses.Skip(1), status => Assert.True(status.IsOpen));
		}

		[Fact]
		public async Task Ncu24l_false_candidate_with_valid_checksum_does_not_eat_real_response()
		{
			// Ostatak starog okvira + početak novog daju kandidat „02 00 80 10 03 03 03 03 66 00 02" sa ispravnom sumom,
			// čiji je poslednji bajt STX pravog odgovora. Lažni kandidat ne sme da se ukloni iz bafera.
			var old = Ncu24lState(0x02, 0x03, 0x66, 0x00);
			var real = Ncu24lState(0x02, 0xFF, 0xFF, 0xFF);
			await using var server = new FakeNcuServer(requestLength: 8, (request, index) => index switch {
				0 => [Ncu24lState(0x02, 0x01, 0x00, 0x00), old[..1]],
				_ => [[.. old[1..], .. real]]
			});
			using var client = new LockControllerClientNCU24L(server.Configuration);

			var first = await client.CheckDoorMultiple(3, CancellationToken.None);
			var second = await client.CheckDoorMultiple(3, CancellationToken.None);

			Assert.NotNull(first);
			Assert.NotNull(second);
			Assert.All(second!, status => Assert.True(status.IsClosed));
		}

		[Fact]
		public async Task Ncu24l_corrupted_length_never_returns_previous_command_response()
		{
			// Codex: DATALEN oštećen (03 → 13), iza njega odgovor A. Prva provera ističe; druga ne sme da vrati A
			// (sva vrata zatvorena) umesto trenutnog B (sva otvorena); posle oporavka treća čita B.
			var corrupted = Bytes24("02 00 00 80 10 13 03 98 00 00 00");
			var responseA = Ncu24lState(0x00, 0xFF, 0xFF, 0xFF);
			var responseB = Ncu24lState(0x00, 0x00, 0x00, 0x00);
			await using var server = new FakeNcuServer(requestLength: 8, (request, index) => index switch {
				0 => [corrupted, responseA],
				_ => [responseB]
			});
			using var client = new LockControllerClientNCU24L(server.Configuration);

			var first = await client.CheckDoorMultiple(1, CancellationToken.None);
			var second = await client.CheckDoorMultiple(1, CancellationToken.None);
			var third = await client.CheckDoorMultiple(1, CancellationToken.None);

			Assert.Null(first);
			Assert.True(second == null || second.All(status => status.IsOpen));
			Assert.NotNull(third);
			Assert.All(third!, status => Assert.True(status.IsOpen));
		}

		[Fact]
		public async Task Ncu24l_command_is_not_sent_until_link_is_quiet_after_corrupted_frame()
		{
			// Codex: posle oštećenog okvira bez prekida stižu zakasneli odgovori A (100 okvira po 20+ ms, najmanje ~1,6 s).
			// Dok nema tišine, komanda se ne šalje — inače bi A bio pročitan kao odgovor na nju (sva vrata zatvorena umesto
			// stvarnog B, sva otvorena). Kad A prestanu, klijent se oporavlja u nekoliko pokušaja (DoorWatcher i inače
			// ponavlja očitavanje).
			var corrupted = Bytes24("02 00 00 80 10 03 00 98 00 00 00");	// ETX oštećen
			var responseA = Ncu24lState(0x00, 0xFF, 0xFF, 0xFF);
			var responseB = Ncu24lState(0x00, 0x00, 0x00, 0x00);
			byte[][] noise = [corrupted, .. Enumerable.Range(0, 100).Select(_ => responseA)];
			await using var server = new FakeNcuServer(requestLength: 8, (request, index) => index switch {
				0 => noise,
				_ => [responseB]
			});
			using var client = new LockControllerClientNCU24L(server.Configuration);

			var first = await client.CheckDoorMultiple(1, CancellationToken.None);
			var second = await client.CheckDoorMultiple(1, CancellationToken.None);

			DoorStatusCode[]? recovered = null;
			for (int attempt = 0; attempt < 5 && recovered == null; attempt++) {
				recovered = await client.CheckDoorMultiple(1, CancellationToken.None);
			}

			Assert.Null(first);
			Assert.Null(second);
			Assert.NotNull(recovered);
			Assert.All(recovered!, status => Assert.True(status.IsOpen));
		}

		[Fact]
		public async Task Ncu24l_continuous_input_does_not_hold_command_lock_beyond_limit()
		{
			// Codex: dok podaci stižu bez prekida, pražnjenje pre komande ne sme da traje bez kraja — drži se pod semaforom
			// komandi, pa bi stajalo i otvaranje vrata. Posle roka (1 s) komanda se ne šalje i poziv se završava.
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			var flood = Task.Run(async () => {
				try
				{
					using var connection = await listener.AcceptTcpClientAsync(cts.Token);
					var stream = connection.GetStream();
					await stream.ReadExactlyAsync(new byte[8], cts.Token);
					await stream.WriteAsync(Bytes24("02 00 00 80 10 03 00 98 00 00 00"), cts.Token);	// ETX oštećen
					var chunk = new byte[64 * 1024];
					while (!cts.IsCancellationRequested) {
						await stream.WriteAsync(chunk, cts.Token);
					}
				}
				catch (OperationCanceledException) { }
				catch (IOException) { }
				catch (SocketException) { }
			});
			var configuration = new TcpControllerConfiguration { IpAddress = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };

			DoorStatusCode[]? first, second;
			TimeSpan elapsed;
			using (var client = new LockControllerClientNCU24L(configuration))
			{
				first = await client.CheckDoorMultiple(1, CancellationToken.None);
				var stopwatch = Stopwatch.StartNew();
				second = await client.CheckDoorMultiple(1, CancellationToken.None);
				elapsed = stopwatch.Elapsed;
			}
			cts.Cancel();
			listener.Stop();
			await flood;

			Assert.Null(first);
			Assert.Null(second);
			Assert.True(elapsed < TimeSpan.FromSeconds(3), $"druga provera je trajala {elapsed}");
		}

		private static byte[] Bytes24(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

		// ==================== NCU16L ====================

		[Fact]
		public async Task Ncu16l_duplicate_response_left_in_buffer_is_discarded()
		{
			await using var server = new FakeNcuServer(requestLength: 5, (request, index) => index switch {
				0 => [Ncu16lFrame(0x00, 0x35, 0x01, 0x00), Ncu16lFrame(0x00, 0x35, 0x00, 0x00)],	// vrata 1 zatvorena + zakasneli duplikat
				_ => [Ncu16lFrame(0x00, 0x35, 0x02, 0x00)]										// vrata 2 zatvorena
			});
			using var client = new LockControllerClientNCU16L(server.Configuration);

			var first = await client.CheckDoorMultiple(1, CancellationToken.None);
			var second = await client.CheckDoorMultiple(1, CancellationToken.None);

			Assert.NotNull(first);
			Assert.True(first![0].IsClosed);
			Assert.NotNull(second);
			Assert.True(second![0].IsOpen);
			Assert.True(second[1].IsClosed);
		}

		[Fact]
		public async Task Ncu16l_foreign_frame_before_response_is_skipped()
		{
			await using var server = new FakeNcuServer(requestLength: 5, (request, index) =>
				[Ncu16lFrame(0x00, 0x38, 0xFF, 0xFF), Ncu16lFrame(0x00, 0x35, 0x01, 0x00)]);	// tuđi 0x38, pa pravi 0x35
			using var client = new LockControllerClientNCU16L(server.Configuration);

			var statuses = await client.CheckDoorMultiple(1, CancellationToken.None);

			Assert.NotNull(statuses);
			Assert.True(statuses![0].IsClosed);
			Assert.All(statuses.Skip(1), status => Assert.True(status.IsOpen));
		}

		[Fact]
		public async Task Ncu16l_fragmented_duplicate_rest_arriving_after_next_command_is_skipped()
		{
			// Duplikat sa 0x02 u podacima: posle pražnjenja ostatak sadrži lažne STX ispred pravog odgovora.
			var duplicate = Ncu16lFrame(0x00, 0x35, 0x02, 0x02);
			await using var server = new FakeNcuServer(requestLength: 5, (request, index) => index switch {
				0 => [Ncu16lFrame(0x00, 0x35, 0x01, 0x00), duplicate[..1]],		// vrata 1 zatvorena + samo STX duplikata
				_ => [[.. duplicate[1..], .. Ncu16lFrame(0x00, 0x35, 0x04, 0x00)]]	// ostatak duplikata i vrata 3 zatvorena, u istom segmentu
			});
			using var client = new LockControllerClientNCU16L(server.Configuration);

			var first = await client.CheckDoorMultiple(1, CancellationToken.None);
			var second = await client.CheckDoorMultiple(1, CancellationToken.None);

			Assert.NotNull(first);
			Assert.True(first![0].IsClosed);
			Assert.NotNull(second);
			Assert.True(second![2].IsClosed);
			Assert.True(second[0].IsOpen);
			Assert.True(second[1].IsOpen);
		}

		[Fact]
		public async Task Ncu16l_response_split_across_tcp_segments_is_assembled()
		{
			var response = Ncu16lFrame(0x00, 0x35, 0x01, 0x00);
			await using var server = new FakeNcuServer(requestLength: 5, (request, index) => [response[..4], response[4..]]);
			using var client = new LockControllerClientNCU16L(server.Configuration);

			var statuses = await client.CheckDoorMultiple(1, CancellationToken.None);

			Assert.NotNull(statuses);
			Assert.True(statuses![0].IsClosed);
			Assert.All(statuses.Skip(1), status => Assert.True(status.IsOpen));
		}

		[Fact]
		public async Task Ncu16l_false_candidate_with_valid_checksum_does_not_eat_real_response()
		{
			// Ostatak starog okvira + početak novog daju kandidat „02 03 C1 02 00 35 00 03 00" sa ispravnom sumom,
			// koji sadrži početak pravog odgovora. Lažni kandidat ne sme da se ukloni iz bafera.
			var old = Ncu16lFrame(0x00, 0x35, 0x00, 0x85, 0x00, 0x02);
			var real = Ncu16lFrame(0x00, 0x35, 0x00, 0x03);
			await using var server = new FakeNcuServer(requestLength: 5, (request, index) => index switch {
				0 => [Ncu16lFrame(0x00, 0x35, 0x01, 0x00), old[..1]],
				_ => [[.. old[1..], .. real]]
			});
			using var client = new LockControllerClientNCU16L(server.Configuration);

			var first = await client.CheckDoorMultiple(1, CancellationToken.None);
			var second = await client.CheckDoorMultiple(1, CancellationToken.None);

			Assert.NotNull(first);
			Assert.NotNull(second);
			Assert.True(second![8].IsClosed);
			Assert.True(second[9].IsClosed);
			Assert.All(second.Where((_, i) => i != 8 && i != 9), status => Assert.True(status.IsOpen));
		}

		// ==================== ZATVARANJE I PONOVNO POVEZIVANJE (ZLG MODUL) ====================
		// Teren (2026-10-16): veza zatvorena dok odgovor još stiže ostavlja ZLG modul da minutima odbija svaku novu vezu,
		// a posle urednog zatvaranja modul prima novu vezu tek ~1,5–2 s kasnije.

		[Fact]
		public async Task Ncu24l_dispose_waits_for_response_before_closing_connection()
		{
			await using var server = new FakeNcuServer(requestLength: 8, (request, index) => [Ncu24lState(0x00, 0xFF, 0xFF, 0xFF)],
				replyDelay: TimeSpan.FromMilliseconds(300));
			var client = new LockControllerClientNCU24L(server.Configuration);

			var read = Task.Run(() => client.CheckDoorMultiple(1, CancellationToken.None).AsTask());
			await WaitUntil(() => server.RequestCount == 1);
			client.Dispose();

			Assert.NotNull(await read);
		}

		[Fact]
		public async Task Ncu24l_command_after_dispose_does_not_reconnect()
		{
			await using var server = new FakeNcuServer(requestLength: 8, (request, index) => [Ncu24lState(0x00, 0xFF, 0xFF, 0xFF)]);
			var client = new LockControllerClientNCU24L(server.Configuration);
			Assert.NotNull(await client.CheckDoorMultiple(1, CancellationToken.None));

			client.Dispose();
			var after = await client.CheckDoorMultiple(1, CancellationToken.None);

			Assert.Null(after);
			Assert.Equal(1, server.ConnectionCount);
		}

		[Fact]
		public async Task Ncu24l_refused_connection_is_retried()
		{
			int port = FreePort();
			var refused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			using var client = new LockControllerClientNCU24L(
				new TcpControllerConfiguration { IpAddress = "127.0.0.1", Port = port },
				new CallbackLogger(message => {
					if (message.Contains("odbio vezu") || message.Contains("failed to respond")) {
						refused.TrySetResult();
					}
				}));

			var read = client.CheckDoorMultiple(1, CancellationToken.None).AsTask();
			await refused.Task.WaitAsync(TimeSpan.FromSeconds(10));
			// Modul je oslobodio mesto tek posle prvog (odbijenog) pokušaja.
			await using var server = new FakeNcuServer(requestLength: 8, (request, index) => [Ncu24lState(0x00, 0xFF, 0xFF, 0xFF)], port);

			Assert.NotNull(await read);
		}

		[Fact]
		public async Task Ncu16l_dispose_waits_for_response_before_closing_connection()
		{
			await using var server = new FakeNcuServer(requestLength: 5, (request, index) => [Ncu16lFrame(0x00, 0x35, 0xFF, 0xFF)],
				replyDelay: TimeSpan.FromMilliseconds(300));
			var client = new LockControllerClientNCU16L(server.Configuration);

			var read = Task.Run(() => client.CheckDoorMultiple(1, CancellationToken.None).AsTask());
			await WaitUntil(() => server.RequestCount == 1);
			client.Dispose();

			Assert.NotNull(await read);
		}

		[Fact]
		public async Task Ncu16l_refused_connection_is_retried()
		{
			int port = FreePort();
			var refused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			using var client = new LockControllerClientNCU16L(
				new TcpControllerConfiguration { IpAddress = "127.0.0.1", Port = port },
				new CallbackLogger(message => {
					if (message.Contains("odbio vezu") || message.Contains("failed to respond")) {
						refused.TrySetResult();
					}
				}));

			var read = client.CheckDoorMultiple(1, CancellationToken.None).AsTask();
			await refused.Task.WaitAsync(TimeSpan.FromSeconds(10));
			await using var server = new FakeNcuServer(requestLength: 5, (request, index) => [Ncu16lFrame(0x00, 0x35, 0xFF, 0xFF)], port);

			Assert.NotNull(await read);
		}

		[Fact]
		public async Task Kiosk_exit_waits_for_command_started_on_ui_thread()
		{
			// Kiosk otvara vrata sa UI niti, pa se komanda nastavlja na UI niti. Zatvaranje pri izlasku (takođe na UI
			// niti) ne sme da je zadrži: Dispose bi čekao do isteka roka i ipak zatvorio vezu usred odgovora.
			await using var server = new FakeNcuServer(requestLength: 8, (request, index) => [Ncu24lState(0x00, 0xFF, 0xFF, 0xFF)],
				replyDelay: TimeSpan.FromMilliseconds(300));
			var client = new LockControllerClientNCU24L(server.Configuration);
			DoorStatusCode[]? result = null;
			TimeSpan closeDuration = default;
			Exception? failure = null;

			// v4: UI nit glumi PumpingDispatcher (v2: WPF Dispatcher na STA niti); nastavci komande idu u njegov red kroz
			// SynchronizationContext, a LockControllerShutdown ga vrti kroz WaitWithMessagePump, kao WPF DispatcherFrame.
			var ui = new Thread(() => {
				var dispatcher = new PumpingDispatcher();
				UiThread.Dispatcher = dispatcher;
				dispatcher.Run(async () => {
					try
					{
						var read = client.CheckDoorMultiple(1, CancellationToken.None).AsTask();
						while (server.RequestCount == 0) {
							await Task.Delay(5);
						}

						var sw = Stopwatch.StartNew();
						new LockControllerShutdown(client, logger: null).Close();
						closeDuration = sw.Elapsed;
						result = await read;
					}
					catch (Exception ex) {
						failure = ex;
					}
				});
			});
			ui.Start();

			Assert.True(ui.Join(TimeSpan.FromSeconds(20)), "UI nit nije završila");
			Assert.Null(failure);
			Assert.NotNull(result);
			Assert.True(closeDuration < TimeSpan.FromSeconds(3), $"zatvaranje je trajalo {closeDuration}");
		}

		private static int FreePort()
		{
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			int port = ((IPEndPoint)listener.LocalEndpoint).Port;
			listener.Stop();
			return port;
		}

		private static async Task WaitUntil(Func<bool> condition)
		{
			var giveUp = DateTime.Now.AddSeconds(5);
			while (!condition()) {
				Assert.True(DateTime.Now < giveUp, "uslov nije ispunjen za 5 s");
				await Task.Delay(5);
			}
		}

		private sealed class CallbackLogger(Action<string> onMessage) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => true;

			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
				=> onMessage(formatter(state, exception));
		}

		// ==================== OKVIRI ====================

		/// <summary>NCU24L odgovor na GetState: STX ADDR LOCKNUM CMD ASK DATALEN ETX SUM DATA(3).</summary>
		internal static byte[] Ncu24lState(byte addr, byte locks1to8, byte locks9to16, byte locks17to24)
			=> WithNcu24lChecksum([0x02, addr, 0x00, 0x80, 0x10, 0x03, 0x03, 0x00, locks1to8, locks9to16, locks17to24]);

		/// <summary>NCU24L odgovor na Unlock: ponavlja ADDR, LOCKNUM i CMD, bez podataka.</summary>
		internal static byte[] Ncu24lUnlock(byte addr, byte lockNumber)
			=> WithNcu24lChecksum([0x02, addr, lockNumber, 0x81, 0x10, 0x00, 0x03, 0x00]);

		internal static byte[] WithNcu24lChecksum(byte[] frame)
		{
			int sum = 0;
			for (int i = 0; i < frame.Length; i++) {
				if (i != 7) {
					sum += frame[i];
				}
			}
			frame[7] = (byte)(sum & 0xFF);
			return frame;
		}

		/// <summary>NCU16L odgovor: STX ADDR CMD DATA(4) ETX SUM; DATA = brave 1–8, 9–16, IR 1–8, IR 9–16.</summary>
		internal static byte[] Ncu16lFrame(byte addr, byte cmd, byte locks1to8, byte locks9to16, byte ir1to8 = 0x00, byte ir9to16 = 0x00)
		{
			byte[] frame = [0x02, addr, cmd, locks1to8, locks9to16, ir1to8, ir9to16, 0x03, 0x00];
			frame[8] = (byte)(frame.Take(8).Sum(b => b) & 0xFF);
			return frame;
		}

		/// <summary>
		/// Lažni NCU kontroler na slobodnom (ili zadatom) portu: prima veze jednu po jednu i na svaki zahtev zadate
		/// dužine, posle <c>replyDelay</c>, šalje delove koje vrati <c>reply</c> (zahtev, redni broj zahteva).
		/// Svaki deo ide posebnim upisom, sa razmakom <see cref="SegmentGap"/> — kao odvojeni TCP segmenti, pa deo
		/// može da bude i komad okvira.
		/// </summary>
		internal sealed class FakeNcuServer : IAsyncDisposable
		{
			/// <summary>Razmak između delova odgovora; kraći od razmaka između komandi klijenta (200 ms).</summary>
			private static readonly TimeSpan SegmentGap = TimeSpan.FromMilliseconds(20);

			private readonly TcpListener _listener;
			private readonly CancellationTokenSource _cts = new();
			private readonly Task _loop;
			private readonly TimeSpan _replyDelay;
			private int _requestCount;
			private int _connectionCount;

			public FakeNcuServer(int requestLength, Func<byte[], int, byte[][]> reply, int port = 0, TimeSpan replyDelay = default)
			{
				_replyDelay = replyDelay;
				_listener = new TcpListener(IPAddress.Loopback, port);
				_listener.Start();
				_loop = Task.Run(() => Serve(requestLength, reply));
			}

			public TcpControllerConfiguration Configuration
				=> new() { IpAddress = "127.0.0.1", Port = ((IPEndPoint)_listener.LocalEndpoint).Port };

			/// <summary>Koliko je zahteva primljeno (broji se pre slanja odgovora).</summary>
			public int RequestCount => Volatile.Read(ref _requestCount);

			/// <summary>Koliko je veza prihvaćeno.</summary>
			public int ConnectionCount => Volatile.Read(ref _connectionCount);

			private async Task Serve(int requestLength, Func<byte[], int, byte[][]> reply)
			{
				try
				{
					while (true)
					{
						using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
						Interlocked.Increment(ref _connectionCount);
						await ServeConnection(client.GetStream(), requestLength, reply);
					}
				}
				catch (OperationCanceledException) { }
				catch (ObjectDisposedException) { }
				catch (SocketException) { }
				catch (InvalidOperationException) { }	// listener je zaustavljen
			}

			private async Task ServeConnection(NetworkStream stream, int requestLength, Func<byte[], int, byte[][]> reply)
			{
				try
				{
					while (true)
					{
						var request = new byte[requestLength];
						int read = 0;
						while (read < requestLength)
						{
							int n = await stream.ReadAsync(request.AsMemory(read), _cts.Token);
							if (n == 0) {
								return;
							}
							read += n;
						}

						int index = Interlocked.Increment(ref _requestCount) - 1;
						if (_replyDelay > TimeSpan.Zero) {
							await Task.Delay(_replyDelay, _cts.Token);
						}

						var segments = reply(request, index);
						for (int i = 0; i < segments.Length; i++)
						{
							if (i > 0) {
								await Task.Delay(SegmentGap, _cts.Token);
							}
							await stream.WriteAsync(segments[i], _cts.Token);
						}
					}
				}
				catch (IOException) { }
			}

			public async ValueTask DisposeAsync()
			{
				_cts.Cancel();
				_listener.Stop();
				await _loop;
				_cts.Dispose();
			}
		}
	}
}
