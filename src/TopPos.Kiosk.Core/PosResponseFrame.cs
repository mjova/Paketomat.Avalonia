using System.Text;

namespace TopPos.Kiosk
{
	/// <summary>
	/// Završni odgovor POS terminala (poruka <c>10</c>) iz sirovog okvira: <c>STX telo ETX LRC</c>. ECR ne proverava LRC i
	/// ne vezuje odgovor za zahtev, pa <see cref="PosClient"/> to radi sam — samo za log, bez uticaja na ishod naplate.
	/// Zaglavlje (do prvog FS): id(2) terminalId(2) sourceId(2) seq(4) tip(2) flag(2) …; polje 1 = iznos u parama (12 cifara),
	/// polje 4 = valuta (ECR logovi tri paketomata, 2025-11 … 2026-10: seq, tip i iznos se uvek poklapaju sa zahtevom).
	/// </summary>
	public sealed record PosResponseFrame(string Seq, string Type, string Flag, string Amount, string Currency, PosResponseFrame.LrcStatus Lrc)
	{
		public enum LrcStatus
		{
			Ok,
			Mismatch,
			/// <summary>
			/// ECR čita LRC sa <c>ReadChar()</c> kroz ASCII kodiranje porta: pravi LRC ≥ 0x80 (odgovor sa našim slovima, npr.
			/// đ/ć/č u cp1250) stigne kao „?". U logovima je to bio jedini uzrok „pogrešnog" LRC-a — takav okvir se ne može proveriti.
			/// </summary>
			NotVerifiable,
		}

		private const byte STX = 0x02, ETX = 0x03, FS = 0x1C;

		/// <summary>Okvir poruke <c>10</c>, ili <c>null</c> ako okvir nije poruka 10 (ACK, međuporuke 25/26, zahtev…).</summary>
		public static PosResponseFrame? TryParse(byte[] frame)
		{
			int start = 0;
			while (start < frame.Length && frame[start] == STX) {
				start++;
			}
			// najkraće: id(2) … flag(2) = 14 znakova, ETX, LRC
			if (frame.Length - start < 16 || frame[^2] != ETX || frame[start] != '1' || frame[start + 1] != '0') {
				return null;
			}
			byte lrc = 0;
			for (int i = start; i < frame.Length - 1; i++) {
				lrc ^= frame[i];
			}
			var received = frame[^1];
			var lrcStatus = lrc == received ? LrcStatus.Ok
							: received == (byte)'?' && lrc >= 0x80 ? LrcStatus.NotVerifiable
							: LrcStatus.Mismatch;

			var fields = Encoding.Latin1.GetString(frame, start, frame.Length - start - 2).Split((char)FS);
			var header = fields[0];
			if (header.Length < 14) {
				return null;
			}
			return new PosResponseFrame(Seq: header.Substring(6, 4),
										Type: header.Substring(10, 2),
										Flag: header.Substring(12, 2),
										Amount: fields.Length > 1 ? fields[1] : "",
										Currency: fields.Length > 4 ? fields[4] : "",
										Lrc: lrcStatus);
		}

		/// <summary>
		/// Odstupanja od poslatog zahteva (prazno = sve se poklapa). Iznos se poredi samo za prodaju (odgovor na TID nema iznos).
		/// </summary>
		public IReadOnlyList<string> Differences(string seq, string type, long? amountInPara, string currency)
		{
			var differences = new List<string>();
			if (Lrc == LrcStatus.Mismatch) {
				differences.Add("LRC");
			}
			if (Seq != seq) {
				differences.Add($"seq {Seq} (poslato {seq})");
			}
			if (Type != type) {
				differences.Add($"tip {Type} (poslato {type})");
			}
			if (amountInPara is { } amount && (!long.TryParse(Amount, out var received) || received != amount)) {
				differences.Add($"iznos {Amount} (poslato {amount})");
			}
			if (Currency != currency) {
				differences.Add($"valuta {Currency} (poslato {currency})");
			}
			return differences;
		}

		public override string ToString() => $"seq={Seq}, tip={Type}, flag={Flag}, iznos={Amount}, valuta={Currency}, LRC={Lrc}";
	}
}
