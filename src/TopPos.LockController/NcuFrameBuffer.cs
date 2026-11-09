namespace TopPos.LockController
{
	/// <summary>
	/// Prijemni bafer NCU kontrolera (NCU16L, NCU24L) iza ZLG mrežnog modula. Modul prosleđuje serijske okvire jedan za
	/// drugim, pa se okviri čitaju redom, od granice okvira — okvir se nikad ne traži usred drugog okvira. Kod traženja
	/// bajt po bajt bi ostatak zakasnelog okvira spojen sa početkom pravog odgovora mogao da prođe jednobajtnu kontrolnu
	/// sumu i da se pročita kao odgovor (pogrešno stanje vrata). Iz istog razloga se posle oštećenog okvira granica ne
	/// traži unutar primljenih bajtova: bafer prelazi u <see cref="IsDesynchronized"/>, tekuća razmena se prekida bez
	/// odgovora („kontroler nije odgovorio"), a klijent pre sledeće komande sačeka tišinu na vezi i očisti bafer.
	/// Okvir može da stigne u više TCP segmenata — nekompletan se čeka.
	/// </summary>
	public abstract class NcuFrameBuffer
	{
		/// <summary>Stanje okvira koji počinje na početku bafera (prvi bajt je STX).</summary>
		protected enum FrameCheck
		{
			/// <summary>Okvir je ceo i ispravan; dužina je poznata.</summary>
			Complete,
			/// <summary>Okvir još stiže.</summary>
			Incomplete,
			/// <summary>Na početku bafera nije ispravan okvir.</summary>
			Invalid
		}

		/// <summary>
		/// Najveći prefiks koji BU modul pošalje ispred prvog odgovora na vezi (npr. A0 FE 00). Viđen je samo pred
		/// prvim odgovorom posle povezivanja, pa se samo tada i prihvata.
		/// </summary>
		private const int MaxPrefixLength = 3;

		/// <summary>Najviše bajtova koji se čuvaju; odgovori imaju 8–40 B, pa je ovo daleko iznad ispravnog toka.</summary>
		public const int MaxBufferedBytes = 4096;

		/// <summary>Koliko bajtova se najviše ispisuje u log.</summary>
		private const int MaxLoggedBytes = 64;

		private readonly List<byte> _bytes = new List<byte>();
		private readonly Action<string>? _warning;
		private readonly Action<string>? _debug;

		/// <summary>
		/// Na početku bafera je zadržan početak zastarelog okvira; kad stigne ceo, odbacuje se kao celina,
		/// da njegov ostatak ne bi bio pročitan kao nov okvir.
		/// </summary>
		private bool _discardNextFrame;

		/// <summary>Na ovoj vezi je već stigao bar jedan okvir — od tada se BU prefiks više ne prihvata.</summary>
		private bool _frameReceived;

		protected NcuFrameBuffer(Action<string>? warning, Action<string>? debug)
		{
			_warning = warning;
			_debug = debug;
		}

		/// <summary>Broj primljenih bajtova koji još nisu deo pročitanog okvira.</summary>
		public int Count => _bytes.Count;

		/// <summary>
		/// Stigao je oštećen okvir ili neočekivani bajtovi na granici okvira, pa je granica izgubljena. Dok se ne pozove
		/// <see cref="Resynchronize"/> ili <see cref="Clear"/>, ne vraća se nijedan okvir — klijent prekida razmenu i
		/// pre sledeće komande čeka tišinu na vezi.
		/// </summary>
		public bool IsDesynchronized { get; private set; }

		public byte[] ToArray() => _bytes.ToArray();

		/// <summary>Nova veza: bajtovi sa stare ne pripadaju odgovorima na novoj, a BU prefiks se opet prihvata.</summary>
		public void Clear()
		{
			Resynchronize();
			_frameReceived = false;
		}

		/// <summary>Na vezi je nastupila tišina posle izgubljene granice: prijem počinje od čistog bafera.</summary>
		public void Resynchronize()
		{
			_bytes.Clear();
			_discardNextFrame = false;
			this.IsDesynchronized = false;
		}

		/// <summary>
		/// Dodaje primljene bajtove. Dok je granica okvira izgubljena, ništa se ne čuva (klijent čeka tišinu), a bafer
		/// veći od <see cref="MaxBufferedBytes"/> znači da tok nema smisla — i to je gubitak granice. Tako bafer ostaje
		/// ograničen i kad kontroler šalje bez prekida, a DoorWatcher ponavlja pokušaje.
		/// </summary>
		public void Append(byte[] data, int count)
		{
			if (this.IsDesynchronized) {
				return;
			}

			_bytes.AddRange(new ArraySegment<byte>(data, 0, count));
			if (_bytes.Count > MaxBufferedBytes) {
				Desynchronize($"prijemni bafer je premašio {MaxBufferedBytes} B");
			}
		}

		/// <summary>
		/// Pre slanja komande: odbacuje sve što je već stiglo — to su zakasneli ili dupli odgovori na ranije komande.
		/// Okvir od koga je stigao samo početak ostaje u baferu i odbacuje se kad stigne ceo (<see cref="TakeNextFrame"/>).
		/// </summary>
		public void DiscardStale()
		{
			var discarded = new List<byte>();
			byte[]? frame;
			while ((frame = TakeNextFrame()) != null) {
				discarded.AddRange(frame);
			}

			if (discarded.Count > 0) {
				_warning?.Invoke($"odbačeno {discarded.Count} bajtova zastarelih okvira pre slanja komande: {Hex(discarded)}");
			}

			// Zastareo je samo započet okvir (počinje sa STX); BU prefiks koji čeka prvi okvir na vezi nije.
			if (_bytes.Count > 0 && _bytes[0] == this.Stx && !this.IsDesynchronized)
			{
				_discardNextFrame = true;
				_warning?.Invoke($"zadržan početak zastarelog okvira ({_bytes.Count} B), odbacuje se kad stigne ceo: {Hex(_bytes)}");
			}
		}

		/// <summary>
		/// Vadi sledeći kompletan okvir sa početka bafera; null ako ga još nema ili ako je granica okvira izgubljena
		/// (<see cref="IsDesynchronized"/>). Na granici okvira mora da bude STX; drugi bajtovi znače oštećenje —
		/// osim BU prefiksa pred prvim okvirom na vezi.
		/// </summary>
		public byte[]? TakeNextFrame()
		{
			if (this.IsDesynchronized) {
				return null;
			}

			while (_bytes.Count > 0)
			{
				int stx = _bytes.IndexOf(this.Stx);
				int junk = stx < 0 ? _bytes.Count : stx;
				if (junk > 0)
				{
					if (_frameReceived || junk > MaxPrefixLength)
					{
						// Ne preskače se do sledećeg 0x02: ako je oštećen baš STX, to bi bilo usred okvira.
						Desynchronize($"neočekivani bajtovi na granici okvira ({junk} B)");
						return null;
					}

					if (stx < 0) {
						return null;	// prefiks još stiže
					}

					_debug?.Invoke($"preskočen prefiks BU modula: {Hex(_bytes.GetRange(0, junk))}");
					_bytes.RemoveRange(0, junk);
					continue;
				}

				switch (CheckFrame(_bytes, out int length))
				{
					case FrameCheck.Incomplete:
						return null;

					case FrameCheck.Invalid:
						Desynchronize("oštećen okvir");
						return null;

					default:
						var frame = _bytes.GetRange(0, length).ToArray();
						_bytes.RemoveRange(0, length);
						_frameReceived = true;

						if (_discardNextFrame)
						{
							_discardNextFrame = false;
							_warning?.Invoke($"odbačen zastareo okvir, stigao do kraja: {Hex(frame)}");
							continue;
						}

						return frame;
				}
			}

			return null;
		}

		/// <summary>
		/// Granica okvira je izgubljena. Ne traži se unutar primljenih bajtova: kandidat iz sredine oštećenog okvira
		/// može da prođe jednobajtnu sumu, adresu i komandu i da se pročita kao (pogrešno) stanje vrata.
		/// </summary>
		private void Desynchronize(string reason)
		{
			_warning?.Invoke($"{reason}, granica okvira je izgubljena; odbačeno {_bytes.Count} bajtova: {Hex(_bytes)}");
			_bytes.Clear();
			_discardNextFrame = false;
			this.IsDesynchronized = true;
		}

		/// <summary>Početni bajt okvira.</summary>
		protected abstract byte Stx { get; }

		/// <summary>Proverava okvir na početku bafera (<paramref name="bytes"/>[0] je STX) i vraća njegovu dužinu.</summary>
		protected abstract FrameCheck CheckFrame(IReadOnlyList<byte> bytes, out int length);

		private static string Hex(IReadOnlyCollection<byte> bytes)
			=> string.Join(" ", bytes.Take(MaxLoggedBytes).Select(b => b.ToString("X2")))
			   + (bytes.Count > MaxLoggedBytes ? " …" : "");
	}
}
