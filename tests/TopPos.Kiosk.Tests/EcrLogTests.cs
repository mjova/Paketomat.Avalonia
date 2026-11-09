using System.Reflection;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// v4: ECR log ide u <c>Configuration.logDirectory</c>, a greška upisa ne sme da prekine transakciju (Codex nalaz 9.11.2026).
	/// <c>EcrLog</c> je interni tip biblioteke iz v2 (bez <c>InternalsVisibleTo</c>), pa ga test poziva refleksijom. Testovi su u
	/// jednoj klasi jer menjaju statičko polje (xUnit ih tada ne pušta paralelno).
	/// </summary>
	public class EcrLogTests : IDisposable
	{
		private readonly string _originalDirectory = ECR.Configuration.logDirectory;
		private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("toppos-ecrlog-");

		public void Dispose()
		{
			ECR.Configuration.logDirectory = _originalDirectory;
			_dir.Delete(recursive: true);
		}

		private static void WriteFunction(string message)
		{
			var method = typeof(ECR.Configuration).Assembly.GetType("ECR.EcrLog", throwOnError: true)!
				.GetMethod("WriteFunction", BindingFlags.Static | BindingFlags.NonPublic)!;
			method.Invoke(null, [message, null]);
		}

		[Fact]
		public void Writes_header_and_entry_to_configured_directory()
		{
			ECR.Configuration.logDirectory = Path.Combine(_dir.FullName, "Log");

			WriteFunction("prvi");
			WriteFunction("drugi");

			var file = Assert.Single(Directory.GetFiles(ECR.Configuration.logDirectory));
			Assert.Equal($"std_log_{DateTime.Now:yyyy-MM-dd}.log", Path.GetFileName(file));
			var lines = File.ReadAllLines(file);
			Assert.StartsWith("PAYTEN - Standard ECR log: ", lines[0]);
			Assert.Single(lines, l => l.StartsWith("PAYTEN"));     // zaglavlje samo jednom
			Assert.EndsWith(" prvi", lines[^2]);
			Assert.EndsWith(" drugi", lines[^1]);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		public void Missing_directory_is_ignored(string? directory)
		{
			ECR.Configuration.logDirectory = directory!;

			WriteFunction("bez foldera");
		}

		/// <summary>Folder koji ne može da se napravi (putanja ide kroz postojeći fajl) — kao folder bez prava upisa.</summary>
		[Fact]
		public void Unwritable_directory_is_ignored()
		{
			var file = Path.Combine(_dir.FullName, "fajl");
			File.WriteAllText(file, "");
			ECR.Configuration.logDirectory = Path.Combine(file, "Log");

			WriteFunction("bez prava");

			Assert.False(Directory.Exists(ECR.Configuration.logDirectory));
		}
	}
}