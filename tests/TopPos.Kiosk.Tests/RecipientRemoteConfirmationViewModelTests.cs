using Flatsoft.Common;
using TopPos.Kiosk.ViewModels;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// v4: unos koda prisustva pri preuzimanju na zahtev Agenta — „Otkaži“ odlučuje odmah (bez 200 ms odziva klika) i kod potvrđen
	/// posle roka pokreće istek, a ne proveru (rok je ranije proveravao samo tajmer, jednom u sekundi).
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
			var time = new ManualTime(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
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

		private static void Type(RecipientRemoteConfirmationViewModel vm, string code)
		{
			foreach (var key in code.Select(c => c.ToString()).Append("confirm")) {
				vm.Keypad.KeyInputCommand.Execute(key);
			}
		}

		[Fact]
		public void Cancel_decides_at_the_click()
		{
			var (vm, _, _, events) = Create();

			var pending = ((Flatsoft.Common.UI.IAsyncCommand) vm.CancelCommand).ExecuteAsync(null);

			Assert.Equal(["cancel"], events);	// pre 200 ms odziva: potvrda koda posle klika više nema izlaz
			pending.GetAwaiter().GetResult();
			vm.Deactivate();
		}

		[Fact]
		public void Cancel_after_the_deadline_expires_instead_of_declining()
		{
			var (vm, time, _, events) = Create();
			time.Now += TimeSpan.FromSeconds(121);	// tajmer još nije video istek

			((Flatsoft.Common.UI.IAsyncCommand) vm.CancelCommand).ExecuteAsync(null).GetAwaiter().GetResult();

			Assert.Equal(["expired"], events);
			vm.Deactivate();
		}

		[Fact]
		public void Code_within_the_deadline_is_submitted()
		{
			var (vm, time, codes, events) = Create();
			time.Now += TimeSpan.FromSeconds(119);

			Type(vm, "4729");

			Assert.Equal(["4729"], codes);
			Assert.Empty(events);
			vm.Deactivate();
		}

		[Fact]
		public void Code_after_the_deadline_expires_instead_of_being_submitted()
		{
			var (vm, time, codes, events) = Create();
			time.Now += TimeSpan.FromSeconds(121);	// tajmer (jednom u sekundi) još nije video istek

			Type(vm, "4729");

			Assert.Empty(codes);
			Assert.Equal(["expired"], events);
			vm.Deactivate();
		}
	}
}
