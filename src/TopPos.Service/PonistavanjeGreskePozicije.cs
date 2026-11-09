namespace TopPos.Service
{
	public class PonistavanjeGreskePozicije 
	{
		public string SifraBenzinskeStanice { get; set; } = default!;

		public int BrojPozicije { get; set; }
	}

	public class PonistavanjeGreskePozicijeResponse : ResponseBase
	{
		public int BrojPozicije { get; set; }
	}
}
