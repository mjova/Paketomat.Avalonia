using System.Text;

namespace TopPos.LockerSimulator.Pos
{
	public enum PosRequestKind
	{
		/// <summary>Zahtev 00 sa poznatim tipom transakcije.</summary>
		Transaction,
		/// <summary>Zahtev 00 koji terminal ne može da protumači — odgovara se 26/06 "POGRESAN UNOS".</summary>
		Invalid,
		/// <summary>Zahtev 23 — ECR otkazuje transakciju (EcrPort.CancelTransaction).</summary>
		Cancel,
		/// <summary>Okvir sa nepoznatim identifikatorom.</summary>
		Unknown,
	}

	/// <summary>
	/// Zahtev ECR → POS. Zaglavlje zahteva 00 se čita po fiksnim pozicijama, kao na pravom terminalu:
	/// id(2) terminalId(2) sourceId(2) seq(4) tip(2) printer(1) cashier(2) [transactionNumber], pa FS, iznos u parama...
	/// </summary>
	public sealed class PosRequest
	{
		public const string TypeSale = "01";
		public const string TypeCancelLast = "11";
		public const string TypeReturnTid = "96";

		private static readonly HashSet<string> __knownTypes = [TypeSale, TypeCancelLast, TypeReturnTid];

		private const int MinHeaderLength = 15;

		public PosRequestKind Kind { get; private init; }

		public string Identifier { get; private init; } = string.Empty;

		public string Header { get; private init; } = string.Empty;

		/// <summary>4 cifre, onako kako ih čita terminal (pozicije 6..9 zaglavlja).</summary>
		public string SeqNumber { get; private init; } = string.Empty;

		/// <summary>2 cifre, onako kako ih čita terminal (pozicije 10..11 zaglavlja).</summary>
		public string TransactionType { get; private init; } = string.Empty;

		/// <summary>Iznos u parama (polje posle prvog FS).</summary>
		public long Amount { get; private init; }

		/// <summary>Zašto je zahtev neispravan (za log).</summary>
		public string? Error { get; private init; }

		/// <summary>Parsira ceo okvir STX STX telo ETX LRC.</summary>
		public static PosRequest Parse(ReadOnlySpan<byte> frame)
		{
			int start = 0;
			while (start < frame.Length && frame[start] == PosFrame.STX) {
				start++;
			}
			int etx = frame.IndexOf(PosFrame.ETX);
			if (etx < start + 2) {
				return new PosRequest { Kind = PosRequestKind.Unknown, Error = "okvir je prekratak" };
			}
			string body = Encoding.ASCII.GetString(frame[start..etx]);
			string id = body[..2];

			if (id == "23") {
				return new PosRequest { Kind = PosRequestKind.Cancel, Identifier = id, Header = body };
			}
			if (id != "00") {
				return new PosRequest { Kind = PosRequestKind.Unknown, Identifier = id, Header = body, Error = $"nepoznat identifikator {id}" };
			}

			string[] fields = body.Split(PosFrame.FsChar);
			string header = fields[0];
			long amount = fields.Length > 1 && long.TryParse(fields[1], out long a) ? a : 0;

			// Terminal čita fiksne pozicije. Kraće zaglavlje (seq bez vodećih nula) pomera tip transakcije,
			// npr. seq "205" + tip "96" → terminal vidi seq "2059" i tip "60".
			string seq = header.Length >= 10 ? header[6..10] : string.Empty;
			string type = header.Length >= 12 ? header[10..12] : string.Empty;

			string? error = null;
			if (header.Length < MinHeaderLength) {
				error = $"zaglavlje ima {header.Length} znakova umesto najmanje {MinHeaderLength} (terminal čita seq \"{seq}\", tip \"{type}\")";
			}
			else if (!seq.All(char.IsAsciiDigit)) {
				error = $"seq \"{seq}\" nije broj";
			}
			else if (!__knownTypes.Contains(type)) {
				error = $"nepodržan tip transakcije \"{type}\"";
			}

			return new PosRequest {
				Kind = error == null ? PosRequestKind.Transaction : PosRequestKind.Invalid,
				Identifier = id,
				Header = header,
				SeqNumber = seq,
				TransactionType = type,
				Amount = amount,
				Error = error,
			};
		}
	}
}
