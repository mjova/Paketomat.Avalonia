using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// HTTP/1.1 server na slobodnom portu za testove <c>TopPosServiceClient</c>-a: na svaki zahtev vraća zadati odgovor
	/// (<see cref="Respond"/>) i zatvara vezu. Pamti zahteve (početna linija + telo) da test može da proveri šta je poslato.
	/// </summary>
	internal sealed class StubHttpServer : IDisposable
	{
		private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
		private readonly CancellationTokenSource _cts = new();
		private readonly List<(string RequestLine, string Body)> _requests = [];
		private readonly Lock _requestsLock = new();
		private int _droppedConnections;
		private volatile string? _response = "";

		public StubHttpServer()
		{
			_listener.Start();
			_ = Task.Run(AcceptLoop);
		}

		public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

		public string LastRequestLine => Requests.LastOrDefault().RequestLine ?? "";

		public IReadOnlyList<(string RequestLine, string Body)> Requests
		{
			get {
				lock (_requestsLock) {
					return [.. _requests];
				}
			}
		}

		public void Respond(HttpStatusCode status, string body, string contentType = "application/json")
			=> _response = Response(status, body, contentType);

		/// <summary>Odgovor po zahtevu (početna linija, telo), umesto jednog za sve (<see cref="Respond"/>).</summary>
		public Func<string, string, (HttpStatusCode Status, string Body)>? Handler { get; set; }

		/// <summary>Na sledećih <paramref name="count"/> zahteva zatvara vezu bez odgovora, pa odgovara po <see cref="Respond"/>.</summary>
		public void DropNextConnections(int count)
		{
			lock (_requestsLock) {
				_droppedConnections += count;
			}
		}

		/// <summary>Zahtev se primi, pa se veza zatvori bez odgovora (npr. pukla veza posle slanja).</summary>
		public void DropConnections() => _response = null;

		/// <summary>Kašnjenje pre odgovora (npr. duže od RequestTimeout-a klijenta).</summary>
		public TimeSpan ResponseDelay { get; set; }

		private static string Response(HttpStatusCode status, string body, string contentType)
			=> $"HTTP/1.1 {(int)status} {status}\r\nContent-Type: {contentType}; charset=utf-8\r\n" +
				$"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

		private async Task AcceptLoop()
		{
			while (!_cts.IsCancellationRequested) {
				TcpClient client;
				try {
					client = await _listener.AcceptTcpClientAsync(_cts.Token);
				}
				catch (Exception) {
					return;
				}
				using (client) {
					try {
						var stream = client.GetStream();
						var (header, body) = await ReadRequestAsync(stream);
						string? response;
						var requestLine = header.Split("\r\n")[0];
						lock (_requestsLock) {
							_requests.Add((requestLine, body));
							response = _droppedConnections > 0 ? null
										: Handler?.Invoke(requestLine, body) is { } handled ? Response(handled.Status, handled.Body, "application/json")
										: _response;
							_droppedConnections = Math.Max(0, _droppedConnections - 1);
						}
						if (ResponseDelay > TimeSpan.Zero) {
							await Task.Delay(ResponseDelay, _cts.Token);
						}
						if (response != null) {
							await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
						}
					}
					catch (Exception) {
						// klijent je prekinuo vezu (npr. RequestTimeout)
					}
				}
			}
		}

		// zaglavlje do praznog reda, pa telo po Content-Length
		private static async Task<(string Header, string Body)> ReadRequestAsync(NetworkStream stream)
		{
			var data = new List<byte>();
			var buffer = new byte[4096];
			int headerEnd;
			while ((headerEnd = IndexOfHeaderEnd(data)) < 0) {
				var read = await stream.ReadAsync(buffer);
				if (read == 0) {
					break;
				}
				data.AddRange(buffer.AsSpan(0, read).ToArray());
			}
			var header = Encoding.ASCII.GetString(data.ToArray(), 0, Math.Max(headerEnd, 0));
			var lengthLine = header.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
			var contentLength = lengthLine == null ? 0 : int.Parse(lengthLine["Content-Length:".Length..].Trim());
			while (headerEnd >= 0 && data.Count < headerEnd + 4 + contentLength) {
				var read = await stream.ReadAsync(buffer);
				if (read == 0) {
					break;
				}
				data.AddRange(buffer.AsSpan(0, read).ToArray());
			}
			var body = headerEnd < 0 ? "" : Encoding.UTF8.GetString(data.ToArray(), headerEnd + 4, Math.Min(contentLength, data.Count - headerEnd - 4));
			return (header, body);
		}

		private static int IndexOfHeaderEnd(List<byte> data)
		{
			for (int i = 0; i + 3 < data.Count; i++) {
				if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n') {
					return i;
				}
			}
			return -1;
		}

		public void Dispose()
		{
			_cts.Cancel();
			_listener.Stop();
			_cts.Dispose();
		}
	}
}
