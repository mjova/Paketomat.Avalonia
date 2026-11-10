namespace TopPos.ServerSimulator
{
	// v4: bez WPF prikaza (boje, okvir po veličini) — ostaje stanje vrata koje server simulira (greške, zauzetost).
	public class DoorViewModel
	{
		public DoorViewModel(int number)
		{
			Number = number;
			int posInGroup = (number - 1) % 8;
			Size = posInGroup < 3 ? "S" : posInGroup < 6 ? "M" : "L";
		}

		public int Number { get; }
		public string Size { get; }

		public string? ErrorCode { get; set; }

		public bool HasPackage { get; set; }

		public string DisplayText => ErrorCode != null ? $"{Number}-{ErrorCode}" : Number.ToString();
	}

	public class DoorErrorEntry
	{
		public int DoorNumber { get; set; }
		public string ErrorCode { get; set; } = default!;
	}

	/// <summary>Vrata paketomata na serveru. Nije bezbedno za više niti — <see cref="HttpServer"/> ga koristi pod svojim lock-om.</summary>
	public class DoorPanelViewModel
	{
		private readonly List<DoorViewModel> _allDoors;

		public DoorPanelViewModel(int doorCount)
		{
			_allDoors = Enumerable.Range(1, doorCount).Select(i => new DoorViewModel(i)).ToList();
		}

		public IReadOnlyList<DoorViewModel> Doors => _allDoors;

		public void SetError(int doorNumber, string errorCode)
		{
			var door = _allDoors.FirstOrDefault(d => d.Number == doorNumber);
			if (door != null)
				door.ErrorCode = errorCode;
		}

		public void ClearError(int doorNumber)
		{
			var door = _allDoors.FirstOrDefault(d => d.Number == doorNumber);
			if (door != null)
				door.ErrorCode = null;
		}

		public void ClearErrors()
		{
			foreach (var door in _allDoors)
				door.ErrorCode = null;
		}

		public bool HasError(int doorNumber)
			=> _allDoors.FirstOrDefault(d => d.Number == doorNumber)?.ErrorCode != null;

		public List<DoorErrorEntry> GetErrors()
		{
			return _allDoors
				.Where(d => d.ErrorCode != null)
				.Select(d => new DoorErrorEntry { DoorNumber = d.Number, ErrorCode = d.ErrorCode! })
				.ToList();
		}

		public void UpdatePackages(IEnumerable<PackageItem> packages)
		{
			var occupied = packages.Where(p => p.UPaketomatu).Select(p => p.BrojPozicije).ToHashSet();
			foreach (var door in _allDoors)
				door.HasPackage = occupied.Contains(door.Number);
		}
	}
}
