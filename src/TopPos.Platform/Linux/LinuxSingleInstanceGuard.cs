namespace TopPos.Platform.Linux
{
	/// <summary>
	/// Zaključan fajl na jednoj putanji za ceo uređaj (<c>/run/lock</c>, inače <c>/tmp</c>), bez obzira na korisnika i način
	/// pokretanja (systemd servis ili ručno). .NET na Linuxu za <see cref="FileShare.None"/> uzima <c>flock(LOCK_EX)</c>, koji
	/// kernel oslobađa i kad proces padne.
	/// Postojeći fajl se otvara bez <c>O_CREAT</c> i samo za čitanje (flock ne traži pravo upisa): u sticky folderu kernel uz
	/// <c>fs.protected_regular</c> odbija <c>O_CREAT</c> nad tuđim fajlom, pa bi fajl koji je ostavio drugi korisnik sprečio start.
	/// Nepostojeći fajl se pravi isključivo (<see cref="FileMode.CreateNew"/>) i dobija prava za sve.
	/// </summary>
	internal sealed class LinuxSingleInstanceGuard(string lockFilePath) : ISingleInstanceGuard
	{
		public const string LockFileName = "toppos-kiosk.lock";

		private const UnixFileMode __everyoneReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
														| UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;

		public LinuxSingleInstanceGuard() : this(Path.Combine(Directory.Exists("/run/lock") ? "/run/lock" : "/tmp", LockFileName)) { }

		/// <summary>
		/// <c>null</c> = fajl je zaključala druga instanca. Greška pristupa (prava, nepostojeći folder) nije „druga instanca“:
		/// baca <see cref="InvalidOperationException"/> sa putanjom.
		/// </summary>
		public IDisposable? TryAcquire()
		{
			try {
				// Drugi proces može da napravi fajl između pokušaja otvaranja i pravljenja; tada se ponovo otvara.
				for (int attempt = 0; attempt < 3; attempt++) {
					try {
						return new FileStream(lockFilePath, FileMode.Open, FileAccess.Read, FileShare.None);
					}
					catch (FileNotFoundException) {
					}
					try {
						var created = new FileStream(lockFilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
						AllowEveryone();
						return created;
					}
					catch (IOException) when (File.Exists(lockFilePath)) {
					}
				}
				throw new IOException($"Fajl {lockFilePath} se stalno pojavljuje i nestaje.");
			}
			catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException) {
				throw new InvalidOperationException($"Zaključavanje jedne instance nije moguće: nema pristupa {lockFilePath}.", ex);
			}
			catch (IOException) {
				return null;    // zaključan (flock) — druga instanca radi
			}
		}

		// umask skida prava grupi i ostalima; vraćaju se na fajlu koji je ovaj proces upravo napravio.
		private void AllowEveryone()
		{
			if (OperatingSystem.IsWindows()) {
				return;
			}
			try {
				File.SetUnixFileMode(lockFilePath, __everyoneReadWrite);
			}
			catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) {
				// zaključavanje radi i bez toga; drugi korisnik ga i dalje otvara samo za čitanje
			}
		}
	}
}