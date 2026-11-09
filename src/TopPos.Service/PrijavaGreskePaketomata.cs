using System.ComponentModel;

namespace TopPos.Service
{
	public enum VrstaGreskePaketomat
	{
		[Description("Greška COM porta")]
		C,
		[Description("Nema konekcije")]
		K
	}

	public class PrijavaGreskePaketomata 
	{
		public string SifraBenzinskeStanice { get; set; } = default!;

		public VrstaGreskePaketomat Greska { get; set; }

		public DateTime? DatumVremeGreske { get; set; }
	}

	public class PrijavaGreskePaketomataResponse : ResponseBase
	{
		public string SifraBenzinskeStanice { get; set; } = default!;
	}
}
