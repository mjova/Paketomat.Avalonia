using System.Net;

namespace TopPos.Service
{
	/// <summary>
	/// Beleži na zahtevu da li ga je server možda obradio, za svaki pokušaj posebno. Stoji ispod Polly-ja, koji pri ponavljanju
	/// šalje isti <see cref="HttpRequestMessage"/>, pa oznaka ostaje i kad poslednji pokušaj dobije odgovor koji sam po sebi
	/// znači „nije obrađeno" (npr. 503 posle pokušaja kome je veza pukla). Čita se preko <see cref="MayHaveBeenProcessed(HttpRequestMessage)"/>.
	/// </summary>
	internal sealed class ProcessingTrackingHandler : DelegatingHandler
	{
		private static readonly HttpRequestOptionsKey<bool> __mayHaveBeenProcessedKey = new("TopPos.MayHaveBeenProcessed");

		public static bool MayHaveBeenProcessed(HttpRequestMessage request)
			=> request.Options.TryGetValue(__mayHaveBeenProcessedKey, out var value) && value;

		/// <summary>
		/// Statusi koji znače da zahtev nije obrađen: autentifikacija, nepostojeća ruta, odbijanje zbog opterećenja ili isteka
		/// čekanja na serveru. Svi ostali (200, 500, 502, 504...) mogli su doći posle obrade — 502/504 vraća i proxy kad
		/// server obradi zahtev, a odgovor ne stigne do njega na vreme.
		/// </summary>
		public static bool MayHaveBeenProcessed(HttpStatusCode statusCode)
			=> statusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
									or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable);

		// Zahtev bez tela: greške pre slanja su ime servera, uspostavljanje veze i TLS; sve ostalo se računa kao „možda".
		private static bool IsNotSent(Exception ex)
			=> ex is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError };

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			// Telo se šalje tek kad je veza uspostavljena i zaglavlje poslato: dok slanje tela nije počelo, server nema ceo zahtev.
			// Tako se i istek roka dok se veza uspostavlja (npr. nema interneta, a paketi se gube) razlikuje od isteka posle slanja.
			var content = request.Content;
			var tracked = content is null ? null : new SendTrackingContent(content);
			if (tracked != null) {
				request.Content = tracked;
			}
			try {
				var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
				if (MayHaveBeenProcessed(response.StatusCode)) {
					request.Options.Set(__mayHaveBeenProcessedKey, true);
				}
				return response;
			}
			catch (Exception ex) when (tracked?.SendingStarted ?? !IsNotSent(ex)) {
				request.Options.Set(__mayHaveBeenProcessedKey, true);
				throw;
			}
			finally {
				// Polly sledeći pokušaj šalje sa originalnim telom (i ponovo ga omota); omotač ne oslobađa original.
				if (tracked != null) {
					request.Content = content;
				}
			}
		}

		/// <summary>Prosleđuje telo zahteva i beleži da je slanje počelo (SocketsHttpHandler ga piše u uspostavljenu vezu).</summary>
		private sealed class SendTrackingContent : HttpContent
		{
			private readonly HttpContent _inner;

			public SendTrackingContent(HttpContent inner)
			{
				_inner = inner;
				foreach (var header in inner.Headers) {
					Headers.TryAddWithoutValidation(header.Key, header.Value);
				}
			}

			public bool SendingStarted { get; private set; }

			protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
				=> SerializeToStreamAsync(stream, context, CancellationToken.None);

			protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
			{
				SendingStarted = true;
				return _inner.CopyToAsync(stream, context, cancellationToken);
			}

			protected override bool TryComputeLength(out long length)
			{
				var contentLength = _inner.Headers.ContentLength;
				length = contentLength ?? 0;
				return contentLength.HasValue;
			}
		}
	}
}
