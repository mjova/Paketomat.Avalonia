using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Principal;
using TopPos.Agent.Contracts;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Minimalan klijent (uloga Agenta) za testove. v4: adresa je ime pipe-a (Windows) ili putanja Unix socket-a (Linux).
	/// </summary>
	internal sealed class TestAgentClient : IAsyncDisposable
	{
		public const string LockerId = "112";
		private static readonly TimeSpan __readTimeout = TimeSpan.FromSeconds(5);

		private ServerHello? _hello;

		private TestAgentClient(Stream pipe) => Pipe = pipe;

		public Stream Pipe { get; }

		/// <summary>
		/// v4: nova adresa kanala za test — ime pipe-a (Windows) ili putanja socket-a u privremenom folderu (Linux; folder
		/// pravi server, <c>0700</c>).
		/// </summary>
		public static string NewAddress() => OperatingSystem.IsWindows()
												? $"TopPos.Test.{Guid.NewGuid():N}"
												: Path.Combine(Path.GetTempPath(), "toppos-agent-tests", $"{Guid.NewGuid():N}"[..12], "agent.v1.sock");

		public static async Task<TestAgentClient> ConnectAsync(string address)
		{
			if (OperatingSystem.IsWindows()) {
				var pipe = new NamedPipeClientStream(".", address, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
				await pipe.ConnectAsync(5000);
				return new TestAgentClient(pipe);
			}
			var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
			try {
				using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
				await socket.ConnectAsync(new UnixDomainSocketEndPoint(address), cts.Token);
			}
			catch {
				socket.Dispose();
				throw;
			}
			return new TestAgentClient(new NetworkStream(socket, ownsSocket: true));
		}

		public async Task<ServerHello> HandshakeAsync()
		{
			await SendAsync(new ClientHello { AgentVersion = "test" });
			var body = await ReadFrameAsync() ?? throw new InvalidOperationException("Server je zatvorio vezu umesto Hello.");
			Assert.Equal(AgentMessageTypes.Hello, AgentJson.ReadEnvelope(body).Type);
			_hello = AgentJson.Deserialize<ServerHello>(body);
			return _hello;
		}

		public Task SendAsync<T>(T message) where T : AgentMessage => SendRawAsync(AgentJson.Serialize(message));

		public Task SendRawAsync(byte[] body) => AgentFrameCodec.WriteFrameAsync(Pipe, body, 2 * 1024 * 1024, CancellationToken.None);

		public Task<AgentResponse> RequestAsync(string command, string lockerId = LockerId, string? clientInstanceId = null, int protocolVersion = AgentProtocol.Version)
			=> RequestAsync(new AgentRequest {
				ProtocolVersion = protocolVersion,
				RequestId = Guid.NewGuid().ToString("N"),
				LockerId = lockerId,
				ClientInstanceId = clientInstanceId,
				Command = command,
			});

		/// <summary>Šalje zahtev i čeka njegov odgovor; događaji pre odgovora se preskaču.</summary>
		public async Task<AgentResponse> RequestAsync(AgentRequest request)
		{
			await SendAsync(request);
			while (true) {
				var body = await ReadFrameAsync() ?? throw new InvalidOperationException("Veza zatvorena pre odgovora.");
				if (AgentJson.ReadEnvelope(body).Type == AgentMessageTypes.Response) {
					var response = AgentJson.Deserialize<AgentResponse>(body);
					Assert.Equal(request.RequestId, response.RequestId);
					return response;
				}
			}
		}

		public async Task<AgentEvent> ReadEventAsync(Func<AgentEvent, bool>? match = null)
		{
			while (true) {
				var body = await ReadFrameAsync() ?? throw new InvalidOperationException("Veza zatvorena pre događaja.");
				if (AgentJson.ReadEnvelope(body).Type != AgentMessageTypes.Event) {
					continue;
				}
				var e = AgentJson.Deserialize<AgentEvent>(body);
				if (match == null || match(e)) {
					return e;
				}
			}
		}

		public async Task<byte[]?> ReadFrameAsync()
		{
			using var cts = new CancellationTokenSource(__readTimeout);
			try {
				return await AgentFrameCodec.ReadFrameAsync(Pipe, 2 * 1024 * 1024, cts.Token);
			}
			catch (IOException) {
				return null;   // server je prekinuo vezu
			}
		}

		/// <summary>Poruka ako stigne u roku, inače null (bez prekida veze).</summary>
		public async Task<byte[]?> TryReadAsync(TimeSpan timeout)
		{
			using var cts = new CancellationTokenSource(timeout);
			try {
				return await AgentFrameCodec.ReadFrameAsync(Pipe, 2 * 1024 * 1024, cts.Token);
			}
			catch (OperationCanceledException) {
				return null;
			}
		}

		public async ValueTask DisposeAsync() => await Pipe.DisposeAsync();
	}
}
