using TopPos.LockController;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Najveći broj vrata po tipu kontrolera (konfigurator odbija snimanje, klijent odbija start).
	/// Broj vrata ne mora biti deljiv sa brojem vrata po modulu.
	/// </summary>
	public class LockControllerTypesTests
	{
		[Theory]
		[InlineData(LockControllerTypes.Serial, 127)]
		[InlineData(LockControllerTypes.SerialDailyExpress, 127)]
		[InlineData(LockControllerTypes.NCU16L, 256)]
		[InlineData(LockControllerTypes.NCU24L, 240)]
		public void GetMaxDoorCount_known_types(string controllerType, int expected)
			=> Assert.Equal(expected, LockControllerTypes.GetMaxDoorCount(controllerType));

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("serial")]      // klijent bira kontroler po tačnom imenu (switch je osetljiv na velika/mala slova)
		[InlineData("NCU32L")]
		public void GetMaxDoorCount_unknown_type_is_null(string? controllerType)
			=> Assert.Null(LockControllerTypes.GetMaxDoorCount(controllerType));

		[Fact]
		public void Serial_max_matches_implementation_constants()
		{
			Assert.Equal(TopPos.LockController.Serial.Constants.MaxDoorNumber, LockControllerTypes.GetMaxDoorCount(LockControllerTypes.Serial));
			Assert.Equal(TopPos.LockController.SerialDailyExpress.Constants.MaxDoorNumber, LockControllerTypes.GetMaxDoorCount(LockControllerTypes.SerialDailyExpress));
		}

		[Theory]
		[InlineData(LockControllerTypes.Serial, 8, 16)]
		[InlineData(LockControllerTypes.SerialDailyExpress, 8, 16)]    // Constants.MaxModuleNumber = 0x10
		[InlineData(LockControllerTypes.NCU16L, 16, 16)]               // adrese 0x00-0x0F
		[InlineData(LockControllerTypes.NCU24L, 24, 10)]               // adrese 0x00-0x09
		public void Max_door_count_does_not_exceed_module_addresses(string controllerType, int doorsPerModule, int maxModules)
			=> Assert.Equal(maxModules, DoorWatcher.GetModuleCount(LockControllerTypes.GetMaxDoorCount(controllerType)!.Value, doorsPerModule));

		[Theory]
		[InlineData(LockControllerTypes.Serial, 127)]
		[InlineData(LockControllerTypes.SerialDailyExpress, 127)]
		[InlineData(LockControllerTypes.NCU16L, 256)]
		[InlineData(LockControllerTypes.NCU24L, 240)]
		public void ValidateDoorCount_boundaries(string controllerType, int max)
		{
			Assert.NotNull(LockControllerTypes.ValidateDoorCount(controllerType, 0));
			Assert.NotNull(LockControllerTypes.ValidateDoorCount(controllerType, -1));
			Assert.Null(LockControllerTypes.ValidateDoorCount(controllerType, 1));
			Assert.Null(LockControllerTypes.ValidateDoorCount(controllerType, 35));     // nije deljivo ni sa 8, 16 ni 24
			Assert.Null(LockControllerTypes.ValidateDoorCount(controllerType, max - 1));
			Assert.Null(LockControllerTypes.ValidateDoorCount(controllerType, max));

			string? error = LockControllerTypes.ValidateDoorCount(controllerType, max + 1);
			Assert.NotNull(error);
			Assert.Contains(max.ToString(), error);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("serial")]
		[InlineData("NCU32L")]
		public void ValidateDoorCount_unknown_type_is_error(string? controllerType)
			=> Assert.NotNull(LockControllerTypes.ValidateDoorCount(controllerType, 32));
	}
}
