using System.IO;
using Flatsoft.Common;
using TopPos.Agent.Contracts;
using TopPos.Kiosk;
using TopPos.Kiosk.AgentCommunication;
using TopPos.Kiosk.ViewModels;
using TopPos.Kiosk.Workflow;
using TopPos.LockController;
using Xunit;
using FakeController = TopPos.Kiosk.Tests.RemoteServiceSessionTests.FakeController;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// <see cref="RemoteServiceViewModel"/> bez WPF prikaza: crtež rasporeda, prijava sa tastature i skenera, otvaranje sa
	/// ekrana i kraj u dva dodira. Sesija je prava, kontroler lažan.
	/// </summary>
	public class RemoteServiceViewModelTests : IDisposable
	{
		private const string __code = "123456";
		private static readonly TimeSpan __wait = TimeSpan.FromSeconds(5);

		private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("toppos-remote-vm-");
		private readonly FakeController _controller = new(8);
		private readonly BarcodeScannerClient _scanner = new(new BarcodeScannerConfiguration {
																 ComPort = new ComPortConfiguration { PortName = "COM250", BaudRate = 9600, DataBits = 8, Parity = "None" },
															 }, logger: null, CancellationToken.None);
		private readonly LanguageManager _languages = new(null);

		public RemoteServiceViewModelTests()
		{
			_languages.LoadLanguageResources(Path.Combine(AppContext.BaseDirectory, "Languages"), ".lang", "sr");
		}

		public void Dispose()
		{
			for (int attempt = 1; ; attempt++) {
				try {
					_dir.Delete(recursive: true);
					return;
				}
				catch (IOException) when (attempt < 50) {
					Thread.Sleep(50);
				}
			}
		}

		private static LayoutBox Box(int door, string size, int column, int row) => new() { DoorNumber = door, Size = size, Column = column, Row = row };

		private async Task<(RemoteServiceViewModel Vm, RemoteServiceSession Session)> Start(IReadOnlyList<LayoutBox>? layout = null)
		{
			var journal = new AgentOperationJournal(_dir.FullName, logger: null);
			journal.Initialize(RemoteServiceManager.Recoverers);
			var session = new RemoteServiceSession(new RemoteServiceSessionOptions {
															SessionId = "s1",
															PresenceCode = __code,
															IdleTimeout = TimeSpan.FromSeconds(600),
															LockerBoxCount = 16,
															Layout = layout ?? [Box(1, BoxSizes.L, 1, 1), Box(2, BoxSizes.S, 1, 2), Box(9, BoxSizes.M, 2, 1)],
															UnavailableModulesAtStart = [2],
														}, _controller, journal, new KioskStatusSource("112", logger: null), logger: null);
			await session.StartAsync();
			var vm = new RemoteServiceViewModel(_scanner, _languages, "112", logger: null);
			vm.Bind(session);
			vm.Activate();
			return (vm, session);
		}

		private static async Task WaitUntilAsync(Func<bool> condition)
		{
			var deadline = DateTime.UtcNow + __wait;
			while (!condition()) {
				Assert.True(DateTime.UtcNow < deadline, "uslov nije ispunjen u roku");
				await Task.Delay(20);
			}
		}

		[Fact]
		public async Task Layout_is_drawn_like_Technician_by_column_row_and_size()
		{
			// Vertikala 2 nema vrata ovog kiosk-a (nije u rasporedu), pa se vertikale ređaju bez rupe.
			var (vm, _) = await Start([Box(3, BoxSizes.M, 3, 2), Box(1, BoxSizes.L, 1, 1), Box(2, BoxSizes.S, 1, 2), Box(4, BoxSizes.S, 3, 1)]);

			Assert.Equal([1, 3], vm.Columns.Select(c => c.Column));
			Assert.Equal([0, RemoteServiceBoxViewModel.ColumnWidth], vm.Columns.Select(c => c.X));
			Assert.Equal([3, 4], vm.Columns[1].Doors.Order());
			Assert.Equal([4, 3], vm.Columns[1].Doors);       // odozgo, po row
			var byDoor = vm.Boxes.ToDictionary(b => b.DoorNumber);
			Assert.Equal(RemoteServiceColumnViewModel.HeaderHeight, byDoor[1].Y);
			Assert.Equal(byDoor[1].Y + byDoor[1].Height, byDoor[2].Y);
			Assert.True(byDoor[1].Height > byDoor[3].Height && byDoor[3].Height > byDoor[2].Height);   // L > M > S
			Assert.Equal(2 * RemoteServiceBoxViewModel.ColumnWidth, vm.CanvasWidth);
			Assert.Equal("RemoteServicePage.Door.1", byDoor[1].AutomationId);
		}

		[Fact]
		public async Task Wrong_code_shows_remaining_attempts_and_correct_code_shows_the_layout()
		{
			var (vm, _) = await Start();
			Assert.True(vm.IsAwaitingTechnician);
			Assert.False(vm.IsActive);
			Assert.Contains("2", vm.ModuleErrorText);

			Assert.False(await vm.Keypad.HandleCodeConfirmed!("111111"));
			Assert.Contains("2", vm.NoticeText);
			Assert.True(vm.IsAwaitingTechnician);

			Assert.True(await vm.Keypad.HandleCodeConfirmed!(__code));
			Assert.False(vm.IsAwaitingTechnician);
			Assert.True(vm.IsActive);
			Assert.True(vm.CanOpen);
			Assert.Null(vm.NoticeText);
			Assert.Equal("Provera brava na paketomatu 112", vm.ActiveTitle);
		}

		[Fact]
		public async Task Scanned_QR_logs_in_and_is_ignored_after_login()
		{
			var (vm, session) = await Start();

			Assert.True(await _scanner.ProcessBarcode!(__code));
			Assert.Equal(RemoteServicePhases.Active, session.Phase);
			Assert.False(await _scanner.ProcessBarcode!(__code));

			vm.Deactivate();
			Assert.Null(_scanner.ProcessBarcode);
		}

		[Fact]
		public async Task Tapping_a_box_opens_the_door_and_shows_the_outcome()
		{
			_controller.Attempts[2] = new DoorOpenAttempt(true, DoorStatusCode.Closed);
			var (vm, session) = await Start();
			await vm.Keypad.HandleCodeConfirmed!(__code);
			var byDoor = vm.Boxes.ToDictionary(b => b.DoorNumber);

			byDoor[1].TapCommand.Execute(null);
			await WaitUntilAsync(() => byDoor[1].State == RemoteServiceBoxState.Open);
			vm.Columns[0].OpenCommand.Execute(null);
			await WaitUntilAsync(() => byDoor[2].State == RemoteServiceBoxState.DidNotOpen && vm.CanOpen);

			Assert.Equal([1, 1, 2], _controller.Opened);
			Assert.Equal("otvoreno", byDoor[1].StatusText);
			Assert.Equal(RemoteServiceBoxState.NotOpened, byDoor[9].State);
			Assert.All(session.GetResult().Openings, o => Assert.Equal(DoorOpenSources.Screen, o.Source));
		}

		[Fact]
		public async Task End_needs_two_taps_and_first_tap_lists_doors_that_may_be_open()
		{
			var (vm, session) = await Start();
			await vm.Keypad.HandleCodeConfirmed!(__code);
			vm.Boxes.First(b => b.DoorNumber == 9).TapCommand.Execute(null);
			await WaitUntilAsync(() => vm.CanOpen && session.LastOutcomeByDoor.ContainsKey(9));

			vm.EndCommand.Execute(null);
			await WaitUntilAsync(() => vm.EndButtonText == "Potvrdi kraj");
			Assert.Contains("9", vm.NoticeText);
			Assert.False(session.IsEnding);

			vm.EndCommand.Execute(null);
			// Poziv na potvrdu ne ostaje na ekranu dok se čita stanje vrata (E2E Serial proverava i trenutak).
			await WaitUntilAsync(() => session.Phase == RemoteServicePhases.Ended);
			Assert.Null(vm.NoticeText);
			Assert.Equal(RemoteServiceEndModes.ScreenConfirmed, session.GetResult().EndMode);
		}

		[Fact]
		public async Task End_from_portal_removes_a_pending_end_confirmation()
		{
			var (vm, session) = await Start();
			await vm.Keypad.HandleCodeConfirmed!(__code);
			vm.EndCommand.Execute(null);
			await WaitUntilAsync(() => vm.EndButtonText == "Potvrdi kraj");

			await session.EndFromPortalAsync();

			Assert.Null(vm.NoticeText);
			Assert.Equal("Zatvorio sam sva vrata", vm.EndButtonText);
			Assert.False(vm.CanOpen);
		}

		[Fact]
		public async Task Opening_resets_a_pending_end_confirmation()
		{
			var (vm, session) = await Start();
			await vm.Keypad.HandleCodeConfirmed!(__code);

			vm.EndCommand.Execute(null);
			await WaitUntilAsync(() => vm.EndButtonText == "Potvrdi kraj");
			vm.OpenAllCommand.Execute(null);
			await WaitUntilAsync(() => vm.CanOpen && _controller.Opened.Count == 16);
			Assert.Equal("Zatvorio sam sva vrata", vm.EndButtonText);
			vm.EndCommand.Execute(null);
			await WaitUntilAsync(() => vm.EndButtonText == "Potvrdi kraj");

			Assert.Equal(RemoteServicePhases.Active, session.Phase);
		}

		/// <summary>v4: otvaranje iz portala posle prvog dodira na kraj poništava potvrdu — prikazani spisak vrata više ne važi.</summary>
		[Fact]
		public async Task Opening_from_portal_resets_a_pending_end_confirmation()
		{
			var (vm, session) = await Start();
			await vm.Keypad.HandleCodeConfirmed!(__code);

			vm.EndCommand.Execute(null);
			await WaitUntilAsync(() => vm.EndButtonText == "Potvrdi kraj");
			await session.StartOpenDoorsAsync([9], all: false, DoorOpenSources.Portal);
			await WaitUntilAsync(() => vm.CanOpen && _controller.Opened.Contains(9));

			Assert.Equal("Zatvorio sam sva vrata", vm.EndButtonText);
			Assert.Equal(RemoteServicePhases.Active, session.Phase);
		}
	}
}
