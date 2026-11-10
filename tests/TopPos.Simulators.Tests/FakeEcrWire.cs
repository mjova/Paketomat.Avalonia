using System.Text;
using System.Threading.Channels;
using TopPos.LockerSimulator.Pos;
using Xunit;

namespace TopPos.LockerSimulator.Tests
{
	/// <summary>
	/// Lažna "ECR strana": test šalje zahteve kao ECR, a sve što simulator pošalje ide u red za proveru.
	/// Kao pravi ECR, podrazumevano odmah potvrđuje (ACK) svaki okvir koji pošalje terminal.
	/// </summary>
	internal sealed class FakeEcrWire : IPosWire
	{
		private static readonly TimeSpan __defaultTimeout = TimeSpan.FromSeconds(5);

		private readonly Channel<PosWireEvent> _incoming = Channel.CreateUnbounded<PosWireEvent>();
		private readonly Channel<byte[]> _sent = Channel.CreateUnbounded<byte[]>();
		private readonly PosFrameAssembler _assembler = new();

		/// <summary>Šta ECR odgovara na sledeće okvire terminala (redom); kad se isprazni, šalje ACK.</summary>
		public Queue<byte?> ReplyOverrides { get; } = new();

		public ChannelReader<PosWireEvent> Incoming => _incoming.Reader;

		public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
		{
			byte[] bytes = data.ToArray();
			_sent.Writer.TryWrite(bytes);
			if (bytes.Length > 1 && bytes[0] == PosFrame.STX) {
				byte? reply = ReplyOverrides.Count > 0 ? ReplyOverrides.Dequeue() : PosFrame.ACK;
				if (reply.HasValue) {
					_incoming.Writer.TryWrite(PosWireEvent.Control(reply.Value));
				}
			}
			return ValueTask.CompletedTask;
		}

		public void SendFromEcr(byte[] data)
		{
			foreach (var ev in _assembler.Feed(data)) {
				_incoming.Writer.TryWrite(ev);
			}
		}

		public void SendCancelFromEcr()
		{
			byte[] body = Encoding.ASCII.GetBytes("23000000\u0003");
			SendFromEcr([PosFrame.STX, PosFrame.STX, .. body, PosFrame.Lrc(body)]);
		}

		public async Task<byte[]> NextAsync(TimeSpan? timeout = null)
		{
			using var cts = new CancellationTokenSource(timeout ?? __defaultTimeout);
			try {
				return await _sent.Reader.ReadAsync(cts.Token);
			}
			catch (OperationCanceledException) {
				throw new TimeoutException("Simulator nije ništa poslao.");
			}
		}

		/// <summary>Sledeći poslat okvir/bajt opisan kratko: "ACK", "25:51:PROVUCI/UBACI KARTICU", "26:09:PREKINUTO", "24:PREKINUTO", "10:01/02".</summary>
		public async Task<string> NextDescribedAsync(TimeSpan? timeout = null) => Describe(await NextAsync(timeout));

		public async Task ExpectAsync(params string[] expected)
		{
			foreach (string e in expected) {
				Assert.Equal(e, await NextDescribedAsync());
			}
		}

		public async Task ExpectNothingAsync(TimeSpan window)
		{
			await Task.Delay(window);
			Assert.False(_sent.Reader.TryRead(out var data), data == null ? null : $"Neočekivano poslato: {Describe(data)}");
		}

		public static string Describe(byte[] data)
		{
			if (data.Length == 1) {
				return data[0] switch { PosFrame.ACK => "ACK", PosFrame.NAK => "NAK", _ => $"0x{data[0]:X2}" };
			}
			string body = Encoding.ASCII.GetString(data, 1, data.Length - 3);
			string id = body[..2];
			string[] fields = body.Split(PosFrame.FsChar);
			return id switch {
				"25" or "26" => $"{id}:{fields[1]}:{fields[0][8..]}",
				"24" => $"24:{body[8..]}",
				"10" => $"10:{fields[0][10..12]}/{fields[0][12..14]}",
				_ => body,
			};
		}
	}
}
