using System.Net.Http.Headers;

namespace Flatsoft.Net.Http
{
	public class DynamicHttpAuthenticationHandler : DelegatingHandler
	{
		private readonly Func<Uri?, AuthenticationHeaderValue?> _getAuthHeader;
		public DynamicHttpAuthenticationHandler(Func<Uri?, AuthenticationHeaderValue?> getAuthHeader)
		{
			_getAuthHeader = getAuthHeader;
		}

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var authHeader = _getAuthHeader(request.RequestUri);
			if (authHeader != null) {
				request.Headers.Authorization = authHeader;
			}
			return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
		}
	}
}
