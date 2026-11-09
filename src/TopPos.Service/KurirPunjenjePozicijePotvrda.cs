namespace TopPos.Service
{
	public class KurirPunjenjePozicijePotvrda
	{
		public string SifraKurira { get; set; } = default!;

		public string SifraBenzinskeStanice { get; set; } = default!;

		public string Barkod { get; set; } = default!;

		public int BrojPozicije { get; set; }
	}

	public class KurirPunjenjePozicijePotvrdaResponse : ResponseBase
	{
		public int PopunjenBrojPozicije { get; set; }
	}
}
