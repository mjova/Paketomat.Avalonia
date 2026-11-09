using System.Buffers.Binary;
using System.Text.Json;

namespace TopPos.Agent.Contracts
{
	/// <summary>Kršenje protokola (neispravan frame ili JSON). Veza se posle njega zatvara.</summary>
	public class AgentProtocolException : Exception
	{
		public AgentProtocolException(string message) : base(message) { }

		public AgentProtocolException(string message, Exception innerException) : base(message, innerException) { }
	}

	/// <summary>
	/// Frame: 4 bajta dužine tela (little-endian, bez znaka) + telo (UTF-8 JSON). Dužina se proverava pre alokacije, a telo
	/// se čita do kraja — jedan <c>ReadAsync</c> ne mora da vrati ceo frame.
	/// </summary>
	public static class AgentFrameCodec
	{
		public const int HeaderBytes = 4;

		/// <summary>Upisuje jedan frame jednim pozivom <c>WriteAsync</c> (zaglavlje i telo zajedno) pa radi <c>FlushAsync</c>.</summary>
		public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> body, int maxBodyBytes, CancellationToken cancellationToken)
		{
			if (body.Length == 0 || body.Length > maxBodyBytes) {
				throw new AgentProtocolException($"Dužina poruke {body.Length} B je van dozvoljenog opsega 1..{maxBodyBytes} B.");
			}
			var frame = new byte[HeaderBytes + body.Length];
			BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
			body.CopyTo(frame.AsMemory(HeaderBytes));
			await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
			await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Čita jedan frame. Vraća <c>null</c> kad druga strana uredno zatvori vezu pre prvog bajta zaglavlja; prekid usred
		/// frame-a i dužina van opsega bacaju <see cref="AgentProtocolException"/>. Čekanje na prvi bajt nije ograničeno (veza
		/// sme da miruje), ali kad frame počne, ostatak mora stići u roku <paramref name="frameTimeout"/> (ako je zadat) —
		/// inače <see cref="AgentProtocolException"/>.
		/// </summary>
		public static async Task<byte[]?> ReadFrameAsync(Stream stream, int maxBodyBytes, CancellationToken cancellationToken, TimeSpan? frameTimeout = null)
		{
			var header = new byte[HeaderBytes];
			int headerRead = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);
			if (headerRead == 0) {
				return null;
			}

			using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			if (frameTimeout is { } timeout) {
				frameCts.CancelAfter(timeout);
			}
			try {
				headerRead += await ReadAtLeastAsync(stream, header.AsMemory(headerRead), frameCts.Token).ConfigureAwait(false);
				if (headerRead < HeaderBytes) {
					throw new AgentProtocolException($"Veza je prekinuta usred zaglavlja ({headerRead}/{HeaderBytes} B).");
				}
				int length = BinaryPrimitives.ReadInt32LittleEndian(header);
				if (length <= 0 || length > maxBodyBytes) {
					throw new AgentProtocolException($"Dužina poruke {length} B je van dozvoljenog opsega 1..{maxBodyBytes} B.");
				}
				var body = new byte[length];
				int bodyRead = await ReadAtLeastAsync(stream, body, frameCts.Token).ConfigureAwait(false);
				if (bodyRead < length) {
					throw new AgentProtocolException($"Veza je prekinuta usred poruke ({bodyRead}/{length} B).");
				}
				return body;
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
				throw new AgentProtocolException($"Ostatak poruke nije stigao u roku od {frameTimeout}.");
			}
		}

		// Čita dok bafer nije pun ili dok druga strana ne zatvori vezu; vraća broj pročitanih bajtova.
		private static async Task<int> ReadAtLeastAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
		{
			int total = 0;
			while (total < buffer.Length) {
				int read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
				if (read == 0) {
					break;
				}
				total += read;
			}
			return total;
		}
	}

	/// <summary>JSON pravila kanala: camelCase, ograničena dubina, bez komentara i zareza na kraju.</summary>
	public static class AgentJson
	{
		public static readonly JsonSerializerOptions Options = new() {
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			PropertyNameCaseInsensitive = false,
			MaxDepth = AgentProtocol.MaxJsonDepth,
			ReadCommentHandling = JsonCommentHandling.Disallow,
			AllowTrailingCommas = false,
		};

		private static readonly JsonDocumentOptions DocumentOptions = new() {
			MaxDepth = AgentProtocol.MaxJsonDepth,
			CommentHandling = JsonCommentHandling.Disallow,
			AllowTrailingCommas = false,
		};

		public static byte[] Serialize<T>(T message) where T : AgentMessage
			=> JsonSerializer.SerializeToUtf8Bytes(message, Options);

		/// <summary>Rezultat komande za <see cref="AgentResponse.Result"/>.</summary>
		public static JsonElement ToResult<T>(T result) where T : class
			=> JsonSerializer.SerializeToElement(result, Options);

		/// <summary>Čita <see cref="AgentResponse.Result"/> kao zadati tip; <c>null</c> kad rezultata nema. Neispravan sadržaj baca <see cref="AgentProtocolException"/>.</summary>
		public static T? ReadResult<T>(AgentResponse response) where T : class
		{
			if (response.Result is not { } result || result.ValueKind == JsonValueKind.Null) {
				return null;
			}
			try {
				return result.Deserialize<T>(Options);
			}
			catch (JsonException ex) {
				throw new AgentProtocolException($"Rezultat nije ispravan ({typeof(T).Name}): {ex.Message}", ex);
			}
		}

		/// <summary>
		/// Čita zajednička polja (<c>type</c>, <c>protocolVersion</c>) bez vezivanja za konkretan tip. Neispravan JSON, ne-objekat
		/// ili polja pogrešnog tipa bacaju <see cref="AgentProtocolException"/>.
		/// </summary>
		public static (string Type, int ProtocolVersion) ReadEnvelope(ReadOnlyMemory<byte> body)
		{
			try {
				using var document = JsonDocument.Parse(body, DocumentOptions);
				var root = document.RootElement;
				if (root.ValueKind != JsonValueKind.Object) {
					throw new AgentProtocolException($"Poruka nije JSON objekat ({root.ValueKind}).");
				}
				if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) {
					throw new AgentProtocolException("Poruka nema polje type.");
				}
				if (!root.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.Number
					|| !version.TryGetInt32(out int protocolVersion)) {
					throw new AgentProtocolException("Poruka nema ispravno polje protocolVersion.");
				}
				return (type.GetString()!, protocolVersion);
			}
			catch (JsonException ex) {
				throw new AgentProtocolException($"Poruka nije ispravan JSON: {ex.Message}", ex);
			}
		}

		/// <summary>Deserijalizuje telo u konkretan tip; neispravan sadržaj baca <see cref="AgentProtocolException"/>.</summary>
		public static T Deserialize<T>(ReadOnlyMemory<byte> body) where T : AgentMessage
		{
			try {
				return JsonSerializer.Deserialize<T>(body.Span, Options)
						?? throw new AgentProtocolException("Poruka je null.");
			}
			catch (JsonException ex) {
				throw new AgentProtocolException($"Poruka nije ispravna ({typeof(T).Name}): {ex.Message}", ex);
			}
		}
	}
}
