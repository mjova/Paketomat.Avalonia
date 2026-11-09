namespace TopPos.Service
{
	public class KurirAutorizacija
	{
		public string QrKod { get; set; } = default!;

		public string SifraBenzinskeStanice { get; set; } = default!;
	}

	public class KurirAutorizacijaResponse : ResponseBase
	{
		public string SifraKurira { get; set; } = default!;
	}
}
