using System.Net;
using Flatsoft.Common;
using Flatsoft.Common.UI;
using Microsoft.Extensions.Logging;
using Stateless;
using TopPos.Kiosk.ViewModels;
using TopPos.Kiosk.Workflow;
using TopPos.LockController;
using TopPos.PenPosService;
using TopPos.Service;
using Xunit;
using static TopPos.Kiosk.PosClient;
using S = TopPos.Kiosk.Workflow.WorkflowState;
using T = TopPos.Kiosk.Workflow.WorkflowTrigger;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// v4: ishodi POS-a u <see cref="RecipientFlowActivator.GoToRecipientPaymentPage"/> (v2 ih pokriva samo E2E-om): poruka koju
	/// kupac vidi, sledeće stanje toka i sudbina uplate (potvrda serveru, red za slanje). Aktivator radi sa lažnim hostom,
	/// lažnim POS-om i stub serverom; mašina je dovedena u RecipientPayment bez OnEntry handlera.
	/// </summary>
	public class RecipientPaymentOutcomeTests : IDisposable
	{
		private const string UnlockCode = "123456";
		private const int DoorNumber = 5;

		private readonly StubHttpServer _server = new();
		private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("toppos-payment-");
		private readonly List<FakeWorkflowHost> _hosts = [];

		public void Dispose()
		{
			foreach (var host in _hosts) {
				host.Dispose();     // pozadinski red potvrda (i kad test padne pre kraja)
			}
			_server.Dispose();
			_dir.Delete(recursive: true);
		}

		private static string Error(string message) => $"{{\"responseStatus\":{{\"errorCode\":\"ArgumentException\",\"message\":\"{message}\"}}}}";

		/// <summary>Server: potvrda uplate i dodela vrata po putanji zahteva.</summary>
		private void Server((HttpStatusCode, string) paymentConfirmation, (HttpStatusCode, string)? pickup = null)
			=> _server.Handler = (requestLine, _) => requestLine.Contains(nameof(PotvrdaPlacanja)) ? paymentConfirmation
																									: pickup ?? (HttpStatusCode.OK, $"{{\"brojPozicije\":{DoorNumber},\"iznosZaUplatu\":0}}");

		private async Task<FakeWorkflowHost> RunPayment(FakePos pos, bool penPos = false)
		{
			var host = new FakeWorkflowHost(_server.BaseUrl, _dir.FullName, pos, penPos);
			_hosts.Add(host);
			await DriveToPayment(host);
			await new RecipientFlowActivator(host).GoToRecipientPaymentPage();
			return host;
		}

		private static FakePos Approved() => new() { SaleResult = (ResponseCodes.Success, TransactionFlagCodes.TransactionAcceptedWithAuthorization, "000041", "SIMUL001") };

		[Fact]
		public async Task Unavailable_terminal_declines_with_terminal_error()
		{
			var pos = new FakePos { IsConnected = false, Presence = false };
			var host = await RunPayment(pos);

			Assert.Equal("RecipientPaymentPage.POSTerminalError", Assert.Single(host.Messages));
			Assert.Equal(S.Start, host.WorkflowMachine.State);
			Assert.Equal(0, pos.Sales);
		}

		[Fact]
		public async Task Canceled_on_terminal_shows_information_and_declines()
		{
			var host = await RunPayment(new FakePos { SaleResult = (ResponseCodes.TransactionCanceled, TransactionFlagCodes.NotValidValue, null, null) });

			Assert.Equal((CanceledByUserMessage, MessageType.Information), Assert.Single(host.MessagesWithType));
			Assert.Equal(S.Start, host.WorkflowMachine.State);
			Assert.Empty(_server.Requests);
		}

		[Theory]
		[InlineData(ResponseCodes.ComUnavailable)]
		[InlineData(ResponseCodes.FunctionTimeout)]
		public async Task Terminal_failure_declines_with_terminal_error(ResponseCodes status)
		{
			var host = await RunPayment(new FakePos { SaleResult = (status, TransactionFlagCodes.NotValidValue, null, null) });

			Assert.Equal("RecipientPaymentPage.POSTerminalError", Assert.Single(host.Messages));
			Assert.Equal(S.Start, host.WorkflowMachine.State);
			Assert.Empty(_server.Requests);
		}

		[Theory]
		[InlineData(TransactionFlagCodes.TransactionRefused, "RecipientPaymentPage.PaymentRefused")]
		[InlineData(TransactionFlagCodes.CommError, "RecipientPaymentPage.PaymentFailed")]
		public async Task Unsuccessful_transaction_declines_without_confirmation(TransactionFlagCodes flag, string message)
		{
			var host = await RunPayment(new FakePos { SaleResult = (ResponseCodes.Success, flag, null, null) });

			Assert.Equal(message, Assert.Single(host.Messages));
			Assert.Equal(S.Start, host.WorkflowMachine.State);
			Assert.Empty(_server.Requests);
		}

		[Theory]
		[InlineData(false, S.RecipientPackagePickup)]
		[InlineData(true, S.RecipientFiscalReceipt)]
		public async Task Approved_payment_is_confirmed_and_door_assigned(bool penPos, S next)
		{
			Server((HttpStatusCode.OK, "{\"evidencioniBrojUplate\":1820993}"));
			var host = await RunPayment(Approved(), penPos);

			Assert.Empty(host.Messages);
			Assert.Equal(next, host.WorkflowMachine.State);
			Assert.Equal(DoorNumber, host.WorkflowContext.RecipientPackageDoorNumber);
			Assert.Contains(_server.Requests, r => r.RequestLine.Contains(nameof(PotvrdaPlacanja)));
			Assert.False(File.Exists(host.PaymentConfirmationFile));
		}

		/// <summary>Polly ponovio POST posle izgubljenog odgovora: server je uplatu već evidentirao — nastavlja se kao posle potvrde.</summary>
		[Fact]
		public async Task Already_recorded_payment_continues_to_pickup()
		{
			Server((HttpStatusCode.BadRequest, Error("Za paket sa kodom \\\"123456\\\" je ve\\u0107 evidentirana uplata")));
			var host = await RunPayment(Approved());

			Assert.Empty(host.Messages);
			Assert.Equal(S.RecipientPackagePickup, host.WorkflowMachine.State);
			Assert.Equal(DoorNumber, host.WorkflowContext.RecipientPackageDoorNumber);
		}

		/// <summary>Kartica naplaćena, server nedostupan: potvrda ide u red na disku, kupac dobija „pokušajte ponovo“.</summary>
		[Fact]
		public async Task Unconfirmed_payment_is_queued_and_declined_as_recoverable()
		{
			Server((HttpStatusCode.NotFound, "<html><body>404</body></html>"));
			var host = await RunPayment(Approved());

			Assert.Equal("RecipientPaymentPage.PaymentConfirmationError.Recoverable", Assert.Single(host.Messages));
			Assert.Equal(S.Start, host.WorkflowMachine.State);
			Assert.Contains("000041", File.ReadAllText(host.PaymentConfirmationFile));
		}

		/// <summary>Server je odbio potvrdu (ne ponavlja se): bez reda, kupac dobija „pozovite podršku“.</summary>
		[Fact]
		public async Task Rejected_confirmation_is_declined_as_unrecoverable()
		{
			Server((HttpStatusCode.BadRequest, Error("Neispravan iznos")));
			var host = await RunPayment(Approved());

			Assert.Equal("RecipientPaymentPage.PaymentConfirmationError.Unrecoverable", Assert.Single(host.Messages));
			Assert.Equal(S.Start, host.WorkflowMachine.State);
			Assert.False(File.Exists(host.PaymentConfirmationFile));
		}

		/// <summary>Uplata potvrđena, a dodela vrata nije uspela: poruka servera, pri ponovnom unosu kupac ne plaća ponovo.</summary>
		[Fact]
		public async Task Confirmed_payment_without_door_declines_with_server_message()
		{
			Server((HttpStatusCode.OK, "{\"evidencioniBrojUplate\":1820993}"), (HttpStatusCode.BadRequest, Error("Paket nije dostupan")));
			var host = await RunPayment(Approved());

			Assert.Equal("Paket nije dostupan", Assert.Single(host.Messages));
			Assert.Equal(S.Start, host.WorkflowMachine.State);
		}

		/// <summary>Izuzetak iz POS-a pre naplate: ne Reset (ignorisan u RecipientPayment), nego Declined sa porukom terminala.</summary>
		[Fact]
		public async Task Exception_from_terminal_declines_instead_of_hanging()
		{
			var host = await RunPayment(new FakePos { SaleException = new IOException("port zatvoren") });

			Assert.Equal("RecipientPaymentPage.POSTerminalError", Assert.Single(host.Messages));
			Assert.Equal(S.Start, host.WorkflowMachine.State);
		}

		// ─── Lažni POS i dovođenje do plaćanja ──────────────────────────────

		private sealed class FakePos : IPosClient
		{
			public bool IsConnected { get; set; } = true;
			public bool Presence { get; init; } = true;
			public (ResponseCodes, TransactionFlagCodes, string?, string?) SaleResult { get; init; }
			public Exception? SaleException { get; init; }
			public int Sales { get; private set; }

			public Task<bool> CheckPresence(CancellationToken cancellationToken) => Task.FromResult(Presence);

			public ValueTask<(ResponseCodes, TransactionFlagCodes, string?, string?)> Sale(decimal amount, Func<string, ValueTask>? posTerminalMessagesHandler,
																							CancellationToken cancellationToken)
			{
				Sales++;
				if (SaleException != null) {
					throw SaleException;
				}
				return ValueTask.FromResult(SaleResult);
			}
		}

		/// <summary>Start → šifra → telefon → provera → plaćanje, sa iznosom i šifrom u kontekstu (kao posle provere na serveru).</summary>
		private static async Task DriveToPayment(FakeWorkflowHost host)
		{
			var m = host.WorkflowMachine;
			await m.FireAsync(T.Reset);
			await m.FireAsync(T.RecipientPickupRequested);
			await m.FireAsync(m.RecipientCodeAccepted, UnlockCode);
			await m.FireAsync(m.RecipientPhoneEntered, "0641234567");
			host.WorkflowContext.UnlockCode = UnlockCode;
			host.WorkflowContext.RecipientPhoneNumber = "0641234567";
			host.WorkflowContext.PaymentAmount = 350.50m;
			await m.FireAsync(T.RecipientPaymentRequired);
			Assert.Equal(S.RecipientPayment, m.State);
		}
	}
}