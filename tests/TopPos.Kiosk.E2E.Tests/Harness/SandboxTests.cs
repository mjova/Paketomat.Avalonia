using System.Text.Json.Nodes;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;

namespace TopPos.E2E.Tests.Harness
{
	/// <summary>
	/// Samoprovera harness-a (ne traži E2E okruženje): runtime stanje iz namerno zagađenog bin foldera ne sme
	/// preći u sandbox, a provera sandbox-a mora pasti ako ga ipak ima.
	/// </summary>
	public sealed class SandboxTests : IDisposable
	{
		private readonly string _root = Path.Combine(Path.GetTempPath(), "toppos-e2e-selftest", Guid.NewGuid().ToString("N"));

		[Fact]
		public void CopyBinaries_PollutedBin_RuntimeStateIsNotCopied()
		{
			var bin = Path.Combine(_root, "bin");
			Directory.CreateDirectory(Path.Combine(bin, "Logs"));
			Directory.CreateDirectory(Path.Combine(bin, "Log"));
			Directory.CreateDirectory(Path.Combine(bin, "Languages", "sr"));
			File.WriteAllText(Path.Combine(bin, "App.exe"), "exe");
			File.WriteAllText(Path.Combine(bin, "Languages", "sr", "lang.json"), "{}");
			File.WriteAllText(Path.Combine(bin, "Logs", "log-111-x-20261007.txt"), "stari log");
			File.WriteAllText(Path.Combine(bin, "Log", "std_log.log"), "ecr log");
			// Velika slova: isključivanje ne sme zavisiti od toga kako je fajl napisan.
			foreach (var name in Sandbox.ExcludedFiles) {
				File.WriteAllText(Path.Combine(bin, name.ToUpperInvariant()), "stanje");
			}

			var sandbox = Path.Combine(_root, "sandbox");
			Sandbox.CopyBinaries(bin, sandbox);

			Assert.True(File.Exists(Path.Combine(sandbox, "App.exe")));
			Assert.True(File.Exists(Path.Combine(sandbox, "Languages", "sr", "lang.json")));
			Assert.False(Directory.Exists(Path.Combine(sandbox, "Logs")));
			Assert.False(Directory.Exists(Path.Combine(sandbox, "Log")));
			Assert.DoesNotContain(Directory.EnumerateFiles(sandbox).Select(Path.GetFileName), name => Sandbox.ExcludedFiles.Contains(name!));
			Sandbox.AssertClean(sandbox);	// ne baca
		}

		[Fact]
		public void AssertClean_LeftoverState_Throws()
		{
			Directory.CreateDirectory(_root);
			File.WriteAllText(Path.Combine(_root, "PaymentConfirmation.json"), "[]");

			var ex = Assert.Throws<InvalidOperationException>(() => Sandbox.AssertClean(_root));
			Assert.Contains("PaymentConfirmation.json", ex.Message);
		}

		[Fact]
		public void WriteConfiguration_Configure_ChangesWrittenJsonAfterTokens()
		{
			Directory.CreateDirectory(_root);
			var target = Path.Combine(_root, "app-configuration.json");
			var tokens = new Dictionary<string, string> { ["LockerId"] = "111", ["CourierQrCode"] = "KURIR-E2E-1" };

			Sandbox.WriteConfiguration("server.app-configuration.json", target, tokens,
										c => c["CourierQrCodes"]!.AsArray().Add("QR-M1;4321"));

			var written = JsonNode.Parse(File.ReadAllText(target))!;
			Assert.Equal(["KURIR-E2E-1", "QR-M1;4321"], written["CourierQrCodes"]!.AsArray().Select(n => n!.GetValue<string>()));
			// v4: port servera nije token (0 = slobodan port, simulator ga javlja u READY redu)
			Assert.Equal("111", written["PaketomatId"]!.GetValue<string>());
		}

		public void Dispose()
		{
			if (Directory.Exists(_root)) {
				Directory.Delete(_root, recursive: true);
			}
		}
	}
}
