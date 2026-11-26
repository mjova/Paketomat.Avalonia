using Flatsoft.Common;
using Flatsoft.Common.UI;
using TopPos.Kiosk.ViewModels;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Unos koda prisustva pri preuzimanju na zahtev Agenta (v4 84548f0, u v2 8a42c1d): „Otkaži“ odlučuje pri kliku (bez 200 ms odziva), a
	/// kod ili „Otkaži“ posle roka su istek, ne provera ili odustajanje. Rok je ranije proveravao samo tajmer, jednom u sekundi.
	/// </summary>
	public class RecipientRemoteConfirmationViewModelTests
	{
		private sealed class ManualTime(DateTimeOffset now) : TimeProvider
		{
			public DateTimeOffset Now { get; set; } = now;

			public override DateTimeOffset GetUtcNow() => Now;
		}

		private static (RecipientRemoteConfirmationViewModel Vm, ManualTime Time, List<string> Codes, List<string> Events) Create()
		{
			var time = new ManualTime(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
			var vm = new RecipientRemoteConfirmationViewModel(new LanguageManager(null), null, time);
			var codes = new List<string>();
			var events = new List<string>();
			vm.HandleCodeConfirmed = code => {
				codes.Add(code);
				return ValueTask.FromResult(true);
			};
			vm.HandleCancelClicked = () => {
				events.Add("cancel");
				return ValueTask.CompletedTask;
			};
			vm.HandleExpired = () => {
				events.Add("expired");
				return ValueTask.CompletedTask;
			};
			vm.Prepare(TimeSpan.FromSeconds(120), 3);
			vm.Activate();
			return (vm, time, codes, events);
		}

		[Fact]
		public async Task Cancel_decides_at_the_click()
		{
			var (vm, _, _, events) = Create();

			var pending = ((IAsyncCommand) vm.CancelCommand).ExecuteAsync(null);

			Assert.Equal(["cancel"], events);	// pre odziva klika: tačan kod potvrđen posle klika više nema izlaz
			await pending;
			vm.Deactivate();
		}

		[Fact]
		public async Task Cancel_after_the_deadline_expires_instead_of_declining()
		{
			var (vm, time, _, events) = Create();
			time.Now += TimeSpan.FromSeconds(121);	// tajmer još nije video istek

			await ((IAsyncCommand) vm.CancelCommand).ExecuteAsync(null);

			Assert.Equal(["expired"], events);
			vm.Deactivate();
		}

		[Fact]
		public async Task Code_within_the_deadline_is_submitted()
		{
			var (vm, time, codes, events) = Create();
			time.Now += TimeSpan.FromSeconds(119);

			Assert.True(await vm.Keypad.HandleCodeConfirmed!("4729"));

			Assert.Equal(["4729"], codes);
			Assert.Empty(events);
			vm.Deactivate();
		}

		[Fact]
		public async Task Code_after_the_deadline_expires_instead_of_being_submitted()
		{
			var (vm, time, codes, events) = Create();
			time.Now += TimeSpan.FromSeconds(121);	// tajmer (jednom u sekundi) još nije video istek

			Assert.False(await vm.Keypad.HandleCodeConfirmed!("4729"));

			Assert.Empty(codes);
			Assert.Equal(["expired"], events);
			vm.Deactivate();
		}
	}
}