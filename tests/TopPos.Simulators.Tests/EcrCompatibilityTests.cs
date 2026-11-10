using TopPos.LockerSimulator.Pos;
using Xunit;

namespace TopPos.LockerSimulator.Tests
{
	/// <summary>Okviri simulatora kroz pravi ECR parser: ono što PosClient posle čita mora da stigne tačno.</summary>
	public class EcrCompatibilityTests
	{
		[Fact]
		public void HoldWithCode_IsExtendedMessageWithTextAndCode()
		{
			var message = Ecr.Parse(PosFrame.BuildHoldWithCode("51", "PROVUCI/UBACI KARTICU"));

			Assert.True(message.Call("isHoldWithCode"));
			Assert.Equal("51", message.Field("code"));
			Assert.Equal("PROVUCI/UBACI KARTICU", message.Field("displayMessage"));
		}

		[Fact]
		public void ErrorWithCode_IsErrorWithTextAndCode()
		{
			var message = Ecr.Parse(PosFrame.BuildErrorWithCode("09", "PREKINUTO"));

			Assert.True(message.Call("isErrorWithCode"));
			Assert.Equal("09", message.Field("code"));
			Assert.Equal("PREKINUTO", message.Field("displayMessage"));
		}

		[Fact]
		public void CancelConfirmation_IsCancel()
		{
			var message = Ecr.Parse(PosFrame.BuildCancelConfirmation("PREKINUTO"));

			Assert.True(message.Call("isCancel"));
			Assert.Equal("PREKINUTO", message.Field("displayMessage"));
		}

		[Theory]
		[InlineData(PosResponses.FlagApprovedWithAuthorization, "ODOBRENO")]
		[InlineData(PosResponses.FlagRefused, "ODBIJENO")]
		[InlineData(PosResponses.FlagCommError, "KOMUNIKACIONA GRESKA")]
		public void SaleResponse_FillsTransactionParamsReadByPosClient(string flag, string text)
		{
			var frame = PosResponses.BuildSaleResponse(new PosResponses.SaleResult(
				"0042", flag, 150000, new DateTime(2026, 10, 6, 14, 30, 5), SimCard.VisaChip, "000123", "654321",
				"SIMT0001", "1000001", text, "00", PinUsed: true));

			var message = Ecr.Parse(frame);
			var tp = message.ApplyTo(Ecr.NewTransaction("0042", "01", 150000));

			Assert.Equal("POS2ECRMessage_10V3", message.TypeName);
			Assert.Equal(flag, tp.TransactionFlag);
			Assert.Equal("000123", tp.TransactionNumber);
			Assert.Equal("SIMT0001", tp.TidNumber);
			Assert.Equal("0", message.Field("moreMessagesFlag"));
			Assert.Equal(text, message.Field("displayMessage")?.Trim());
			Assert.Equal("TEST/PERA", message.Field("cardHolderName"));
		}

		[Fact]
		public void SaleResponse_HasSameFieldCountAsCapturedResponses()
		{
			var frame = PosResponses.BuildSaleResponse(new PosResponses.SaleResult(
				"0042", "02", 100, DateTime.Now, SimCard.VisaChip, "000001", "000001", "SIMT0001", "1000001", "ODOBRENO", "00", PinUsed: false));

			Assert.Equal(62, frame[..^1].Count(b => b == PosFrame.FS));	// bez LRC-a (može biti i 0x1C)
		}

		[Fact]
		public void TidResponse_FillsTidNumber()
		{
			var frame = PosResponses.BuildTidResponse("4072", "SIMT0001");

			var tp = Ecr.Parse(frame).ApplyTo(Ecr.NewTransaction("4072", "96", 100));

			Assert.Equal(PosResponses.FlagTerminalId, tp.TransactionFlag);
			Assert.Equal("SIMT0001", tp.TidNumber);
			Assert.Equal(62, frame[..^1].Count(b => b == PosFrame.FS));	// bez LRC-a (može biti i 0x1C)
		}
	}
}
