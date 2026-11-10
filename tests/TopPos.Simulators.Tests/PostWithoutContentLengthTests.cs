using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Flatsoft.Common;
using Microsoft.Extensions.Logging.Abstractions;
using TopPos.LockerSimulator;
using TopPos.ServerSimulator;
using Xunit;

namespace TopPos.Simulators.Tests
{
	/// <summary>
	/// POST bez Content-Length (npr. <c>curl -X POST</c> bez <c>-d</c>): managed HttpListener na Linuxu sam odgovara 411, a
	/// zahtev ipak predaje obradi — klijent dobija grešku, a stanje se promeni (provera na Debian VM-u 10.11.2026).
	/// Simulatori takav zahtev odbijaju na obe platforme isto: 411 i bez promene stanja.
	/// </summary>
	public sealed class PostWithoutContentLengthTests
	{
		[Fact]
		public async Task Locker_control_api_rejects_post_without_length_and_keeps_state()
		{
			await using var engine = new LockerEngine(new LockerSimulatorConfiguration {
				Locker = new LockerConfiguration { ModuleCount = 1, BoxCountPerModule = 24 },
				LockController = new LockControllerConfiguration {
					Type = "NCU24L",
					TcpController = new TcpControllerConfiguration { IpAddress = "127.0.0.1", Port = 0 },
					ListenAddress = "127.0.0.1",
					NetworkModule = new NetworkModuleConfiguration { LockAfterAbandonedMs = 0 },
				},
				ControlApi = new ControlApiConfiguration { Port = 0 },
			}, NullLogger.Instance);
			var port = engine.ControlApiPort!.Value;

			Assert.Equal(411, await PostWithoutContentLength(port, "/_sim/doors/5/open"));

			using var http = new HttpClient();
			var doors = await http.GetFromJsonAsync<JsonElement[]>($"http://localhost:{port}/_sim/doors") ?? [];
			Assert.True(doors.Single(d => d.GetProperty("number").GetInt32() == 5).GetProperty("isLocked").GetBoolean());
			Assert.Empty(await http.GetFromJsonAsync<JsonElement[]>($"http://localhost:{port}/_sim/unlocks") ?? []);
		}

		[Fact]
		public async Task Server_rejects_post_without_length_and_keeps_state()
		{
			var server = new HttpServer(new ServerSimulatorConfiguration {
				Server = new ServerConfiguration { Port = 0, BindAddress = "localhost", EnableControlApi = true },
				PaketomatId = "111",
			}, NullLogger.Instance);
			server.Start();
			try {
				using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{server.Port}/_sim/") };
				var added = await http.PostAsJsonAsync("packages", new { barkod = "PKT00001", brojPozicije = 5, kodZaOtvaranje = 1122331, telefonPrimaoca = "063111222" });
				Assert.True(added.IsSuccessStatusCode);

				Assert.Equal(411, await PostWithoutContentLength(server.Port, "/_sim/reset"));

				Assert.Single(await http.GetFromJsonAsync<JsonElement[]>("packages") ?? []);
			}
			finally {
				server.Stop();
			}
		}

		/// <summary>Sirov HTTP/1.1 POST bez tela i bez Content-Length; vraća status iz odgovora.</summary>
		private static async Task<int> PostWithoutContentLength(int port, string path)
		{
			// „localhost": na Linuxu HttpListener sluša samo na [::1], na Windows-u (HTTP.sys) na obe adrese
			using var tcp = new TcpClient();
			await tcp.ConnectAsync("localhost", port);
			var stream = tcp.GetStream();
			await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST {path} HTTP/1.1\r\nHost: localhost:{port}\r\nConnection: close\r\n\r\n"));
			using var reader = new StreamReader(stream, Encoding.ASCII);
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			var statusLine = await reader.ReadLineAsync(timeout.Token);
			Assert.NotNull(statusLine);
			// „HTTP/1.1 411 Length Required"
			return int.Parse(statusLine.Split(' ')[1]);
		}
	}
}
