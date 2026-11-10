using System.Diagnostics;
using System.Text.Json.Nodes;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Harness
{
	/// <summary>
	/// Samoprovera podešavanja po testu (<see cref="KioskOptions"/>): izmena konfiguracije stiže u upisani
	/// app-configuration.json i klijent je čita, a priprema foldera klijenta se odigra pre starta procesa — i sme da upiše
	/// fajl koji <see cref="Sandbox.AssertClean"/> inače odbija. Traži E2E okruženje (pravi start sva tri procesa).
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class KioskOptionsTests(ITestOutputHelper output)
	{
		[SkippableFact]
		public Task Options_ChangeWrittenConfiguration_PreparationRunsBeforeClientStarts()
		{
			JsonNode? configurationDuringPreparation = null;
			bool? clientRunningDuringPreparation = null;
			var options = new KioskOptions {
				ConfigureClient = c => c["General"]!["OperationTimeout"] = 21000,
				ConfigureServer = c => c["CourierQrCodes"]!.AsArray().Add("QR-SELFTEST;1"),
				PrepareClientDirectory = directory => {
					configurationDuringPreparation = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "app-configuration.json")));
					clientRunningDuringPreparation = IsClientRunningFrom(directory);
					File.WriteAllText(Path.Combine(directory, "PaymentConfirmation.json"), "[]");	// na spisku ExcludedFiles
				},
			};

			return KioskSession.RunAsync(nameof(Options_ChangeWrittenConfiguration_PreparationRunsBeforeClientStarts), output, options, kiosk => {
				Assert.False(clientRunningDuringPreparation, "Priprema foldera klijenta mora se završiti pre starta klijenta.");
				Assert.Equal(21000, configurationDuringPreparation!["General"]!["OperationTimeout"]!.GetValue<int>());
				Assert.True(File.Exists(Path.Combine(kiosk.ClientDirectory, "PaymentConfirmation.json")));

				// Klijent je pročitao izmenjenu konfiguraciju (tajmer se pravi iz General.OperationTimeout).
				Assert.NotEmpty(kiosk.Log.LinesAfter(0, "OperationCountdownTimer inicijalizovan, period: 21 s."));

				var server = JsonNode.Parse(File.ReadAllText(kiosk.ServerConfigurationPath))!;
				Assert.Equal([kiosk.Settings.CourierQrCode, "QR-SELFTEST;1"], server["CourierQrCodes"]!.AsArray().Select(n => n!.GetValue<string>()));
				return Task.CompletedTask;
			});
		}

		private static bool IsClientRunningFrom(string directory)
		{
			var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
			foreach (var process in Process.GetProcessesByName("TopPos.Kiosk")) {
				using (process) {
					try {
						if (process.MainModule?.FileName?.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true) {
							return true;
						}
					}
					catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
						// proces je izašao ili mu se ne može pristupiti — nije naš
					}
				}
			}
			return false;
		}
	}
}
