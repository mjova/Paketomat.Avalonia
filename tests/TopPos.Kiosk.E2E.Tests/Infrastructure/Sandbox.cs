using System.Text.Json;
using System.Text.Json.Nodes;

namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>
	/// Sveža kopija bin foldera za jedan test, bez runtime stanja. Bin folderi na razvojnoj mašini nose stanje iz
	/// ručnog rada (npr. ServerStatus.json sa greškama vrata koje ServerSimulator sam učita), pa se ono ne kopira;
	/// konfiguracija se piše iz e2e šablona. Vidi doc/plan-e2e-ui-testing.md, sekcija 3.7.
	/// </summary>
	public static class Sandbox
	{
		private static readonly JsonSerializerOptions __indented = new() { WriteIndented = true };


		/// <summary>Runtime stanje i lokalne konfiguracije koje ne smeju preći iz bin-a u sandbox (poređenje bez obzira na velika slova).</summary>
		public static readonly IReadOnlySet<string> ExcludedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
			"ServerStatus.json",
			"ServerStatus.json.tmp",
			"PaymentConfirmation.json",
			"PaymentConfirmation.json.tmp",
			"ServerConfirmations.json",
			"ServerConfirmations.json.tmp",
			"status.lock",
			"status.lock.tmp",
			"app-configuration.json",
			"app-configuration.ScreenResolution.json",
			"app-configuration.Simulator.json",	// Debug simulatori u klijentu (npr. POS) — E2E koristi pravi ECR
		};

		/// <summary>Folderi sa logovima (Serilog Logs\, ECR Log\).</summary>
		public static readonly IReadOnlySet<string> ExcludedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
			"Logs",
			"Log",
		};

		/// <summary>Kopira <paramref name="sourceDirectory"/> u <paramref name="targetDirectory"/> bez isključenih fajlova i foldera.</summary>
		public static void CopyBinaries(string sourceDirectory, string targetDirectory)
		{
			if (!Directory.Exists(sourceDirectory)) {
				throw new DirectoryNotFoundException($"Nema build izlaza: {sourceDirectory} (prvo build-ovati rešenje).");
			}
			Directory.CreateDirectory(targetDirectory);
			foreach (var file in Directory.EnumerateFiles(sourceDirectory)) {
				if (!ExcludedFiles.Contains(Path.GetFileName(file))) {
					File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)));
				}
			}
			foreach (var directory in Directory.EnumerateDirectories(sourceDirectory)) {
				var name = Path.GetFileName(directory);
				if (!ExcludedDirectories.Contains(name)) {
					CopyBinaries(directory, Path.Combine(targetDirectory, name));
				}
			}
		}

		/// <summary>Pada ako u sandbox-u (pre starta procesa) postoji runtime stanje.</summary>
		public static void AssertClean(string directory)
		{
			var leftovers = Directory.EnumerateFileSystemEntries(directory)
										.Select(Path.GetFileName)
										.Where(name => name != null && (ExcludedFiles.Contains(name) || ExcludedDirectories.Contains(name)))
										.ToList();
			if (leftovers.Count > 0) {
				throw new InvalidOperationException($"Sandbox {directory} sadrži runtime stanje: {string.Join(", ", leftovers)}");
			}
		}

		/// <summary>
		/// Upisuje šablon iz Config\ sa zamenjenim {{Token}} vrednostima; <paramref name="configure"/> (podešavanje po testu,
		/// <see cref="KioskOptions"/>) menja konfiguraciju posle zamene tokena.
		/// </summary>
		public static void WriteConfiguration(string templateName, string targetPath, IReadOnlyDictionary<string, string> tokens,
												Action<JsonObject>? configure = null)
		{
			var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Config", templateName));
			foreach (var (key, value) in tokens) {
				text = text.Replace("{{" + key + "}}", value);
			}
			if (text.Contains("{{")) {
				throw new InvalidOperationException($"Šablon {templateName} ima nezamenjen token.");
			}
			if (configure != null) {
				var configuration = JsonNode.Parse(text) as JsonObject
										?? throw new InvalidOperationException($"Šablon {templateName} nije JSON objekat.");
				configure(configuration);
				text = configuration.ToJsonString(__indented);
			}
			File.WriteAllText(targetPath, text);
		}

		/// <summary>Briše sandbox-e starije od jednog dana (pokretanja koja su pala ostaju za pregled dok ne zastare).</summary>
		public static void DeleteStaleRuns(string sandboxRoot)
		{
			if (!Directory.Exists(sandboxRoot)) {
				return;
			}
			foreach (var run in Directory.EnumerateDirectories(sandboxRoot)) {
				try {
					if (Directory.GetLastWriteTime(run) < DateTime.Now.AddDays(-1)) {
						Directory.Delete(run, recursive: true);
					}
				}
				catch (IOException) {
					// fajl drži proces koji još nije ugašen — sledeći put
				}
				catch (UnauthorizedAccessException) {
				}
			}
		}
	}
}
