using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Flatsoft.Common;
using Microsoft.Extensions.Logging.Abstractions;
using TopPos.LockerSimulator;
using Xunit;

namespace TopPos.Simulators.Tests
{
	/// <summary>
	/// <c>POST /_sim/reset</c> skida ZLG blokadu (veza zatvorena dok je odgovor čekao): sledeći E2E scenario ne sme da čeka
	/// <see cref="NetworkModuleConfiguration.LockAfterAbandonedMs"/> (Codex).
	/// </summary>
	public sealed class LockerResetTests
	{
		private static readonly byte[] GetState = [0x02, 0x00, 0x00, 0x80, 0x00, 0x00, 0x03, 0x85];

		[Fact]
		public async Task Reset_lifts_block_after_abandoned_response()
		{
			await using var engine = new LockerEngine(new LockerSimulatorConfiguration {
				Locker = new LockerConfiguration { ModuleCount = 1, BoxCountPerModule = 24 },
				LockController = new LockControllerConfiguration {
					Type = "NCU24L",
					TcpController = new TcpControllerConfiguration { IpAddress = "127.0.0.1", Port = 0 },
					ListenAddress = "127.0.0.1",
					NetworkModule = new NetworkModuleConfiguration { ReleaseDelayMs = 100, ResponseDelayMs = 300, LockAfterAbandonedMs = 60000 },
				},
				ControlApi = new ControlApiConfiguration { Port = 0 },
			}, NullLogger.Instance);
			using (var abandoned = new TcpClient()) {
				await abandoned.ConnectAsync(IPAddress.Loopback, engine.TcpPort!.Value);
				abandoned.GetStream().Write(GetState);
			}
			await Task.Delay(800);	// odgovor (300 ms) nije imao kome da ode → blokada 60 s
			Assert.False(await TryConnect(engine.TcpPort!.Value, TimeSpan.FromMilliseconds(500)), "modul nije blokiran");

			using var control = new HttpClient();
			var response = await control.PostAsync($"http://localhost:{engine.ControlApiPort}/_sim/reset", new StringContent(""));
			Assert.True(response.IsSuccessStatusCode);

			Assert.True(await TryConnect(engine.TcpPort!.Value, TimeSpan.FromSeconds(5)), "posle reset-a modul i dalje odbija vezu");
		}

		/// <summary>Povezivanje uz ponavljanje na „refused" (Linux odbija odmah, Windows posle ~2 s).</summary>
		private static async Task<bool> TryConnect(int port, TimeSpan limit)
		{
			var sw = Stopwatch.StartNew();
			do {
				using var client = new TcpClient();
				using var timeout = new CancellationTokenSource(limit);
				try {
					await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
					return true;
				}
				catch (Exception ex) when (ex is SocketException or OperationCanceledException) {
					await Task.Delay(50);
				}
			} while (sw.Elapsed < limit);
			return false;
		}
	}
}
