namespace TopPos.LockerSimulator.Pos
{
	/// <summary>
	/// Završni odgovor 10 (POS2ECRMessage_10V3). Svi snimljeni odgovori imaju tačno 63 polja (62 FS), a ECR parser
	/// bez provere čita indekse do 26 — zato se odgovor uvek pravi kao pun niz polja.
	/// Indeksi ispod su indeksi iz POS2ECRMessage_10V3.fill (array[0] je prazan deo pre STX, array[1] zaglavlje).
	/// </summary>
	public static class PosResponses
	{
		public const string FlagApprovedWithAuthorization = "02";
		public const string FlagRefused = "04";
		public const string FlagTerminalId = "05";
		public const string FlagCommError = "06";
		public const string FlagApprovedWithoutAuthorization = "01";

		private const int FieldCount = 63;

		private const int IdxAmount = 2;
		private const int IdxAmountExponent = 4;
		private const int IdxCurrency = 5;
		private const int IdxCardDataSource = 6;
		private const int IdxCardNumber = 7;
		private const int IdxExpirationDate = 8;
		private const int IdxAuthorizationCode = 11;
		private const int IdxTid = 12;
		private const int IdxMid = 13;
		private const int IdxCompanyName = 14;
		private const int IdxDisplayMessage = 21;
		private const int IdxEmvData = 24;
		private const int IdxSignatureFlag = 25;
		private const int IdxMoreMessages = 27;
		private const int IdxFullResponseCode = 29;
		private const int IdxTransactionStatus = 30;
		private const int IdxCardholderName = 34;
		private const int IdxRetrievalReferenceNumber = 35;
		private const int IdxPinFlag = 36;
		private const int IdxAmount2 = 48;
		private const int IdxAcquirerId = 58;

		/// <summary>
		/// Izmišljeni EMV podaci u istom TLV obliku kao na pravom terminalu. ECR ih parsira u TransParamsV3.makeTicket
		/// (kad izvor kartice nije "M") i bez provere čita tag 9F12 — bez njega ECR baci NullReferenceException
		/// i ostavi port otvoren.
		/// </summary>
		private static string FakeEmvData(SimCard card)
		{
			string aid = card.Scheme switch {
				"MASTERCARD" => "A0000000041010",
				"DINACARD" => "A0000006021010",
				_ => "A0000000031010",
			};
			string label = Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(card.Scheme));
			return $"84{aid.Length / 2:X2}{aid}"
				 + "95050000008000"
				 + $"9F12{label.Length / 2:X2}{label}"
				 + "9F26080000000000000000"
				 + "9F270180"
				 + "9F3403420300";
		}

		public sealed record SaleResult(
			string SeqNumber,
			string Flag,
			long Amount,
			DateTime Time,
			SimCard? Card,
			string TransactionNumber,
			string AuthorizationCode,
			string Tid,
			string Mid,
			string DisplayText,
			string FullResponseCode,
			bool PinUsed,
			string TransactionType = PosRequest.TypeSale);

		public static byte[] BuildSaleResponse(SaleResult r)
		{
			string[] f = NewFields();
			f[1] = Header(r.SeqNumber, r.TransactionType, r.Flag, r.TransactionNumber, batch: "0001", r.Time);
			f[IdxAmount] = r.Amount.ToString("000000000000");
			f[IdxAmountExponent] = "+0";
			f[IdxCurrency] = "941";
			f[IdxMoreMessages] = "0";
			f[IdxAmount2] = "000000000000";
			f[IdxDisplayMessage] = PosFrame.CheckValue(r.DisplayText).PadRight(25);
			if (r.Card != null) {
				f[IdxCardDataSource] = "C";
				f[IdxCardNumber] = r.Card.MaskedPan;
				f[IdxExpirationDate] = "9999";
				f[IdxAuthorizationCode] = r.AuthorizationCode;
				f[IdxTid] = r.Tid;
				f[IdxMid] = r.Mid;
				f[IdxCompanyName] = r.Card.Scheme.PadRight(10);
				f[IdxEmvData] = FakeEmvData(r.Card);
				f[IdxSignatureFlag] = "1";
				f[IdxFullResponseCode] = r.FullResponseCode;
				f[IdxTransactionStatus] = "C1";
				f[IdxCardholderName] = r.Card.CardholderName;
				f[IdxRetrievalReferenceNumber] = r.Time.ToString("yyMMdd") + r.TransactionNumber;
				f[IdxPinFlag] = r.PinUsed ? "1" : "0";
				f[IdxAcquirerId] = "01";
			}
			return PosFrame.Build(string.Join(PosFrame.FsChar, f.Skip(1)));
		}

		/// <summary>Odgovor na tip 96 (Return TID): flag 05, TID u polju 12, ostalo prazno ili nule (kao u snimljenim logovima).</summary>
		public static byte[] BuildTidResponse(string seqNumber, string tid)
		{
			string[] f = NewFields();
			f[1] = $"100000{seqNumber}{PosRequest.TypeReturnTid}{FlagTerminalId}{new string('0', 22)}";	// transNo, batch, datum i vreme su nule
			f[IdxAmountExponent] = "+0";
			f[IdxCurrency] = "941";
			f[IdxTid] = PosFrame.CheckValue(tid);
			f[IdxMoreMessages] = "0";
			return PosFrame.Build(string.Join(PosFrame.FsChar, f.Skip(1)));
		}

		private static string[] NewFields()
		{
			var f = new string[FieldCount + 1];  // [0] = deo pre STX, ne šalje se
			Array.Fill(f, string.Empty);
			return f;
		}

		private static string Header(string seq, string type, string flag, string transactionNumber, string batch, DateTime time)
			=> $"100000{seq}{type}{flag}{transactionNumber}{batch}{time:ddMMyy}{time:HHmmss}";
	}
}
