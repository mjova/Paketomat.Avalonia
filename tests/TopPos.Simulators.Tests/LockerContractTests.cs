using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flatsoft.Common;
using Microsoft.Extensions.Logging.Abstractions;
using TopPos.LockController;
using TopPos.LockController.NCU24L;
using TopPos.LockerSimulator;
using Xunit;

namespace TopPos.Simulators.Tests
{
	/// <summary>
	/// Simulator paketomata u procesu + pravi NCU24L klijent kioska (<see cref="LockControllerClientNCU24L"/>): mapiranje
	/// vrata na modul i bravu, blokade, isključen modul, kvar DropOpenResponse i kontrolni API (<c>/_sim/*</c>).
	/// ZLG ponašanje je isključeno (brži testovi); njega pokrivaju <see cref="TcpLockControllerTests"/>.
	/// </summary>
	public sealed class LockerContractTests : IAsyncLifetime
	{
		private LockerEngine _engine = default!;
		private HttpClient _control = default!;
		private LockControllerClientNCU24L _client = default!;

		public Task InitializeAsync()
		{
			_engine = new LockerEngine(new LockerSimulatorConfiguration {
				Locker = new LockerConfiguration { ModuleCount = 2, BoxCountPerModule = 24 },
				LockController = new LockControllerConfiguration {
					Type = "NCU24L",
					TcpController = new TcpControllerConfiguration { IpAddress = "127.0.0.1", Port = 0 },
					ListenAddress = "127.0.0.1",
					NetworkModule = new NetworkModuleConfiguration { Enabled = false },
				},
				ControlApi = new ControlApiConfiguration { Port = 0 },
			}, NullLogger.Instance);
			_control = new HttpClient { BaseAddress = new Uri($"http://localhost:{_engine.ControlApiPort}/_sim/") };
			_client = new LockControllerClientNCU24L(new TcpControllerConfiguration { IpAddress = "127.0.0.1", Port = _engine.TcpPort!.Value });
			return Task.CompletedTask;
		}

		public async Task DisposeAsync()
		{
			_client.Dispose();
			_control.Dispose();
			await _engine.DisposeAsync();
		}

		private async Task Post(string path, object? body = null)
		{
			using HttpContent content = body == null ? new StringContent("") : JsonContent.Create(body);
			var response = await _control.PostAsync(path, content);
			Assert.True(response.IsSuccessStatusCode, $"POST {path} → {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
		}

		private async Task<JsonElement> Door(int number)
			=> (await _control.GetFromJsonAsync<JsonElement[]>("doors") ?? []).Single(d => d.GetProperty("number").GetInt32() == number);

		private async Task<JsonElement[]> Unlocks(long since = 0)
			=> await _control.GetFromJsonAsync<JsonElement[]>($"unlocks?since={since}") ?? [];

		[Theory]
		[InlineData(5, 1)]
		[InlineData(30, 2)]
		public async Task Opened_door_is_unlocked_in_its_module_and_recorded(int door, int module)
		{
			var attempt = await _client.OpenDoorOnce(door, null, CancellationToken.None);

			Assert.Equal(new DoorOpenAttempt(true, DoorStatusCode.Open), attempt);
			var state = await Door(door);
			Assert.False(state.GetProperty("isLocked").GetBoolean());
			Assert.Equal(module, state.GetProperty("module").GetInt32());
			var unlock = Assert.Single(await Unlocks());
			Assert.Equal(door, unlock.GetProperty("door").GetInt32());
			Assert.True(unlock.GetProperty("unlocked").GetBoolean());

			var statuses = await _client.CheckDoorMultiple(module, CancellationToken.None);
			Assert.NotNull(statuses);
			var index = (door - 1) % 24;
			Assert.Equal(DoorStatusCode.Open, statuses[index]);
			Assert.All(statuses.Where((_, i) => i != index), s => Assert.Equal(DoorStatusCode.Closed, s));
		}

		[Fact]
		public async Task Blocked_door_stays_locked_but_attempt_is_recorded()
		{
			await Post("doors/7/block");

			var attempt = await _client.OpenDoorOnce(7, null, CancellationToken.None);

			Assert.Equal(DoorStatusCode.Closed, attempt.Status);
			Assert.False(Assert.Single(await Unlocks()).GetProperty("unlocked").GetBoolean());
			Assert.True((await Door(7)).GetProperty("isLocked").GetBoolean());
		}

		[Fact]
		public async Task Switched_off_module_does_not_answer()
		{
			await Post("modules/2/off");

			Assert.Null(await _client.CheckDoorMultiple(2, TimeSpan.FromMilliseconds(300), CancellationToken.None));
			Assert.NotNull(await _client.CheckDoorMultiple(1, CancellationToken.None));
		}

		[Fact]
		public async Task Dropped_open_response_still_opens_door()
		{
			await Post("faults", new { fault = "DropOpenResponse", count = 1 });

			var attempt = await _client.OpenDoorOnce(3, null, CancellationToken.None);

			// NCU24L klijent stanje čita iz GetState, pa vidi otvorena vrata i bez potvrde Unlock-a
			Assert.Equal(DoorStatusCode.Open, attempt.Status);
			Assert.Empty(await _control.GetFromJsonAsync<Dictionary<string, int>>("faults") ?? []);
		}

		[Fact]
		public async Task Door_closed_by_control_api_is_reported_closed()
		{
			await _client.OpenDoorOnce(10, null, CancellationToken.None);
			await Post("doors/10/close");

			Assert.Equal(DoorStatusCode.Closed, await _client.CheckDoorSingle(10, CancellationToken.None));
		}

		[Fact]
		public async Task Reset_restores_closed_unblocked_doors_and_powered_modules()
		{
			await _client.OpenDoorOnce(1, null, CancellationToken.None);
			var seq = Assert.Single(await Unlocks()).GetProperty("seq").GetInt64();
			await Post("doors/2/block");
			await Post("modules/2/off");
			await Post("faults", new { fault = "DropOpenResponse", count = 3 });

			await Post("reset");

			var doors = await _control.GetFromJsonAsync<JsonElement[]>("doors") ?? [];
			Assert.Equal(48, doors.Length);
			Assert.All(doors, d => {
				Assert.True(d.GetProperty("isLocked").GetBoolean());
				Assert.False(d.GetProperty("isBlocked").GetBoolean());
				Assert.True(d.GetProperty("moduleOn").GetBoolean());
			});
			Assert.Empty(await Unlocks());
			Assert.Empty(await _control.GetFromJsonAsync<Dictionary<string, int>>("faults") ?? []);

			await _client.OpenDoorOnce(1, null, CancellationToken.None);
			Assert.True(Assert.Single(await Unlocks(seq)).GetProperty("seq").GetInt64() > seq);
		}

		[Fact]
		public async Task Health_reports_actual_ports_and_pos_is_not_simulated_yet()
		{
			var health = await _control.GetFromJsonAsync<JsonElement>("health");
			Assert.Equal(_engine.TcpPort, health.GetProperty("tcpPort").GetInt32());
			Assert.Equal(_engine.ControlApiPort, health.GetProperty("controlApiPort").GetInt32());

			Assert.Equal(HttpStatusCode.NotFound, (await _control.GetAsync("pos")).StatusCode);
		}
	}
}
