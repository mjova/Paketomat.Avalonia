namespace TopPos.Service
{
	public class KurirPunjenjePozicije
	{
		public string SifraKurira { get; set; } = default!;

		public string SifraBenzinskeStanice { get; set; } = default!;

		public string Barkod { get; set; } = default!;
	}

	public class KurirPunjenjePozicijeResponse : ResponseBase
	{
		public int BrojPozicije { get; set; }
	}
}
