using System.Threading.Channels;

namespace TopPos.LockerSimulator.Pos
{
	/// <summary>Transport bajtova između simulatora i ECR-a (serijski port ili lažna "ECR strana" u testovima).</summary>
	public interface IPosWire
	{
		/// <summary>Okviri i kontrolni bajtovi koje je poslao ECR, već sklopljeni (<see cref="PosFrameAssembler"/>).</summary>
		ChannelReader<PosWireEvent> Incoming { get; }

		/// <summary>Završava se kad su bajtovi predati portu.</summary>
		ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
	}

	public sealed class PosSimulatorOptions
	{
		public string Pin { get; set; } = "1234";

		public string Tid { get; set; } = "SIMT0001";

		public string Mid { get; set; } = "1000001";

		/// <summary>Koliko terminal čeka karticu (i PIN) pre nego što sam pošalje PREKINUTO. U logovima 18–29 s.</summary>
		public TimeSpan CardWaitTimeout { get; set; } = TimeSpan.FromSeconds(30);

		/// <summary>Množilac pauza između poruka (1 = tajming iz snimljenih logova, 0 = bez pauza).</summary>
		public double SpeedFactor { get; set; } = 1.0;

		/// <summary>Koliko simulator čeka ACK od ECR-a na poslat okvir.</summary>
		public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(2);

		/// <summary>Posle koliko "auto kupac" prinese karticu / unese PIN.</summary>
		public TimeSpan AutoCustomerDelay { get; set; } = TimeSpan.FromSeconds(1.5);
	}

	public enum PosTerminalPhase
	{
		Idle,
		WaitingForCard,
		WaitingForPin,
		Processing,
		Frozen,
	}

	public sealed record PosTerminalStatus(PosTerminalPhase Phase, string DisplayText, long Amount, string? SeqNumber, string? TransactionType)
	{
		public static PosTerminalStatus Initial { get; } = new(PosTerminalPhase.Idle, "DOBRODOŠLI", 0, null, null);
	}

	public enum PosLogKind
	{
		EcrToPos,
		PosToEcr,
		Info,
		Warning,
	}

	public sealed record PosLogEntry(DateTime Time, PosLogKind Kind, string Text, string? Hex = null);

	public enum PosFault
	{
		/// <summary>Terminal ne odgovara na zahtev (nema ACK-a) — ECR posle WaitForAckTimeout zatvara port.</summary>
		NoResponse,
		/// <summary>Umesto ACK-a terminal pošalje 0x00, a na ponovljene zahteve ćuti (kao u logu 2025-12-17).</summary>
		WrongAckByte,
		/// <summary>Posle ACK-a terminal ništa ne šalje — ECR prijavi FunctionTimeout, kiosk ponavlja isti seq, pa šalje 11.</summary>
		FreezeAfterAck,
		/// <summary>Ceo tok sa karticom, pa 10/06 KOMUNIKACIONA GRESKA. Samo za prodaju.</summary>
		NoBankConnection,
		/// <summary>Naplata je odobrena, ali završni odgovor 10 se ne pošalje. Samo za prodaju.</summary>
		SwallowFinalResponse,
	}

	public enum Cancel11Response
	{
		/// <summary>26/06 POGRESAN UNOS (podrazumevano — pravi terminal tip 11 verovatno ne podržava; nepotvrđeno).</summary>
		Reject,
		/// <summary>10 sa tipom 11 i flagom 01 (nepotvrđeno) — proverava da kiosk to ne tumači kao naplatu.</summary>
		Accept,
	}

	/// <summary>Kvarovi "za N narednih zahteva". Bezbedno za pozive iz UI-a i iz simulatora.</summary>
	public sealed class PosFaultSchedule
	{
		private readonly object _lock = new();
		private readonly Dictionary<PosFault, int> _remaining = new();

		public event Action? Changed;

		public void Arm(PosFault fault, int count)
		{
			lock (_lock) {
				if (count <= 0) {
					_remaining.Remove(fault);
				}
				else {
					_remaining[fault] = count;
				}
			}
			Changed?.Invoke();
		}

		public bool TryConsume(PosFault fault)
		{
			lock (_lock) {
				if (!_remaining.TryGetValue(fault, out int n)) {
					return false;
				}
				if (n <= 1) {
					_remaining.Remove(fault);
				}
				else {
					_remaining[fault] = n - 1;
				}
			}
			Changed?.Invoke();
			return true;
		}

		public void Clear()
		{
			lock (_lock) {
				_remaining.Clear();
			}
			Changed?.Invoke();
		}

		public IReadOnlyDictionary<PosFault, int> Snapshot()
		{
			lock (_lock) {
				return new Dictionary<PosFault, int>(_remaining);
			}
		}
	}
}
