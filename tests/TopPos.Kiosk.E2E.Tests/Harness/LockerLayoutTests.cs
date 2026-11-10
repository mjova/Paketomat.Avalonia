using System.Text.Json.Nodes;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;

namespace TopPos.E2E.Tests.Harness
{
	/// <summary>
	/// Samoprovera rasporeda vrata i opcija kontrolera (ne traži E2E okruženje): Serial opcije menjaju sva tri šablona
	/// isto, a TestData bira vrata unutar modula zadatog rasporeda.
	/// </summary>
	public sealed class LockerLayoutTests : IDisposable
	{
		private readonly string _root = Path.Combine(Path.GetTempPath(), "toppos-e2e-selftest", Guid.NewGuid().ToString("N"));

		[Fact]
		public void SerialController_ChangesClientLockerAndServerConfiguration()
		{
			var settings = E2ESettings.Current;
			var options = KioskOptions.SerialController(settings);

			var client = Write("client.app-configuration.json", options.ConfigureClient);
			var locker = Write("locker.app-configuration.json", options.ConfigureLocker);
			var server = Write("server.app-configuration.json", options.ConfigureServer);

			Assert.Equal(LockerLayout.Serial, options.Layout);
			Assert.Equal("Serial", client["Locker"]!["ControllerType"]!.GetValue<string>());
			Assert.Equal(32, client["Locker"]!["LockerBoxCount"]!.GetValue<int>());
			Assert.Equal(settings.LockerClientPort, client["Locker"]!["ComPort"]!["PortName"]!.GetValue<string>());
			Assert.Equal("Serial", locker["LockController"]!["Type"]!.GetValue<string>());
			Assert.Equal(settings.LockerSimulatorPort, locker["LockController"]!["ComPort"]!["PortName"]!.GetValue<string>());
			Assert.Equal(4, locker["Locker"]!["ModuleCount"]!.GetValue<int>());
			Assert.Equal(8, locker["Locker"]!["BoxCountPerModule"]!.GetValue<int>());
			Assert.Equal(32, server["DoorCount"]!.GetValue<int>());
		}

		[Theory]
		[InlineData(1, 1, 8)]
		[InlineData(2, 9, 16)]
		[InlineData(4, 25, 32)]
		public void TestData_SerialLayout_DoorIsInsideModule(int module, int first, int last)
		{
			var data = new TestData(E2ESettings.Current, LockerLayout.Serial);
			for (int i = 0; i < 5; i++) {
				var door = data.RecipientPackage(module: module).BrojPozicije;
				Assert.InRange(door, first, last);
				Assert.NotEqual(3, door);	// servisna vrata
			}
		}

		[Fact]
		public void TestData_ModuleOutsideLayout_Throws()
		{
			var data = new TestData(E2ESettings.Current, LockerLayout.Ncu24L);
			Assert.Throws<ArgumentOutOfRangeException>(() => data.RecipientPackage(module: 3));
		}

		private JsonNode Write(string template, Action<JsonObject>? configure)
		{
			Directory.CreateDirectory(_root);
			var target = Path.Combine(_root, template);
			Sandbox.WriteConfiguration(template, target, KioskSession.ConfigurationTokens(E2ESettings.Current), configure);
			return JsonNode.Parse(File.ReadAllText(target))!;
		}

		public void Dispose()
		{
			if (Directory.Exists(_root)) {
				Directory.Delete(_root, recursive: true);
			}
		}
	}
}
