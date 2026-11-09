namespace Flatsoft.Net.Http
{
	/// <summary>
	/// Zamena za legacy <c>System.Net.Http.HttpClientFactory.Create</c> (iz paketa Microsoft.AspNet.WebApi.Client).
	/// Taj paket je inače povlačio Newtonsoft.Json + System.Net.Http.Formatting, koje projekat ne koristi
	/// (JSON ide preko System.Text.Json), pa je uklonjen — a jedina korišćena funkcija je prebačena ovde.
	///
	/// Ulančava DelegatingHandler-e oko primarnog (inner) handler-a istim redosledom kao original:
	/// <c>handlers[0]</c> je spoljašnji, pa redom ka <paramref name="innerHandler"/>.
	/// </summary>
	public static class HttpClientPipeline
	{
		public static HttpClient Create(HttpMessageHandler innerHandler, params DelegatingHandler[] handlers)
			=> new HttpClient(CreatePipeline(innerHandler, handlers));

		public static HttpMessageHandler CreatePipeline(HttpMessageHandler innerHandler, IReadOnlyList<DelegatingHandler> handlers)
		{
			ArgumentNullException.ThrowIfNull(innerHandler);
			if (handlers is null || handlers.Count == 0)
				return innerHandler;

			HttpMessageHandler pipeline = innerHandler;
			// Idemo od kraja ka početku da bi handlers[0] ostao spoljašnji.
			for (int i = handlers.Count - 1; i >= 0; i--)
			{
				DelegatingHandler handler = handlers[i];
				ArgumentNullException.ThrowIfNull(handler);
				if (handler.InnerHandler is not null)
					throw new InvalidOperationException($"DelegatingHandler '{handler.GetType().Name}' već ima postavljen InnerHandler.");

				handler.InnerHandler = pipeline;
				pipeline = handler;
			}
			return pipeline;
		}
	}
}
