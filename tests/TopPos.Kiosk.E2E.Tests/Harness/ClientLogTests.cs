using System.Text;
using TopPos.Kiosk.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;

namespace TopPos.E2E.Tests.Harness
{
	/// <summary>
	/// Samoprovera čitanja loga (ne traži E2E okruženje): klijent piše istovremeno, pa čitanje može zateći nedovršen
	/// red ili UTF-8 znak prepolovljen između dva upisa — ništa od toga ne sme da se izgubi ni pokvari.
	/// </summary>
	public sealed class ClientLogTests : IDisposable
	{
		private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "toppos-e2e-selftest", Guid.NewGuid().ToString("N"));
		private readonly string _file;

		public ClientLogTests()
		{
			Directory.CreateDirectory(Path.Combine(_sandbox, "Logs"));
			_file = Path.Combine(_sandbox, "Logs", "log-111-test-20261007.txt");
		}

		[Fact]
		public void UnfinishedLine_IsReadOnlyAfterItIsCompleted()
		{
			var log = new ClientLog(_sandbox);
			Append("[INF] Workflow: \"Start\" --[\"RecipientPickupRequested\"]--> \"Recipient");

			Assert.Equal(0, log.Mark());
			Assert.Empty(log.TransitionsAfter(0));

			Append("CodeInput\" {  }\r\n");
			var transition = Assert.Single(log.TransitionsAfter(0));
			Assert.Equal(WorkflowState.Start, transition.From);
			Assert.Equal(WorkflowState.RecipientCodeInput, transition.To);
			Assert.Equal("RecipientPickupRequested", transition.Trigger);
		}

		[Fact]
		public void MultiByteCharacterSplitBetweenWrites_IsDecodedCorrectly()
		{
			var log = new ClientLog(_sandbox);
			var bytes = Encoding.UTF8.GetBytes("Plaćanje odbijeno\n");
			var split = Array.IndexOf(bytes, (byte)0xC4) + 1;	// 'ć' = C4 87 — prekid usred znaka

			AppendBytes(bytes[..split]);
			Assert.Equal(0, log.Mark());
			AppendBytes(bytes[split..]);

			Assert.Equal(1, log.Mark());
			Assert.Single(log.LinesAfter(0, "Plaćanje odbijeno"));
		}

		[Fact]
		public void IgnoredTrigger_IsParsedWithStateButTransitionIsNot()
		{
			var log = new ClientLog(_sandbox);
			Append("[INF] Workflow: \"SenderDepositBarcodeValidating\" --[\"Reset\"]--> \"Start\" {  }\r\n");
			Append("[WRN] Workflow: ignored trigger \"SenderBarcodeAccepted\" from state \"Start\" (nije dozvoljen iz tog stanja). {  }\r\n");
			Append("[WRN] Workflow: trigger \"SenderDepositRequested\" odbačen — prihvata se samo u stanju \"Start\", trenutno \"Idle\". {  }\r\n");

			var ignored = Assert.Single(log.IgnoredTriggersAfter(0));
			Assert.Equal(("SenderBarcodeAccepted", WorkflowState.Start), (ignored.Trigger, ignored.State));
			Assert.Equal(1, ignored.Line);
			Assert.Empty(log.IgnoredTriggersAfter(2));
			Assert.Single(log.TransitionsAfter(0));
		}

		[Fact]
		public void FirstLineAfterAndTimeOf_UseClientTimestamp()
		{
			var log = new ClientLog(_sandbox);
			Append("[2026-10-07 22:37:29.405 INF] Workflow: \"CourierManualLogin\" --[\"CourierManualCodeEntered\"]--> \"CourierManualAuthenticating\" {  }\r\n");
			Append("[2026-10-07 22:37:32.223 DBG] MainWindowViewModel: executing GoToStartPageCommand. {  }\r\n");
			Append("  HEADER: Accept: application/json\r\n");

			var home = log.FirstLineAfter(0, "executing GoToStartPageCommand");
			Assert.Equal(1, home);
			Assert.Null(log.FirstLineAfter(2, "executing GoToStartPageCommand"));
			Assert.Equal(TimeSpan.FromMilliseconds(2818), log.TimeOf(home!.Value) - log.TimeOf(0));
			Assert.Throws<E2EException>(() => log.TimeOf(2));	// nastavak višelinijske poruke nema vreme
		}

		/// <summary>
		/// Test koji prelazi ponoć (pao 2026-10-16 u 00:00): Serilog nastavlja u fajlu sa sledećim datumom, a čitanje je
		/// ostajalo na starom — prelaz upisan posle ponoći test nije video. Kraj starog fajla upisan posle prvog čitanja
		/// ne sme da se izgubi.
		/// </summary>
		[Fact]
		public void MidnightRollover_ContinuesInNextDayFile()
		{
			var log = new ClientLog(_sandbox);
			Append("[2026-10-07 23:59:58.100 INF] Workflow: \"Start\" --[\"RecipientPickupRequested\"]--> \"RecipientCodeInput\" {  }\r\n");
			Assert.Equal(1, log.Mark());

			Append("[2026-10-07 23:59:59.900 INF] poslednja linija pre ponoći {  }\r\n");
			var next = Path.Combine(_sandbox, "Logs", "log-111-test-20261008.txt");
			File.WriteAllText(next, "[2026-10-08 00:00:00.200 INF] Workflow: \"RecipientPackagePickup\" --[\"RecipientPickupComplete\"]--> \"Start\" {  }\r\n");

			var transition = Assert.Single(log.TransitionsAfter(1));
			Assert.Equal((WorkflowState.Start, 2), (transition.To, transition.Line));
			Assert.Single(log.LinesAfter(1, "poslednja linija pre ponoći"));
		}

		private void Append(string text) => AppendBytes(Encoding.UTF8.GetBytes(text));

		private void AppendBytes(byte[] bytes)
		{
			using var stream = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
			stream.Write(bytes);
		}

		public void Dispose()
		{
			if (Directory.Exists(_sandbox)) {
				Directory.Delete(_sandbox, recursive: true);
			}
		}
	}
}
