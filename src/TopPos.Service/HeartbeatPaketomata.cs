namespace TopPos.Service
{ 
	public class HeartbeatPaketomata
	{
		public string SifraBenzinskeStanice { get; set; } = default!;
	}

	public class HeartbeatPaketomataResponse : ResponseBase
	{
		public int HeartbeatInterval { get; set; }
	}
}
