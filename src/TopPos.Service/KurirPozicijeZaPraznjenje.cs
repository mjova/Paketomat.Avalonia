namespace TopPos.Service
{
	public class KurirPozicijeZaPraznjenje 
	{
		public string SifraKurira { get; set; } = default!;

		public string SifraBenzinskeStanice { get; set; } = default!;
	}

	public class KurirPozicijeZaPraznjenjeResponse : ResponseBase
	{
		public List<BarkodPozicija> ListaPaketa { get; set; } = default!;
	}
}
