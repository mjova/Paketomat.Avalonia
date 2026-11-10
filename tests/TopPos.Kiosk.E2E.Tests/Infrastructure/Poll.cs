namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>
	/// Čekanje uslova sa timeout-om (umesto Sleep-ova). Pri isteku baca <see cref="E2ETimeoutException"/> sa opisom.
	/// <c>guard</c> se poziva pre svake provere — npr. da čekanje pukne odmah ako je proces klijenta ugašen.
	/// </summary>
	public static class Poll
	{
		public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

		public static async Task<T> UntilAsync<T>(Func<Task<T?>> probe, TimeSpan timeout, string description, Action? guard = null) where T : class
		{
			var deadline = DateTime.UtcNow + timeout;
			Exception? lastError = null;
			while (true) {
				guard?.Invoke();
				try {
					var value = await probe().ConfigureAwait(false);
					if (value != null) {
						return value;
					}
				}
				catch (Exception ex) when (ex is not E2EException) {
					lastError = ex;
				}
				if (DateTime.UtcNow >= deadline) {
					throw new E2ETimeoutException($"Isteklo {timeout.TotalSeconds:0.#} s: {description}"
													+ (lastError != null ? $" (poslednja greška: {lastError.Message})" : string.Empty));
				}
				await Task.Delay(Interval).ConfigureAwait(false);
			}
		}

		public static Task<T> UntilAsync<T>(Func<T?> probe, TimeSpan timeout, string description, Action? guard = null) where T : class
			=> UntilAsync(() => Task.FromResult(probe()), timeout, description, guard);

		public static Task UntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string description, Action? guard = null)
			=> UntilAsync<object>(async () => await condition().ConfigureAwait(false) ? true : null, timeout, description, guard);

		public static Task UntilAsync(Func<bool> condition, TimeSpan timeout, string description, Action? guard = null)
			=> UntilAsync(() => Task.FromResult(condition()), timeout, description, guard);
	}

	public class E2EException(string message) : Exception(message);

	public sealed class E2ETimeoutException(string message) : E2EException(message);
}
