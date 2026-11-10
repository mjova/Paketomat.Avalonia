using System.Text;

namespace TopPos.LockerSimulator.Pos
{
	/// <summary>
	/// Okviri ECR protokola (Payten/Ingenico, POS-ECR API v3) onako kako ih vidi TopPos.ECR na serijskom portu.
	/// ECR → POS: STX STX telo ETX LRC.  POS → ECR: STX telo ETX LRC.
	/// LRC = XOR svih bajtova posle vodećih STX-ova, zaključno sa ETX (potvrđeno na snimljenim okvirima).
	/// </summary>
	public static class PosFrame
	{
		public const byte STX = 0x02;
		public const byte ETX = 0x03;
		public const byte EOT = 0x04;
		public const byte ACK = 0x06;
		public const byte NAK = 0x15;
		public const byte FS = 0x1C;

		public const char FsChar = '\u001C';

		public static byte Lrc(ReadOnlySpan<byte> bodyWithEtx)
		{
			byte lrc = 0;
			foreach (byte b in bodyWithEtx) {
				lrc ^= b;
			}
			return lrc;
		}

		/// <summary>POS → ECR okvir: STX telo ETX LRC.</summary>
		public static byte[] Build(string body)
		{
			var frame = new byte[body.Length + 3];
			frame[0] = STX;
			for (int i = 0; i < body.Length; i++) {
				char c = body[i];
				if (c > 0x7E || (c < 0x20 && c != FsChar)) {
					throw new ArgumentException($"Nedozvoljen znak 0x{(int)c:X2} na poziciji {i} u telu okvira.", nameof(body));
				}
				frame[i + 1] = (byte)c;
			}
			frame[^2] = ETX;
			frame[^1] = Lrc(frame.AsSpan(1, frame.Length - 2));
			return frame;
		}

		/// <summary>Poruka 25 — "hold" sa kodom (PROVUCI/UBACI KARTICU, OBRADA KARTICE...). ECR je javlja kao EcrExtendedMessageReceived.</summary>
		public static byte[] BuildHoldWithCode(string code, string text) => Build($"25000000{CheckValue(text)}{FsChar}{CheckValue(code)}{FsChar}");

		/// <summary>Poruka 26 — greška sa kodom (PREKINUTO, NEUSPESNO, POGRESAN UNOS). ECR završava transakciju sa PosError.</summary>
		public static byte[] BuildErrorWithCode(string code, string text) => Build($"26000000{CheckValue(text)}{FsChar}{CheckValue(code)}{FsChar}");

		/// <summary>Poruka 24 — odgovor na otkazivanje (23). Format izveden iz parsera POS2ECRMessage_24V3 — nije potvrđen na pravom terminalu.</summary>
		public static byte[] BuildCancelConfirmation(string text) => Build($"24000000{CheckValue(text)}");

		/// <summary>Vrednost polja ne sme da sadrži separatore niti ne-ASCII znake (v1: samo ASCII, kao u svim snimljenim logovima).</summary>
		public static string CheckValue(string value)
		{
			foreach (char c in value) {
				if (c < 0x20 || c > 0x7E) {
					throw new ArgumentException($"Vrednost polja sadrži nedozvoljen znak 0x{(int)c:X2}: \"{value}\"", nameof(value));
				}
			}
			return value;
		}

		/// <summary>Prikaz okvira kao u ECR logu: &lt;STX&gt;25000000PROVUCI...&lt;FS&gt;51&lt;FS&gt;&lt;ETX&gt;D</summary>
		public static string ToDisplay(ReadOnlySpan<byte> data)
		{
			var sb = new StringBuilder(data.Length + 16);
			foreach (byte b in data) {
				switch (b) {
					case STX: sb.Append("<STX>"); break;
					case ETX: sb.Append("<ETX>"); break;
					case EOT: sb.Append("<EOT>"); break;
					case ACK: sb.Append("<ACK>"); break;
					case NAK: sb.Append("<NAK>"); break;
					case FS: sb.Append("<FS>"); break;
					default:
						if (b >= 0x20 && b <= 0x7E) {
							sb.Append((char)b);
						}
						else {
							sb.Append($"<{b:X2}>");
						}
						break;
				}
			}
			return sb.ToString();
		}

		public static string ToHex(ReadOnlySpan<byte> data) => Convert.ToHexString(data);
	}
}
