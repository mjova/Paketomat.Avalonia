using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Harness
{
	/// <summary>
	/// Samoprovera brave sesije: neuspeo start (ovde izlaz testa koji baca) mora osloboditi session.lock,
	/// inače sledeća sesija čeka pet minuta. Traži E2E okruženje jer dira pravi sandbox koren.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class SessionLockTests
	{
		[SkippableFact]
		public async Task StartFails_OutputThrows_SessionLockIsReleased()
		{
			E2ESettings.RequireEnabled();

			await Assert.ThrowsAnyAsync<Exception>(() => KioskSession.StartAsync("selftest-lock", new ThrowingOutput()));

			var path = Path.Combine(E2ESettings.Current.SandboxRoot, "session.lock");
			using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);	// baca ako je brava ostala
		}

		private sealed class ThrowingOutput : ITestOutputHelper
		{
			public void WriteLine(string message) => throw new InvalidOperationException("izlaz testa nije dostupan");

			public void WriteLine(string format, params object[] args) => throw new InvalidOperationException("izlaz testa nije dostupan");
		}
	}
}
