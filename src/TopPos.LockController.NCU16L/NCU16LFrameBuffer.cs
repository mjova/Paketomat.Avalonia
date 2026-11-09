using TopPos.LockController;

namespace TopPos.LockController.NCU16L
{
	/// <summary>
	/// Prijemni bafer NCU16L: svaki odgovor ima fiksnu dužinu — STX + ADDR + CMD + DATA(4) + ETX + SUM.
	/// SUM je niži bajt zbira prvih osam bajtova. Podaci mogu da sadrže i 0x02 i 0x03.
	/// </summary>
	public sealed class NCU16LFrameBuffer : NcuFrameBuffer
	{
		private const int FrameLength = 9;

		public NCU16LFrameBuffer(Action<string>? warning = null, Action<string>? debug = null)
			: base(warning, debug)
		{
		}

		protected override byte Stx => NCU16LProtocol.STX;

		protected override FrameCheck CheckFrame(IReadOnlyList<byte> bytes, out int length)
		{
			length = FrameLength;
			if (bytes.Count < FrameLength - 1) {
				return FrameCheck.Incomplete;
			}

			if (bytes[FrameLength - 2] != NCU16LProtocol.ETX) {
				return FrameCheck.Invalid;
			}

			if (bytes.Count < FrameLength) {
				return FrameCheck.Incomplete;
			}

			int sum = 0;
			for (int i = 0; i < FrameLength - 1; i++) {
				sum += bytes[i];
			}

			return (byte)(sum & 0xFF) == bytes[FrameLength - 1] ? FrameCheck.Complete : FrameCheck.Invalid;
		}
	}
}
