namespace TopPos.Service
{
	public class PrimaocPraznjenjePozicijePotvrda
	{
		public string SifraBenzinskeStanice { get; set; } = default!;

		public string TelefonPrimaoca { get; set; } = default!;

		public int KodZaOtvaranje { get; set; }

		public int BrojPozicije { get; set; }
	}

	public class PrimaocPraznjenjePozicijePotvrdaResponse : ResponseBase
	{
		public int BrojPozicije { get; set; }
	}
}
