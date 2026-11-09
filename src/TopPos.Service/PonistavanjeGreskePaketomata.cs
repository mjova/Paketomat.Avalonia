
namespace TopPos.Service
{
	public class PonistavanjeGreskePaketomata
	{
		public string SifraBenzinskeStanice { get; set; } = default!;
	}

	public class PonistavanjeGreskePaketomataResponse : ResponseBase
	{
		public string SifraBenzinskeStanice { get; set; } = default!;
	}
}
