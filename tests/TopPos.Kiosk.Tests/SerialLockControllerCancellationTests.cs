using System.Reflection;
using Flatsoft.Common;
using TopPos.LockController.Serial;
using TopPos.LockController.SerialDailyExpress;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Otkazano čekanje na semafor Serial kontrolera (npr. DoorWatcher.Pause) ne preuzima semafor, pa ga ne sme ni osloboditi.
	/// Ranije je finally uvek radio Release: kad je semafor bio slobodan → SemaphoreFullException umesto null, a kad ga je
	/// držala druga komanda → druga komanda je puštena na port dok prethodna još traje.
	/// Port ne postoji i ne otvara se — sa već otkazanim tokenom poziv pada na WaitAsync, pre bilo kakvog I/O.
	/// </summary>
	public class SerialLockControllerCancellationTests
	{
		private static ComPortConfiguration Config() => new() { PortName = "COM_TEST_NONE", BaudRate = 9600, DataBits = 8, Parity = "None" };

		private static object NewClient(string client) => client switch {
			"Serial" => new LockControllerClientSerial(Config()),
			"DailyExpress" => new LockControllerClientSerialDailyExpress(Config()),
			_ => throw new ArgumentOutOfRangeException(nameof(client))
		};

		private static async Task<object?> Invoke(object client, string operation, CancellationToken ct)
		{
			if (client is LockControllerClientSerial s) {
				return operation switch {
					"OpenDoorSingle" => await s.OpenDoorSingle(1, ct),
					"CheckDoorSingle" => await s.CheckDoorSingle(1, ct),
					"CheckDoorMultiple" => await s.CheckDoorMultiple(1, ct),
					"OpenDoorMultiple" => await s.OpenDoorMultiple(1, ct),
					"RestartModule" => await s.RestartModule(1, ct),
					"LedLightModule" => await s.LedLightModule(1, true, ct),
					_ => throw new ArgumentOutOfRangeException(nameof(operation))
				};
			}
			var d = (LockControllerClientSerialDailyExpress) client;
			return operation switch {
				"OpenDoorSingle" => await d.OpenDoorSingle(1, ct),
				"CheckDoorSingle" => await d.CheckDoorSingle(1, ct),
				"CheckDoorMultiple" => await d.CheckDoorMultiple(1, ct),
				_ => throw new ArgumentOutOfRangeException(nameof(operation))
			};
		}

		private static SemaphoreSlim CommandSemaphore(object client)
			=> (SemaphoreSlim) client.GetType().GetField("_semaphore", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;

		public static TheoryData<string, string> Operations => new() {
			{ "Serial", "OpenDoorSingle" },
			{ "Serial", "CheckDoorSingle" },
			{ "Serial", "CheckDoorMultiple" },
			{ "Serial", "OpenDoorMultiple" },
			{ "Serial", "RestartModule" },
			{ "Serial", "LedLightModule" },
			{ "DailyExpress", "OpenDoorSingle" },
			{ "DailyExpress", "CheckDoorSingle" },
			{ "DailyExpress", "CheckDoorMultiple" },
		};

		[Theory]
		[MemberData(nameof(Operations))]
		public async Task Cancelled_wait_on_free_semaphore_returns_null_and_keeps_semaphore_free(string client, string operation)
		{
			var c = NewClient(client);
			var result = await Invoke(c, operation, new CancellationToken(canceled: true));
			Assert.Null(result);
			Assert.Equal(1, CommandSemaphore(c).CurrentCount);
		}

		[Theory]
		[MemberData(nameof(Operations))]
		public async Task Cancelled_wait_while_another_command_holds_semaphore_does_not_release_it(string client, string operation)
		{
			var c = NewClient(client);
			var semaphore = CommandSemaphore(c);
			await semaphore.WaitAsync();    // druga komanda je u toku
			try {
				var result = await Invoke(c, operation, new CancellationToken(canceled: true));
				Assert.Null(result);
				Assert.Equal(0, semaphore.CurrentCount);    // i dalje zauzet — sledeća komanda mora da čeka
			}
			finally {
				semaphore.Release();
			}
		}

		[Theory]
		[MemberData(nameof(Operations))]
		public async Task Cancellation_while_waiting_for_semaphore_does_not_release_it(string client, string operation)
		{
			// Realan slučaj: poziv već čeka na semafor koji drži druga komanda, pa se token otkaže (npr. DoorWatcher.Pause).
			var c = NewClient(client);
			var semaphore = CommandSemaphore(c);
			await semaphore.WaitAsync();    // druga komanda je u toku
			try {
				using var cts = new CancellationTokenSource();
				var call = Invoke(c, operation, cts.Token);
				Assert.False(call.IsCompleted);     // čeka na semafor
				cts.Cancel();
				Assert.Null(await call);
				Assert.Equal(0, semaphore.CurrentCount);    // i dalje zauzet
			}
			finally {
				semaphore.Release();
			}
		}
	}
}
