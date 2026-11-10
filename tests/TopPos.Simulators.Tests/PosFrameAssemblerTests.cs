using TopPos.LockerSimulator.Pos;
using Xunit;

namespace TopPos.LockerSimulator.Tests
{
	public class PosFrameAssemblerTests
	{
		[Fact]
		public void FrameFedByteByByte_IsAssembledOnce()
		{
			byte[] frame = GoldenFrames.Bytes(GoldenFrames.Holds[0].Hex);
			var assembler = new PosFrameAssembler();

			var events = frame.SelectMany(b => assembler.Feed(new[] { b })).ToList();

			var ev = Assert.Single(events);
			Assert.Equal(PosWireEventKind.Frame, ev.Kind);
			Assert.Equal(frame, ev.Data);
		}

		[Fact]
		public void AckAndTwoFramesInOneChunk_AreSplit()
		{
			byte[] f1 = PosFrame.BuildHoldWithCode("51", "PROVUCI/UBACI KARTICU");
			byte[] f2 = PosFrame.BuildHoldWithCode("54", "OBRADA KARTICE");
			byte[] chunk = [PosFrame.ACK, .. f1, PosFrame.NAK, .. f2];

			var events = new PosFrameAssembler().Feed(chunk).ToList();

			Assert.Equal(4, events.Count);
			Assert.True(events[0].IsAck);
			Assert.Equal(f1, events[1].Data);
			Assert.True(events[2].IsNak);
			Assert.Equal(f2, events[3].Data);
		}

		[Fact]
		public void LrcEqualToControlByte_IsTakenAsLrc_NotAsNextEvent()
		{
			foreach (var (lrc, hex) in GoldenFrames.RequestsWithControlLrc) {
				byte[] frame = GoldenFrames.Bytes(hex);
				byte[] chunk = [.. frame, PosFrame.ACK];

				var events = new PosFrameAssembler().Feed(chunk).ToList();

				Assert.Equal(2, events.Count);
				Assert.Equal(PosWireEventKind.Frame, events[0].Kind);
				Assert.Equal(lrc, events[0].Data[^1]);
				Assert.True(events[1].IsAck);
			}
		}

		[Fact]
		public void FrameSplitAcrossChunks_IsAssembled()
		{
			byte[] frame = GoldenFrames.Bytes(GoldenFrames.RequestsWithControlLrc[1].Hex);
			var assembler = new PosFrameAssembler();

			var first = assembler.Feed(frame.AsMemory(0, frame.Length - 1)).ToList();
			var second = assembler.Feed(frame.AsMemory(frame.Length - 1)).ToList();

			Assert.Empty(first);
			Assert.Equal(frame, Assert.Single(second).Data);
		}

		[Fact]
		public void OverlongFrame_IsDiscardedAsGarbage()
		{
			byte[] chunk = [PosFrame.STX, .. Enumerable.Repeat((byte)'A', PosFrameAssembler.MaxFrameLength + 10)];

			var events = new PosFrameAssembler().Feed(chunk).ToList();

			Assert.Equal(PosWireEventKind.Garbage, events[0].Kind);
			Assert.DoesNotContain(events, e => e.Kind == PosWireEventKind.Frame);
		}
	}
}
