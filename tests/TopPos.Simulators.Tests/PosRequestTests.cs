using TopPos.LockerSimulator.Pos;
using Xunit;

namespace TopPos.LockerSimulator.Tests
{
	public class PosRequestTests
	{
		[Theory]
		[InlineData("1000")]
		[InlineData("9999")]
		[InlineData("0000")]
		[InlineData("0205")]
		public void RequestFromRealEcrSerializer_WithFourDigitSeq_IsValid(string seq)
		{
			byte[] frame = Ecr.SerializeRequest(Ecr.NewTransaction(seq, "01", 123456));

			var request = PosRequest.Parse(frame);

			Assert.Equal(PosRequestKind.Transaction, request.Kind);
			Assert.Equal(seq, request.SeqNumber);
			Assert.Equal(PosRequest.TypeSale, request.TransactionType);
			Assert.Equal(123456, request.Amount);
		}

		[Theory]
		[InlineData("0", "01")]
		[InlineData("9", "96")]
		[InlineData("99", "01")]
		[InlineData("999", "96")]
		[InlineData("205", "96")]
		[InlineData("397", "01")]
		public void RequestWithShortSeq_IsInvalid_LikeOnRealTerminal(string seq, string type)
		{
			// Bug iz PosClient (seq bez vodećih nula, popravljen 2026-10-06): terminal je odgovarao POGRESAN UNOS.
			byte[] frame = Ecr.SerializeRequest(Ecr.NewTransaction(seq, type, 100));

			var request = PosRequest.Parse(frame);

			Assert.Equal(PosRequestKind.Invalid, request.Kind);
			Assert.NotNull(request.Error);
		}

		[Fact]
		public void CapturedShortSeqRequest_IsInvalid()
		{
			// logs/1/std_log_2026-06-02.log: seq 858, tip 96 — terminal bi pročitao seq "8589" i tip "60".
			byte[] frame = GoldenFrames.Bytes(GoldenFrames.RequestsWithControlLrc[1].Hex);

			var request = PosRequest.Parse(frame);

			Assert.Equal(PosRequestKind.Invalid, request.Kind);
			Assert.Equal("8589", request.SeqNumber);
			Assert.Equal("60", request.TransactionType);
		}

		[Fact]
		public void CapturedSaleRequest_IsParsed()
		{
			byte[] frame = GoldenFrames.Bytes(GoldenFrames.RequestsWithControlLrc[0].Hex);

			var request = PosRequest.Parse(frame);

			Assert.Equal(PosRequestKind.Transaction, request.Kind);
			Assert.Equal("2773", request.SeqNumber);
			Assert.Equal("01", request.TransactionType);
			Assert.Equal(368700, request.Amount);
		}

		[Fact]
		public void EcrCancelCommand_IsRecognized()
		{
			// EcrConnectorV3.ReturnCancelCommand: STX STX 2 3 0 0 0 0 0 0 ETX LRC
			byte[] body = "23000000\u0003"u8.ToArray();
			byte[] frame = [PosFrame.STX, PosFrame.STX, .. body, PosFrame.Lrc(body)];

			Assert.Equal(PosRequestKind.Cancel, PosRequest.Parse(frame).Kind);
		}

		[Fact]
		public void TidRequest_IsValid()
		{
			var request = PosRequest.Parse(Ecr.SerializeRequest(Ecr.NewTransaction("4072", "96", 100)));

			Assert.Equal(PosRequestKind.Transaction, request.Kind);
			Assert.Equal(PosRequest.TypeReturnTid, request.TransactionType);
		}
	}
}
