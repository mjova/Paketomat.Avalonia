using System.Text;
using TopPos.Client.WPF;
using Xunit;

namespace TopPos.Workflow.Tests
{
	/// <summary>
	/// Završni odgovor POS terminala (10) i poređenje sa zahtevom — <see cref="PosClient"/> rezultat samo loguje. Okvir je
	/// snimak sa UPT test terminala (ECR log 2025-12-17, seq 3211, prodaja 4,95 RSD), u zapisu ASCII linije ECR loga.
	/// </summary>
	public class PosResponseFrameTests
	{
		private const string RecordedSale =
			"<STX>100000321101020000150001171225121112<FS>000000000495<FS><FS>+0<FS>941<FS>C<FS>9999999999996965<FS>9999<FS><FS><FS>" +
			"685047<FS>UPTTEST00<FS>11111111<FS>MASTERCARD<FS><FS><FS><FS><FS><FS><FS>ODOBRENO                 <FS><FS><FS>" +
			"8407A0000000041010950500200080019F12104445424954204D4153544552434152449F2608EA6C0690D1E3E4399F2701809F34033F0002<FS>1<FS>" +
			"<FS>0<FS><FS>00<FS>C1<FS><FS><FS><FS><FS>444412034444<FS>0<FS><FS><FS><FS><FS><FS><FS><FS><FS><FS><FS><FS>000000000000" +
			"<FS><FS><FS><FS><FS><FS><FS><FS><FS><FS>01<FS><FS><FS><FS><FS><ETX>!";

		private static byte[] Frame(string ecrLogAscii)
			=> Encoding.Latin1.GetBytes(ecrLogAscii.Replace("<STX>", "\u0002").Replace("<FS>", "\u001C").Replace("<ETX>", "\u0003"));

		/// <summary>Okvir sa izračunatim LRC-om (ili zadatim poslednjim bajtom).</summary>
		private static byte[] WithLrc(byte[] frame, byte? received = null)
		{
			byte lrc = 0;
			for (int i = 1; i < frame.Length - 1; i++) {
				lrc ^= frame[i];
			}
			frame[^1] = received ?? lrc;
			return frame;
		}

		[Fact]
		public void Recorded_sale_is_parsed_and_matches_request()
		{
			var response = PosResponseFrame.TryParse(Frame(RecordedSale));
			Assert.NotNull(response);
			Assert.Equal(("3211", "01", "02", "000000000495", "941", PosResponseFrame.LrcStatus.Ok),
						(response.Seq, response.Type, response.Flag, response.Amount, response.Currency, response.Lrc));
			Assert.Empty(response.Differences("3211", "01", 495, "941"));
		}

		[Fact]
		public void Different_seq_type_amount_and_currency_are_reported()
		{
			var response = PosResponseFrame.TryParse(Frame(RecordedSale))!;
			Assert.Equal(["seq 3211 (poslato 3212)", "tip 01 (poslato 96)", "iznos 000000000495 (poslato 500)", "valuta 941 (poslato 978)"],
						response.Differences("3212", "96", 500, "978"));
		}

		[Fact]
		public void Amount_is_not_compared_when_not_given()	// odgovor na TID nema iznos
		{
			var tid = PosResponseFrame.TryParse(WithLrc(Frame("<STX>100000320896050000000000000000000000<FS><FS><FS>+0<FS>941<FS><FS><ETX>?")))!;
			Assert.Equal(("3208", "96", "05", ""), (tid.Seq, tid.Type, tid.Flag, tid.Amount));
			Assert.Empty(tid.Differences("3208", "96", null, "941"));
		}

		[Fact]
		public void Changed_byte_gives_lrc_mismatch()
		{
			// flag 02 → 06 (jedan bit), stari LRC — kao reproduktor u doc/notes/pos-lrc-proof.ps1
			var response = PosResponseFrame.TryParse(Frame(RecordedSale.Replace("100000321101020000", "100000321101060000")))!;
			Assert.Equal(PosResponseFrame.LrcStatus.Mismatch, response.Lrc);
			Assert.Equal(["LRC"], response.Differences("3211", "01", 495, "941"));
		}

		[Fact]
		public void Question_mark_with_high_lrc_is_not_verifiable()
		{
			// Odgovor sa našim slovom (đ = 0xF0 u cp1250): pravi LRC ≥ 0x80, a ECR ga čita kroz ASCII kao „?" (ECR logovi, 14/4.652)
			var frame = Frame(RecordedSale.Replace("ODOBRENO ", "ODOBRENOð"));
			Assert.True(WithLrc((byte[])frame.Clone())[^1] >= 0x80);
			var response = PosResponseFrame.TryParse(WithLrc(frame, (byte)'?'))!;
			Assert.Equal(PosResponseFrame.LrcStatus.NotVerifiable, response.Lrc);
			Assert.Empty(response.Differences("3211", "01", 495, "941"));
		}

		[Fact]
		public void Question_mark_with_low_lrc_is_a_mismatch()
		{
			var frame = WithLrc(Frame(RecordedSale), (byte)'?');
			Assert.Equal(PosResponseFrame.LrcStatus.Mismatch, PosResponseFrame.TryParse(frame)!.Lrc);
		}

		[Theory]
		[InlineData("<STX>2500000000PROVUCI/UBACI KARTICU<FS>51<FS><ETX>D")]	// međuporuka
		[InlineData("<STX>2600000000PREKINUTO<FS>09<FS><ETX>K")]				// greška
		[InlineData("<STX>1000003211<ETX>!")]										// kratko zaglavlje
		[InlineData("<STX><STX>000000407296000<FS>100<FS><ETX>7")]					// zahtev
		public void Other_frames_are_ignored(string ecrLogAscii)
			=> Assert.Null(PosResponseFrame.TryParse(Frame(ecrLogAscii)));

		[Fact]
		public void Single_byte_is_ignored()
			=> Assert.Null(PosResponseFrame.TryParse([0x06]));
	}
}
