using System.Buffers.Binary;
using System.IO;
using System.Text;
using TopPos.Agent.Contracts;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Frame kanala Agent↔kiosk (4 B dužina + UTF-8 JSON): dužina se proverava pre alokacije, frame se čita do kraja i kad
	/// stiže po bajt, a prekid ili zastoj usred frame-a je greška protokola (plan §4, zahtev 7).
	/// </summary>
	public class AgentFrameCodecTests
	{
		[Fact]
		public async Task Frame_round_trips_even_when_stream_returns_one_byte_at_a_time()
		{
			var body = AgentJson.Serialize(new ClientHello { AgentVersion = "1.3.1" });
			var buffer = new MemoryStream();
			await AgentFrameCodec.WriteFrameAsync(buffer, body, AgentProtocol.MaxMessageBytes, CancellationToken.None);

			var read = await AgentFrameCodec.ReadFrameAsync(new TrickleStream(buffer.ToArray()), AgentProtocol.MaxMessageBytes, CancellationToken.None);

			Assert.Equal(body, read);
			var hello = AgentJson.Deserialize<ClientHello>(read!);
			Assert.Equal("1.3.1", hello.AgentVersion);
			Assert.Equal(AgentMessageTypes.ClientHello, hello.Type);
		}

		[Fact]
		public async Task Clean_end_of_stream_before_header_returns_null()
		{
			Assert.Null(await AgentFrameCodec.ReadFrameAsync(new MemoryStream(), 1024, CancellationToken.None));
		}

		[Theory]
		[InlineData(0)]
		[InlineData(-1)]
		[InlineData(1025)]
		[InlineData(int.MaxValue)]
		public async Task Length_out_of_range_is_rejected_before_allocation(int length)
		{
			var header = new byte[4];
			BinaryPrimitives.WriteInt32LittleEndian(header, length);

			var ex = await Assert.ThrowsAsync<AgentProtocolException>(() => AgentFrameCodec.ReadFrameAsync(new MemoryStream(header), 1024, CancellationToken.None));
			Assert.Contains("van dozvoljenog opsega", ex.Message);
		}

		[Theory]
		[InlineData(2, "zaglavlja")]
		[InlineData(4 + 3, "usred poruke")]
		public async Task Truncated_frame_is_a_protocol_error(int available, string expected)
		{
			var frame = new byte[4 + 10];
			BinaryPrimitives.WriteInt32LittleEndian(frame, 10);

			var ex = await Assert.ThrowsAsync<AgentProtocolException>(() => AgentFrameCodec.ReadFrameAsync(new MemoryStream(frame[..available]), 1024, CancellationToken.None));
			Assert.Contains(expected, ex.Message);
		}

		[Fact]
		public async Task Stalled_frame_after_first_byte_times_out_as_protocol_error()
		{
			var stream = new StallingStream([0x0A]);   // prvi bajt zaglavlja, pa ništa

			var ex = await Assert.ThrowsAsync<AgentProtocolException>(() =>
						AgentFrameCodec.ReadFrameAsync(stream, 1024, CancellationToken.None, TimeSpan.FromMilliseconds(200)));
			Assert.Contains("nije stigao u roku", ex.Message);
		}

		[Fact]
		public async Task Writing_empty_or_oversized_body_is_refused()
		{
			await Assert.ThrowsAsync<AgentProtocolException>(() => AgentFrameCodec.WriteFrameAsync(new MemoryStream(), Array.Empty<byte>(), 1024, CancellationToken.None));
			await Assert.ThrowsAsync<AgentProtocolException>(() => AgentFrameCodec.WriteFrameAsync(new MemoryStream(), new byte[1025], 1024, CancellationToken.None));
		}

		[Theory]
		[InlineData("[]")]
		[InlineData("""{ "protocolVersion": 1 }""")]
		[InlineData("""{ "type": "Request" }""")]
		[InlineData("""{ "type": 5, "protocolVersion": 1 }""")]
		[InlineData("""{ "type": "Request", "protocolVersion": "1" }""")]
		[InlineData("""{ "type": "Request", "protocolVersion": 1, }""")]
		[InlineData("""{ "type": "Request", /* x */ "protocolVersion": 1 }""")]
		[InlineData("nije json")]
		public void Invalid_envelope_is_a_protocol_error(string json)
		{
			Assert.Throws<AgentProtocolException>(() => AgentJson.ReadEnvelope(Encoding.UTF8.GetBytes(json)));
		}

		[Fact]
		public void Json_deeper_than_limit_is_rejected()
		{
			var deep = new StringBuilder("""{ "type": "Request", "protocolVersion": 1, "payload": """);
			for (int i = 0; i < 20; i++) deep.Append("""{ "a": """);
			deep.Append('1');
			for (int i = 0; i < 20; i++) deep.Append('}');
			deep.Append('}');

			Assert.Throws<AgentProtocolException>(() => AgentJson.ReadEnvelope(Encoding.UTF8.GetBytes(deep.ToString())));
		}

		[Fact]
		public void Messages_use_camel_case_and_round_trip_status()
		{
			var response = new AgentResponse {
				RequestId = "r1", LockerId = "112", ClientInstanceId = "i1", Command = AgentCommands.GetStatus,
				ResultCode = AgentResultCodes.Completed,
				Status = new KioskStatus { StatusRevision = 7, Busy = true, Role = "Courier", Operational = null, Lifecycle = KioskLifecycles.Ready },
			};
			var bytes = AgentJson.Serialize(response);
			var json = Encoding.UTF8.GetString(bytes);

			Assert.Contains("\"resultCode\":\"Completed\"", json);
			Assert.Contains("\"statusRevision\":7", json);
			var (type, version) = AgentJson.ReadEnvelope(bytes);
			Assert.Equal(AgentMessageTypes.Response, type);
			Assert.Equal(AgentProtocol.Version, version);
			var back = AgentJson.Deserialize<AgentResponse>(bytes);
			Assert.Equal(7, back.Status!.StatusRevision);
			Assert.Null(back.Status.Operational);
		}

		/// <summary>Vraća najviše jedan bajt po čitanju.</summary>
		private sealed class TrickleStream(byte[] data) : MemoryStream(data)
		{
			public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
				=> base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
		}

		/// <summary>Vrati zadate bajtove, pa čeka zauvek (do otkazivanja).</summary>
		private sealed class StallingStream(byte[] first) : Stream
		{
			private bool _sent;

			public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
			{
				if (!_sent) {
					_sent = true;
					first.CopyTo(buffer);
					return first.Length;
				}
				await Task.Delay(Timeout.Infinite, cancellationToken);
				return 0;
			}

			public override bool CanRead => true;
			public override bool CanSeek => false;
			public override bool CanWrite => false;
			public override long Length => throw new NotSupportedException();
			public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
			public override void Flush() { }
			public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		}
	}
}
