namespace TopPos.Service
{
	public class PrimaocPraznjenjePozicije
	{
		public string SifraBenzinskeStanice { get; set; } = default!;

		public string TelefonPrimaoca { get; set; } = default!;

		public int KodZaOtvaranje { get; set; }
	}

	public class PrimaocPraznjenjePozicijeResponse : ResponseBase
	{
		public decimal IznosZaUplatu { get; set; }

		public int BrojPozicije { get; set; }
	}
}
