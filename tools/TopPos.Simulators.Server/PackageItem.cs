using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TopPos.ServerSimulator
{
	public class PackageItem : INotifyPropertyChanged
	{
		private string _barkod = string.Empty;
		private int _brojPozicije;
		private int _kodZaOtvaranje;
		private decimal _iznosOtkupa;
		private string _velicina = "M";
		private string _telefonPrimaoca = string.Empty;
		private bool _istekao;
		private bool _placeno;
		private bool _preuzet;

		public string Barkod
		{
			get => _barkod;
			set { _barkod = value; OnPropertyChanged(); }
		}

		public int BrojPozicije
		{
			get => _brojPozicije;
			set { _brojPozicije = value; OnPropertyChanged(); }
		}

		public int KodZaOtvaranje
		{
			get => _kodZaOtvaranje;
			set { _kodZaOtvaranje = value; OnPropertyChanged(); }
		}

		public decimal IznosOtkupa
		{
			get => _iznosOtkupa;
			set { _iznosOtkupa = value; OnPropertyChanged(); }
		}

		public string Velicina
		{
			get => _velicina;
			set { _velicina = value; OnPropertyChanged(); }
		}

		public string TelefonPrimaoca
		{
			get => _telefonPrimaoca;
			set { _telefonPrimaoca = value; OnPropertyChanged(); }
		}

		public bool Istekao
		{
			get => _istekao;
			set { _istekao = value; OnPropertyChanged(); }
		}

		// Otkup je plaćen (PotvrdaPlacanja). Dok nije, PrimaocPraznjenjePozicije ne otkriva broj pozicije.
		public bool Placeno
		{
			get => _placeno;
			set { _placeno = value; OnPropertyChanged(); }
		}

		// Preuzimanje potvrđeno (PrimaocPraznjenjePozicijePotvrda). Kao pravi server: paket ostaje, isti kod i dalje otvara
		// ista vrata dok kurir u njih ne ostavi nov paket; vrata su za ostavljanje slobodna.
		public bool Preuzet
		{
			get => _preuzet;
			set { _preuzet = value; OnPropertyChanged(); }
		}

		public bool CekaUplatu => IznosOtkupa > 0 && !Placeno;

		/// <summary>Paket je ostavljen i nije preuzet — drži svoja vrata.</summary>
		public bool UPaketomatu => BrojPozicije > 0 && !Preuzet;

		/// <summary>Nezavisna kopija vrednosti (bez pretplatnika na PropertyChanged) — za serijalizaciju van UI niti.</summary>
		public PackageItem Snapshot() => new() {
			Barkod = Barkod,
			BrojPozicije = BrojPozicije,
			KodZaOtvaranje = KodZaOtvaranje,
			IznosOtkupa = IznosOtkupa,
			Velicina = Velicina,
			TelefonPrimaoca = TelefonPrimaoca,
			Istekao = Istekao,
			Placeno = Placeno,
			Preuzet = Preuzet,
		};

		public event PropertyChangedEventHandler? PropertyChanged;

		protected void OnPropertyChanged([CallerMemberName] string? name = null)
			=> PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
