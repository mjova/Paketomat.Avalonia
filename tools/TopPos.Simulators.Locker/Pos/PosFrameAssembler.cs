namespace TopPos.LockerSimulator.Pos
{
	public enum PosWireEventKind
	{
		/// <summary>Kompletan okvir STX…ETX + LRC.</summary>
		Frame,
		/// <summary>Samostalan bajt van okvira (ACK, NAK ili nešto neočekivano).</summary>
		Control,
		/// <summary>Okvir je odbačen (predugačak).</summary>
		Garbage,
	}

	public readonly record struct PosWireEvent(PosWireEventKind Kind, byte[] Data)
	{
		public static PosWireEvent Frame(byte[] data) => new(PosWireEventKind.Frame, data);
		public static PosWireEvent Control(byte b) => new(PosWireEventKind.Control, [b]);
		public static PosWireEvent Garbage(byte[] data) => new(PosWireEventKind.Garbage, data);

		public bool IsAck => Kind == PosWireEventKind.Control && Data[0] == PosFrame.ACK;
		public bool IsNak => Kind == PosWireEventKind.Control && Data[0] == PosFrame.NAK;
	}

	/// <summary>
	/// Sklapa okvire iz toka bajtova, isto kao CommunicationPortV3.readMessage: od STX do prvog ETX, pa još jedan bajt (LRC).
	/// LRC može biti bilo koji bajt (i 0x02/0x03/0x06), zato se posle ETX uvek uzima tačno jedan bajt.
	/// </summary>
	public sealed class PosFrameAssembler
	{
		public const int MaxFrameLength = 4096;

		private enum State { Idle, InFrame, AwaitingLrc }

		private readonly List<byte> _buffer = new();
		private State _state = State.Idle;

		public IEnumerable<PosWireEvent> Feed(ReadOnlyMemory<byte> data)
		{
			var events = new List<PosWireEvent>();
			foreach (byte b in data.Span) {
				switch (_state) {
					case State.Idle:
						if (b == PosFrame.STX) {
							_buffer.Clear();
							_buffer.Add(b);
							_state = State.InFrame;
						}
						else {
							events.Add(PosWireEvent.Control(b));
						}
						break;

					case State.InFrame:
						_buffer.Add(b);
						if (b == PosFrame.ETX) {
							_state = State.AwaitingLrc;
						}
						else if (_buffer.Count >= MaxFrameLength) {
							events.Add(PosWireEvent.Garbage(_buffer.ToArray()));
							_buffer.Clear();
							_state = State.Idle;
						}
						break;

					case State.AwaitingLrc:
						_buffer.Add(b);
						events.Add(PosWireEvent.Frame(_buffer.ToArray()));
						_buffer.Clear();
						_state = State.Idle;
						break;
				}
			}
			return events;
		}

		/// <summary>Odbacuje započet okvir (npr. kad se port zatvori).</summary>
		public void Reset()
		{
			_buffer.Clear();
			_state = State.Idle;
		}
	}
}
