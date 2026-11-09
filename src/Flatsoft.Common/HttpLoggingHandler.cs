
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Flatsoft.Net.Http
{
    public class HttpLoggingHandler : DelegatingHandler
	{
		private readonly ILogger? _logger;

		public HttpLoggingHandler(ILogger? logger)
			=> _logger = logger;

        public Predicate<HttpRequestMessage>? LogFilter { get; set; }

        public IEnumerable<string> ExcludeHeaders { get; set; } = ["Authorization"];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
		{
			if ((LogFilter?.Invoke(request) ?? true) && _logger?.IsEnabled(LogLevel.Debug) == true) {
				var messageDump = await request.DumpRequest(ExcludeHeaders, indent: "  ").ConfigureAwait(false);
				_logger.LogDebug($"{nameof(HttpLoggingHandler)}: HTTP request" + messageDump);
			}
#if DEBUG
			await request.DebugDump();
#endif			
			var response = await base.SendAsync(request, token).ConfigureAwait(false);

			if ((LogFilter?.Invoke(request) ?? true) && _logger?.IsEnabled(LogLevel.Debug) == true) {
				var messageDump = await response.DumpResponse(indent: "  ").ConfigureAwait(false);
				_logger.LogDebug($"{nameof(HttpLoggingHandler)}: HTTP response" + messageDump);
			}

			return response;
		}
	}

	public static class LoggingHelpers
	{
		public static async ValueTask DebugDump(this HttpRequestMessage request)
		{
			Debug.WriteLine("----- HTTP Request -----");
			Debug.WriteLine($"{request.Method} {request.RequestUri} HTTP/{request.Version}");
        
			foreach (var header in request.Headers)
			{
				Debug.WriteLine($"{header.Key}: {string.Join(", ", header.Value)}");
			}

			if (request.Content != null)
			{
				foreach (var header in request.Content.Headers)
				{
					Debug.WriteLine($"{header.Key}: {string.Join(", ", header.Value)}");
				}
				Debug.WriteLine(string.Empty);

				string requestBody = await request.Content.ReadAsStringAsync();
				Debug.WriteLine(requestBody);
			}
			Debug.WriteLine("-----------------------");
		}

		public static async ValueTask<string> DumpRequest(this HttpRequestMessage message, IEnumerable<string>? excludeHeaders, string indent)
		{
			if (message is null) {
				return string.Empty;
			}

			var sb = new StringBuilder()
				.AppendLine()
				.Append(indent).Append($"URL: {message.Method} {message.RequestUri}")
				.AppendLine();

			foreach (var header in message.Headers) {
				if (excludeHeaders is not null && excludeHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase)) {
					continue;
				}
				sb.Append(indent).AppendLine($"HEADER: {header.Key}: {string.Join(";", header.Value)}");
			}

			if (message.Content is not null) {
				sb.Append(indent).Append("CONTENT: ").Append(await message.Content.ReadAsStringAsync().ConfigureAwait(false));
			}
			else {
				sb.Append(indent).Append("CONTENT: <no content>");
			}

			return sb.ToString();
		}

		public static async ValueTask<string> DumpResponse(this HttpResponseMessage message, string indent)
		{
			if (message is null) {
				return string.Empty;
			}

			var sb = new StringBuilder().AppendLine();

			// Load into a buffer so that downstream consumers can re-read the body
			await message.Content.LoadIntoBufferAsync().ConfigureAwait(false);

			foreach (var header in message.Headers) {
				sb.Append(indent).AppendLine($"HEADER: {header.Key}: {string.Join(";", header.Value)}");
            }

			if (message.Content is not null) {
				sb.Append(indent).Append("CONTENT: ").Append(await message.Content.ReadAsStringAsync().ConfigureAwait(false));
			}
			else {
				sb.Append(indent).Append("CONTENT: <no content>");
			}

			return sb.ToString();
		}
	}
}
