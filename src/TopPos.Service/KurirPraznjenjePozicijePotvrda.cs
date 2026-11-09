namespace TopPos.Service
{
	public class KurirPraznjenjePozicijePotvrda 
	{
		public string SifraKurira { get; set; } = default!;

		public string SifraBenzinskeStanice { get; set; } = default!;

		public string Barkod { get; set; } = default!;

		public int BrojPozicije { get; set; }
	}

	public class KurirPraznjenjePozicijePotvrdaResponse : ResponseBase
	{
		public long IspraznjenBrojPozicije { get; set; }
	}
}
