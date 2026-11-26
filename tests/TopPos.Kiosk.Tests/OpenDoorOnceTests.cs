using System.Net;
using System.Net.Sockets;
using Flatsoft.Common;
using TopPos.LockController;
using TopPos.LockController.NCU16L;
using TopPos.LockController.NCU24L;
using TopPos.LockController.Serial;
using TopPos.LockController.SerialDailyExpress;
using Xunit;
using static TopPos.Kiosk.Tests.NcuStaleResponseTests;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// <see cref="ILockControllerClient.OpenDoorOnce"/>: komanda Agenta se ne sme poslati dvaput, a rezultat mora da kaže
	/// da li je komanda poslata — od toga zavisi ishod (<c>ModuleUnavailable</c> ili <c>OutcomeUnknown</c>). Da Serial posle
	/// izgubljenog odgovora ne šalje ponovo, proverava E2E sa simulatorom (pravi port); ovde se proverava „nije poslato".
	/// </summary>
	public class OpenDoorOnceTests
	{
		private static ComPortConfiguration MissingPort() => new() { PortName = "COM250", BaudRate = 9600, DataBits = 8, Parity = "None", RetryCount = 3 };

		public static TheoryData<string> SerialClients => new() { "Serial", "DailyExpress" };

		private static ILockControllerClient Serial(string client) => client switch {
			"Serial" => new LockControllerClientSerial(MissingPort()),
			"DailyExpress" => new LockControllerClientSerialDailyExpress(MissingPort()),
			_ => throw new ArgumentOutOfRangeException(nameof(client))
		};

		[Theory]
		[MemberData(nameof(SerialClients))]
		public async Task Serial_port_that_cannot_open_reports_not_sent(string client)
		{
			var attempt = await Serial(client).OpenDoorOnce(1, null, CancellationToken.None);

			Assert.Equal(DoorOpenAttempt.NotSent, attempt);
		}

		[Theory]
		[MemberData(nameof(SerialClients))]
		public async Task Serial_cancelled_before_send_reports_not_sent(string client)
		{
			using var cts = new CancellationTokenSource();
			cts.Cancel();

			var attempt = await Serial(client).OpenDoorOnce(1, null, cts.Token);

			Assert.Equal(DoorOpenAttempt.NotSent, attempt);
		}

		[Fact]
		public async Task Ncu24l_open_door_is_sent_once_and_confirmed()
		{
			await using var server = new FakeNcuServer(requestLength: 8, (request, index) => index switch {
				0 => [Ncu24lUnlock(0x00, request[2])],
				_ => [Ncu24lState(0x00, 0xFB, 0xFF, 0xFF)]		// vrata 3 otvorena
			});
			using var client = new LockControllerClientNCU24L(server.Configuration);

			var attempt = await client.OpenDoorOnce(3, null, CancellationToken.None);

			Assert.True(attempt.Sent);
			Assert.True(attempt.Status?.IsOpen);
			Assert.Equal(2, server.RequestCount);	// Unlock + GetState
		}

		[Fact]
		public async Task Ncu24l_state_lost_after_unlock_reports_sent_with_unknown_state()
		{
			await using var server = new FakeNcuServer(requestLength: 8, (request, index) => index switch {
				0 => [Ncu24lUnlock(0x00, request[2])],
				_ => []											// GetState bez odgovora
			});
			using var client = new LockControllerClientNCU24L(server.Configuration);

			var attempt = await client.OpenDoorOnce(3, null, CancellationToken.None);

			Assert.Equal(new DoorOpenAttempt(true, null), attempt);
			Assert.Equal(2, server.RequestCount);
		}

		[Fact]
		public async Task Ncu16l_state_lost_after_unlock_reports_sent_with_unknown_state()
		{
			await using var server = new FakeNcuServer(requestLength: 5, (request, index) => []);	// Unlock nema odgovor; GetState ne stiže
			using var client = new LockControllerClientNCU16L(server.Configuration);

			var attempt = await client.OpenDoorOnce(2, null, CancellationToken.None);

			Assert.Equal(new DoorOpenAttempt(true, null), attempt);
			Assert.Equal(2, server.RequestCount);
		}

		[Fact]
		public async Task Ncu16l_open_door_reports_closed_lock_as_sent_and_closed()
		{
			await using var server = new FakeNcuServer(requestLength: 5, (request, index) => index switch {
				0 => [],
				_ => [Ncu16lFrame(0x00, 0x35, 0x02, 0x00)]		// vrata 2 i dalje zatvorena
			});
			using var client = new LockControllerClientNCU16L(server.Configuration);

			var attempt = await client.OpenDoorOnce(2, null, CancellationToken.None);

			Assert.True(attempt.Sent);
			Assert.True(attempt.Status?.IsClosed);
		}

		[Fact]
		public async Task Ncu_without_controller_reports_not_sent()
		{
			// slobodan port na kome niko ne sluša: veza se odbija
			// v4: port ostaje zauzet (bind bez listen) dok test traje — oslobođen port je mogao da dobije FakeNcuServer
			// paralelnog testa, pa je veza uspevala (Sent = true).
			using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
			int port = ((IPEndPoint)reserved.LocalEndPoint!).Port;
			using var client = new LockControllerClientNCU16L(new TcpControllerConfiguration { IpAddress = "127.0.0.1", Port = port });

			var attempt = await client.OpenDoorOnce(2, null, CancellationToken.None);

			Assert.Equal(DoorOpenAttempt.NotSent, attempt);
		}

		[Fact]
		public async Task Ncu_door_number_out_of_range_is_not_sent()
		{
			await using var server = new FakeNcuServer(requestLength: 8, (request, index) => []);
			using var client = new LockControllerClientNCU24L(server.Configuration);

			var attempt = await client.OpenDoorOnce(0, null, CancellationToken.None);

			Assert.Equal(DoorOpenAttempt.NotSent, attempt);
			Assert.Equal(0, server.RequestCount);
		}
	}
}
