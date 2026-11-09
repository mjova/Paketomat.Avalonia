namespace TopPos.LockController
{
	public static class LockControllerTypes
	{
		public const string Serial = "Serial";
		public const string NCU16L = "NCU16L";
		public const string NCU24L = "NCU24L";
		public const string SerialDailyExpress = "SerialDailyExpress";

		/// <summary>
		/// Najveći broj vrata za tip kontrolera:
		/// Serial/SerialDailyExpress 127 (xls fajl: 0x01-0x7F),
		/// NCU16L 256 (adrese 0x00-0x0F * 16),
		/// NCU24L 240 (adrese 0x00-0x09 * 24).
		/// Za nepoznat tip vraća null.
		/// </summary>
		public static int? GetMaxDoorCount(string? controllerType)
			=> controllerType switch {
				Serial or SerialDailyExpress => 127,
				NCU16L => 256,
				NCU24L => 240,
				_ => null
			};

		/// <summary>
		/// Proverava broj vrata za tip kontrolera. Broj ne mora biti deljiv sa brojem vrata po modulu
		/// (poslednji modul može biti delimično popunjen), ali ne sme preći granicu tipa kontrolera.
		/// Vraća poruku o grešci ili null ako je broj ispravan.
		/// </summary>
		public static string? ValidateDoorCount(string? controllerType, int doorCount)
		{
			if (doorCount <= 0)
				return "Broj vrata mora biti veći od 0.";
			int? maxDoorCount = GetMaxDoorCount(controllerType);
			if (maxDoorCount == null)
				return $"Nepoznat tip kontrolera: {controllerType}.";
			if (doorCount > maxDoorCount)
				return $"Za kontroler {controllerType} broj vrata može biti najviše {maxDoorCount}.";
			return null;
		}
	}
}
