namespace TopPos.Service
{
	public class PotvrdaPlacanja
	{
		public string SifraBenzinskeStanice { get; set; } = default!;
		public int PosId { get; set; } = default!;
        public string KodZaOtvaranje { get; set; } = default!;
		public string PosTransakcijaId { get; set; } = default!;
		public int BrojRacuna { get; set; }
		public decimal IznosGotovina { get; set; }
		public decimal IznosKartice { get; set; }
	}

	public class PotvrdaPlacanjaResponse: ResponseBase
	{
		public int EvidencioniBrojUplate { get; set; }
	}
}
