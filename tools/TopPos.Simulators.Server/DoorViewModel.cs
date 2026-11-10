using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace TopPos.ServerSimulator
{
	public class DoorViewModel : INotifyPropertyChanged
	{
		private static readonly SolidColorBrush BrushOk = new(Color.FromRgb(0x7A, 0xBA, 0x78));
		private static readonly SolidColorBrush BrushError = new(Color.FromRgb(0xFA, 0x70, 0x70));

		public event PropertyChangedEventHandler? PropertyChanged;

		public DoorViewModel(int number)
		{
			Number = number;
			int posInGroup = (number - 1) % 8;
			Size = posInGroup < 3 ? "S" : posInGroup < 6 ? "M" : "L";
		}

		public int Number { get; }
		public string Size { get; }

		/// <summary>
		/// Left border thickness encodes door size: S=1, M=3, L=5
		/// </summary>
		public Thickness SizeBorderThickness => Size switch
		{
			"S" => new Thickness(2, 1, 1, 1),
			"M" => new Thickness(4, 1, 1, 1),
			"L" => new Thickness(6, 1, 1, 1),
			_ => new Thickness(1)
		};

		private string? _errorCode;
		public string? ErrorCode
		{
			get => _errorCode;
			set
			{
				if (_errorCode != value)
				{
					_errorCode = value;
					OnPropertyChanged();
					OnPropertyChanged(nameof(Background));
					OnPropertyChanged(nameof(DisplayText));
				}
			}
		}

		private bool _hasPackage;
		public bool HasPackage
		{
			get => _hasPackage;
			set
			{
				if (_hasPackage != value)
				{
					_hasPackage = value;
					OnPropertyChanged();
					OnPropertyChanged(nameof(PackageIndicator));
				}
			}
		}

		public string PackageIndicator => _hasPackage ? "X" : "";

		public SolidColorBrush Background => ErrorCode != null ? BrushError : BrushOk;

		public string DisplayText => ErrorCode != null ? $"{Number}-{ErrorCode}" : Number.ToString();

		private void OnPropertyChanged([CallerMemberName] string? name = null)
			=> PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}

	public class DoorGroup
	{
		public IReadOnlyList<DoorViewModel> Doors { get; }

		public DoorGroup(IEnumerable<DoorViewModel> doors)
		{
			Doors = doors.ToList();
		}
	}

	public class DoorPanelViewModel
	{
		private readonly List<DoorViewModel> _allDoors;
		public IReadOnlyList<DoorGroup> DoorGroups { get; }

		public DoorPanelViewModel(int doorCount)
		{
			_allDoors = Enumerable.Range(1, doorCount).Select(i => new DoorViewModel(i)).ToList();
			DoorGroups = _allDoors.Chunk(8).Select(chunk => new DoorGroup(chunk)).ToList();
		}

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

		public bool HasError(int doorNumber)
			=> _allDoors.FirstOrDefault(d => d.Number == doorNumber)?.ErrorCode != null;

		public List<DoorErrorEntry> GetErrors()
		{
			return _allDoors
				.Where(d => d.ErrorCode != null)
				.Select(d => new DoorErrorEntry { DoorNumber = d.Number, ErrorCode = d.ErrorCode! })
				.ToList();
		}

		public void LoadErrors(IEnumerable<DoorErrorEntry> errors)
		{
			foreach (var entry in errors)
				SetError(entry.DoorNumber, entry.ErrorCode);
		}

		public void UpdatePackages(IEnumerable<PackageItem> packages)
		{
			var occupied = packages.Where(p => p.UPaketomatu).Select(p => p.BrojPozicije).ToHashSet();
			foreach (var door in _allDoors)
				door.HasPackage = occupied.Contains(door.Number);
		}
	}
}
