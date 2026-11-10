using TopPos.LockerSimulator.Pos;
using Xunit;

namespace TopPos.LockerSimulator.Tests
{
	public class PosFrameTests
	{
		public static IEnumerable<object[]> Holds => GoldenFrames.Holds.Select(h => new object[] { h.Id, h.Code, h.Text, h.Hex });

		[Theory]
		[MemberData(nameof(Holds))]
		public void Build_ProducesSameBytesAsRealTerminal(string id, string code, string text, string hex)
		{
			byte[] frame = id == "25" ? PosFrame.BuildHoldWithCode(code, text) : PosFrame.BuildErrorWithCode(code, text);

			Assert.Equal(hex, Convert.ToHexString(frame));
		}

		[Theory]
		[MemberData(nameof(Holds))]
		public void Lrc_MatchesCapturedPosFrames(string id, string code, string text, string hex)
		{
			_ = (id, code, text);
			byte[] frame = GoldenFrames.Bytes(hex);

			Assert.Equal(frame[^1], PosFrame.Lrc(frame.AsSpan(1, frame.Length - 2)));
		}

		[Fact]
		public void Lrc_MatchesCapturedEcrRequests_WithBothLeadingStxExcluded()
		{
			foreach (var (lrc, hex) in GoldenFrames.RequestsWithControlLrc) {
				byte[] frame = GoldenFrames.Bytes(hex);
				Assert.Equal(lrc, PosFrame.Lrc(frame.AsSpan(2, frame.Length - 3)));
			}
		}

		[Theory]
		[InlineData("A\u001CB")]
		[InlineData("A\u0003B")]
		[InlineData("A\u0002B")]
		[InlineData("ČAČAK")]
		public void FieldValue_WithSeparatorOrNonAscii_IsRejected(string value)
		{
			Assert.Throws<ArgumentException>(() => PosFrame.BuildHoldWithCode("51", value));
		}

		[Fact]
		public void ToDisplay_UsesEcrLogNotation()
		{
			byte[] frame = PosFrame.BuildErrorWithCode("09", "PREKINUTO");

			Assert.Equal("<STX>26000000PREKINUTO<FS>09<FS><ETX>K", PosFrame.ToDisplay(frame));
		}

		[Fact]
		public void CancelConfirmation_HasNoFieldSeparators()
		{
			byte[] frame = PosFrame.BuildCancelConfirmation("PREKINUTO");

			Assert.Equal("<STX>24000000PREKINUTO<ETX>", PosFrame.ToDisplay(frame.AsSpan(0, frame.Length - 1)));
		}
	}
}
