

namespace TopPos.Service
{
	public class PrijavaGreskePozicije
	{
		public const string DoorOpen = "O";		// Open, but should have been closed
		public const string DoorClosed = "N";	// Closed ([N]ot open), but should have been open
		public const string DoorStatusUnknown = "K";	// Unknown (controller issue)

		public static readonly string[] SupportedErrorCodes = [DoorOpen, DoorClosed, DoorStatusUnknown];

		public string SifraBenzinskeStanice { get; set; } = default!;

		public int BrojPozicije { get; set; }

		public string GreskaPozicije { get; set; } = default!;
	}

	public class PrijavaGreskePozicijeResponse : ResponseBase
	{
		public int BrojPozicije { get; set; }
	}
}
