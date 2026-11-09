using TopPos.LockController;

namespace TopPos.LockController.NCU24L
{
	/// <summary>
	/// Prijemni bafer NCU24L: STX + ADDR + LOCKNUM + CMD + ASK + DATALEN + ETX + SUM + DATA(DATALEN).
	/// SUM je niži bajt zbira svih bajtova okvira osim samog SUM.
	/// </summary>
	public sealed class NCU24LFrameBuffer : NcuFrameBuffer
	{
		/// <summary>Zaglavlje sa SUM, bez podataka.</summary>
		private const int HeaderLength = 8;

		/// <summary>Najveći DATALEN koji se prihvata; odgovori NCU24L nose najviše nekoliko bajtova podataka.</summary>
		private const int MaxDataLength = 32;

		public NCU24LFrameBuffer(Action<string>? warning = null, Action<string>? debug = null)
			: base(warning, debug)
		{
		}

		protected override byte Stx => NCU24LProtocol.STX;

		protected override FrameCheck CheckFrame(IReadOnlyList<byte> bytes, out int length)
		{
			length = 0;
			if (bytes.Count < 7) {
				return FrameCheck.Incomplete;
			}

			if (bytes[6] != NCU24LProtocol.ETX || bytes[5] > MaxDataLength) {
				return FrameCheck.Invalid;
			}

			length = HeaderLength + bytes[5];
			if (bytes.Count < length) {
				return FrameCheck.Incomplete;
			}

			int sum = 0;
			for (int i = 0; i < length; i++)
			{
				if (i != 7) {
					sum += bytes[i];
				}
			}

			return (byte)(sum & 0xFF) == bytes[7] ? FrameCheck.Complete : FrameCheck.Invalid;
		}
	}
}
