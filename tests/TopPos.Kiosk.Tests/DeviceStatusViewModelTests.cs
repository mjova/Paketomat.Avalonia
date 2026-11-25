using Flatsoft.Common;
using TopPos.Kiosk.ViewModels;
using Xunit;
using static TopPos.Kiosk.PosClient;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Status uređaja (dug pritisak na Home; v4 sloj umesto v2 DeviceStatusWindow): ishod provera, zatvaranje usred provere i
	/// verzija. Da je kiosk zauzet dok se otkazane provere ne završe pokriva
	/// <see cref="KioskOperationCoordinatorTests.Standalone_operation_stays_busy_after_End_until_its_work_finishes"/>; ovde se
	/// proverava da <see cref="DeviceStatusViewModel.Initialize"/> (posao koji koordinator prati) zaista čeka provere.
	/// </summary>
	public class DeviceStatusViewModelTests
	{
		private static readonly TimeSpan __wait = TimeSpan.FromSeconds(5);

		private static DeviceStatusViewModel Create(IPosClient? pos, ApplicationContext? context = null)
			=> new(barcodeScannerClient: null, pos, context, new LanguageManager(null), logger: null) { CheckDelay = TimeSpan.Zero };

		[Theory]
		[InlineData(true, "DeviceStatus.Connected")]
		[InlineData(false, "DeviceStatus.Disconnected")]
		public async Task Checks_show_pos_presence_and_missing_scanner_as_disconnected(bool posPresent, string expectedPosKey)
		{
			var vm = Create(new FakePos { Presence = _ => Task.FromResult(posPresent) });

			await vm.Initialize().WaitAsync(__wait);

			Assert.Equal(vm[expectedPosKey], vm.PosTerminalStatus);
			Assert.Equal(vm["DeviceStatus.Disconnected"], vm.BarcodeScannerStatus);
			Assert.False(vm.IsPosTerminalChecking);
			Assert.False(vm.IsBarcodeScannerChecking);
			Assert.False(vm.Closed.IsCompleted);
		}

		[Fact]
		public async Task Check_without_answer_ends_after_timeout_as_disconnected()
		{
			// npr. pogrešan port koji šalje druge podatke: provera ne sme da se vrti dok tehničar ne odustane
			var vm = new DeviceStatusViewModel(null, new FakePos { Presence = async ct => { await Task.Delay(Timeout.Infinite, ct); return true; } },
												null, new LanguageManager(null), null) { CheckDelay = TimeSpan.Zero, CheckTimeout = TimeSpan.FromMilliseconds(100) };

			await vm.Initialize().WaitAsync(__wait);

			Assert.Equal(vm["DeviceStatus.Disconnected"], vm.PosTerminalStatus);
			Assert.False(vm.IsPosTerminalChecking);
			Assert.False(vm.Closed.IsCompleted);
		}

		[Fact]
		public async Task Check_ignoring_cancellation_shows_disconnected_after_timeout_but_holds_until_it_returns()
		{
			// npr. ECR koji ponavlja zahtev na svaki pogrešan ACK: tehničar dobija ishod, a kiosk ostaje zauzet dok port radi
			var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var vm = new DeviceStatusViewModel(null, new FakePos { Presence = _ => release.Task }, null, new LanguageManager(null), null) {
				CheckDelay = TimeSpan.Zero, CheckTimeout = TimeSpan.FromMilliseconds(100)
			};

			var checks = vm.Initialize();

			await WaitUntilAsync(() => vm.PosTerminalStatus != null);
			Assert.Equal(vm["DeviceStatus.Disconnected"], vm.PosTerminalStatus);
			Assert.False(vm.IsPosTerminalChecking);
			await Task.Delay(50);
			Assert.False(checks.IsCompleted);

			release.SetResult(true);                      // zakasneli uspeh se ne prikazuje
			await checks.WaitAsync(__wait);
			Assert.Equal(vm["DeviceStatus.Disconnected"], vm.PosTerminalStatus);
		}

		private static async Task WaitUntilAsync(Func<bool> condition)
		{
			var deadline = DateTime.UtcNow + __wait;
			while (!condition()) {
				Assert.True(DateTime.UtcNow < deadline, "uslov nije ispunjen na vreme");
				await Task.Delay(10);
			}
		}

		[Fact]
		public async Task Failed_check_is_shown_as_disconnected()
		{
			var vm = Create(new FakePos { Presence = _ => throw new IOException("port zatvoren") });

			await vm.Initialize().WaitAsync(__wait);

			Assert.Equal(vm["DeviceStatus.Disconnected"], vm.PosTerminalStatus);
			Assert.False(vm.IsPosTerminalChecking);
		}

		[Fact]
		public async Task Close_during_check_cancels_it_and_checks_finish_only_when_pos_returns()
		{
			// POS ne reaguje odmah na otkazivanje (kao ECR koji čeka odgovor): posao provera traje dok se on ne vrati, pa ga
			// koordinator i dalje drži zauzetim.
			var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			CancellationToken seen = default;
			var vm = Create(new FakePos { Presence = ct => { seen = ct; started.SetResult(); return release.Task; } });

			var checks = vm.Initialize();
			await started.Task.WaitAsync(__wait);
			Assert.True(vm.IsPosTerminalChecking);

			vm.CloseCommand.Execute(null);

			await vm.Closed.WaitAsync(__wait);
			Assert.True(seen.IsCancellationRequested);
			await Task.Delay(50);
			Assert.False(checks.IsCompleted);

			release.SetResult(true);
			await checks.WaitAsync(__wait);
			Assert.Null(vm.PosTerminalStatus);          // otkazana provera ne prikazuje ishod
			Assert.False(vm.IsPosTerminalChecking);
		}

		[Fact]
		public async Task Close_before_checks_start_ends_them_without_status()
		{
			var vm = new DeviceStatusViewModel(null, new FakePos(), null, new LanguageManager(null), null);   // pauza 2 s kao v2
			var checks = vm.Initialize();

			vm.Close();
			vm.Close();                                  // idempotentno

			await checks.WaitAsync(__wait);
			Assert.True(vm.Closed.IsCompleted);
			Assert.Null(vm.PosTerminalStatus);
			Assert.Null(vm.BarcodeScannerStatus);
			Assert.False(vm.IsPosTerminalChecking);
			Assert.False(vm.IsBarcodeScannerChecking);
		}

		[Theory]
		[InlineData(null, "N/A")]
		[InlineData("1.3.4", "1.3.4")]
		[InlineData("1.3.4-proba-sa-uredjajima", "1.3.4-proba-sa-")]
		public async Task Version_label_is_shown_like_v2(string? versionLabel, string expected)
		{
			var vm = Create(new FakePos(), new ApplicationContext { VersionLabel = versionLabel, GitHash = "abc123" });

			await vm.Initialize().WaitAsync(__wait);

			Assert.Equal(expected, vm.AppVersion);
			Assert.Equal("abc123", vm.BuildId);
			Assert.NotNull(vm.CurrentTime);
		}

		private sealed class FakePos : IPosClient
		{
			public Func<CancellationToken, Task<bool>> Presence { get; init; } = _ => Task.FromResult(true);

			public bool IsConnected => true;

			public Task<bool> CheckPresence(CancellationToken cancellationToken) => Presence(cancellationToken);

			public ValueTask<(ResponseCodes, TransactionFlagCodes, string?, string?)> Sale(decimal amount, Func<string, ValueTask>? posTerminalMessagesHandler,
																							CancellationToken cancellationToken)
				=> throw new NotSupportedException();
		}
	}
}