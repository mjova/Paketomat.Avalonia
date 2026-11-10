namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>
	/// Jedinstveni podaci za jedan test (kod, telefon, barkod, vrata). Runner ih upisuje u ServerSimulator i iste
	/// vrednosti kuca na ekranu ili šalje kao skeniran barkod — ništa se ne unosi ručno.
	/// </summary>
	public sealed class TestData(E2ESettings settings, LockerLayout layout)
	{
		private const int ServiceDoorNumber = 3;	// iz client.app-configuration.json — ne koristi se za pakete

		private readonly Random _random = new();
		private readonly HashSet<int> _usedDoors = [ServiceDoorNumber];
		private readonly HashSet<int> _usedCodes = [];

		public string CourierQrCode => settings.CourierQrCode;

		/// <summary>
		/// Paket koji čeka primaoca u zadatim (ili slučajnim slobodnim vratima zadatog modula — NCU24L: 1 = 1–24, 2 = 25–48;
		/// Serial: 1 = 1–8, 2 = 9–16...).
		/// </summary>
		public SimPackage RecipientPackage(decimal amount = 0, int? door = null, int module = 1)
		{
			var number = door ?? NextDoor(module);
			_usedDoors.Add(number);
			return new SimPackage {
				Barkod = $"E2E{_random.Next(100000, 999999)}",
				BrojPozicije = number,
				KodZaOtvaranje = NextCode(),
				IznosOtkupa = amount,
				Velicina = "S",
				TelefonPrimaoca = $"06{_random.Next(10000000, 99999999)}",
			};
		}

		/// <summary>
		/// Paket koji kurir/pošiljalac tek ostavlja (BrojPozicije 0). ServerSimulator dodeljuje najniža slobodna vrata
		/// odgovarajuće veličine (u grupi od 8: 1–3 S, 4–6 M, 7–8 L); stvarna vrata test čita iz evidencije otključavanja.
		/// </summary>
		public SimPackage DepositPackage(string size = "M")
			=> new() {
				Barkod = $"E2E{_random.Next(100000, 999999)}",
				BrojPozicije = 0,
				Velicina = size,
				TelefonPrimaoca = $"06{_random.Next(10000000, 99999999)}",
			};

		/// <summary>Veličina vrata po rasporedu ServerSimulator-a (GetBoxSize): u grupi od 8 vrata 1–3 S, 4–6 M, 7–8 L.</summary>
		public static string BoxSize(int door)
			=> ((door - 1) % 8) switch { < 3 => "S", < 6 => "M", _ => "L" };

		/// <summary>Istekao paket koji kurir preuzima iz zadatih (ili slučajnih slobodnih) vrata.</summary>
		public SimPackage ExpiredPackage(int? door = null)
			=> RecipientPackage(door: door) with { Istekao = true };

		private int NextDoor(int module)
		{
			if (module < 1 || module > layout.Modules) {
				throw new ArgumentOutOfRangeException(nameof(module), module, $"Raspored ima {layout.Modules} modula.");
			}
			var first = (module - 1) * layout.DoorsPerModule + 1;
			for (int attempt = 0; attempt < 100; attempt++) {
				var door = _random.Next(first, first + layout.DoorsPerModule);
				if (!_usedDoors.Contains(door)) {
					return door;
				}
			}
			throw new InvalidOperationException($"Nema slobodnih vrata u modulu {module}.");
		}

		private int NextCode()
		{
			int code;
			do {
				code = _random.Next(1000000, 9999999);	// 7 cifara (MinimumUnlockCodeLength 6, Maximum 11)
			} while (!_usedCodes.Add(code));
			return code;
		}
	}
}
