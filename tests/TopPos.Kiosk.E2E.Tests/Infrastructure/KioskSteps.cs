using TopPos.Client.WPF.Workflow;

namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>Koraci koji se ponavljaju u scenarijima. Svaki korak uzima kursor pre radnje i čeka posle nje.</summary>
	public static class KioskSteps
	{
		/// <summary>Start → "Preuzmi paket" → ekran za kod.</summary>
		public static async Task StartRecipientPickupAsync(this KioskSession kiosk)
		{
			var mark = kiosk.Log.Mark();
			await kiosk.Ui.TapAsync("StartPage.RecipientPickUpPackage").ConfigureAwait(false);
			await kiosk.WaitForStateAsync(WorkflowState.RecipientCodeInput, mark).ConfigureAwait(false);
			await kiosk.Ui.WaitForPageAsync("RecipientCodeInputPage").ConfigureAwait(false);
		}

		/// <summary>Kod → ekran za telefon → telefon. Vraća kursor uzet pre unosa telefona (posle njega ide provera na serveru).</summary>
		public static async Task<int> EnterCodeAndPhoneAsync(this KioskSession kiosk, SimPackage package)
		{
			var mark = kiosk.Log.Mark();
			await kiosk.Ui.TypeOnKeypadAsync(package.KodZaOtvaranje.ToString()).ConfigureAwait(false);
			await kiosk.WaitForStateAsync(WorkflowState.RecipientPhoneInput, mark).ConfigureAwait(false);
			await kiosk.Ui.WaitForPageAsync("RecipientPhoneInputPage").ConfigureAwait(false);

			mark = kiosk.Log.Mark();
			await kiosk.Ui.TypeOnKeypadAsync(package.TelefonPrimaoca).ConfigureAwait(false);
			return mark;
		}

		/// <summary>
		/// Spremnost za zatvaranje vrata: vrata otključana u simulatoru I prikazana stranica koja se postavlja tek posle
		/// uspešne provere otvaranja (prelaz se loguje pre OnEntry, pa sam prelaz nije dovoljan — plan, sekcija 3.5).
		/// </summary>
		public static async Task WaitForDoorOpenAsync(this KioskSession kiosk, int door, string pageName)
		{
			await Poll.UntilAsync(async () => !(await kiosk.Locker.GetDoorAsync(door).ConfigureAwait(false)).IsLocked,
									KioskUi.DefaultTimeout, $"vrata {door} otključana", kiosk.EnsureClientAlive).ConfigureAwait(false);
			await kiosk.Ui.WaitForPageAsync(pageName).ConfigureAwait(false);
			kiosk.Trace($"vrata {door} otvorena, stranica {pageName}");
		}

		/// <summary>Zatvara vrata i čeka prelaz u <paramref name="expected"/>.</summary>
		public static async Task<Transition> CloseDoorAsync(this KioskSession kiosk, int door, WorkflowState expected, TimeSpan? timeout = null)
		{
			var mark = kiosk.Log.Mark();
			await kiosk.Locker.CloseDoorAsync(door).ConfigureAwait(false);
			kiosk.Trace($"vrata {door} zatvorena");
			return await kiosk.WaitForStateAsync(expected, mark, timeout ?? TimeSpan.FromSeconds(30)).ConfigureAwait(false);
		}

		/// <summary>Skeniranje barkoda (LockerSimulator → COM → klijent) i čekanje prelaza.</summary>
		public static async Task<Transition> ScanAsync(this KioskSession kiosk, string barcode, WorkflowState expected, TimeSpan? timeout = null)
		{
			var mark = kiosk.Log.Mark();
			await kiosk.Locker.ScanBarcodeAsync(barcode).ConfigureAwait(false);
			kiosk.Trace($"skeniran barkod {barcode}");
			return await kiosk.WaitForStateAsync(expected, mark, timeout).ConfigureAwait(false);
		}

		/// <summary>Prvo uspešno otključavanje posle kursora — vrata koja je kiosk (server) izabrao.</summary>
		public static Task<UnlockEntry> WaitForUnlockAsync(this KioskSession kiosk, long unlockMark)
			=> Poll.UntilAsync(async () => (await kiosk.Locker.GetUnlocksAsync(unlockMark).ConfigureAwait(false)).FirstOrDefault(u => u.Unlocked),
								KioskUi.DefaultTimeout, $"otključavanje vrata posle #{unlockMark}", kiosk.EnsureClientAlive);

		/// <summary>Čeka da POS simulator uđe u zadatu fazu (npr. WaitingForCard — tek tada kartica „važi").</summary>
		public static async Task WaitForPosPhaseAsync(this KioskSession kiosk, string phase, TimeSpan? timeout = null)
		{
			await Poll.UntilAsync(async () => (await kiosk.Locker.GetPosAsync().ConfigureAwait(false)).Phase == phase,
									timeout ?? KioskUi.DefaultTimeout, $"POS u fazi {phase}", kiosk.EnsureClientAlive).ConfigureAwait(false);
			kiosk.Trace($"POS: {phase}");
		}

		/// <summary>
		/// Čeka liniju loga posle kursora — znak spremnosti kad se stranica ne menja (povratak na isti ekran posle neuspeha),
		/// pa <see cref="KioskUi.WaitForPageAsync"/> ne govori ništa o tome da je OnEntry završen.
		/// </summary>
		public static async Task WaitForLogLineAsync(this KioskSession kiosk, string text, int after, TimeSpan? timeout = null)
		{
			await Poll.UntilAsync(() => kiosk.Log.LinesAfter(after, text).Count > 0,
									timeout ?? KioskUi.DefaultTimeout, $"linija loga \"{text}\" (posle linije {after})", kiosk.EnsureClientAlive).ConfigureAwait(false);
			kiosk.Trace($"log: {text}");
		}

		/// <summary>
		/// Skener ponovo prihvata barkod (BarcodeScanTracker je zakačio handler) posle prelaza <paramref name="returned"/> —
		/// npr. povratak na skeniranje posle odbijenog barkoda. Barkod poslat ranije ode u handler koji je već skinut.
		/// </summary>
		public static Task WaitForScannerReadyAsync(this KioskSession kiosk, Transition returned)
			=> kiosk.WaitForLogLineAsync("BarcodeScanTracker: TrackingCountdown starting", returned.Line);

		/// <summary>
		/// Ignorisani trigeri posle kursora moraju biti tačno <paramref name="expected"/> (redom). Čeka da se pojave: zakasneli
		/// ishod se loguje tek kad se OnEntry završi, posle Reset-a koji ga je preduhitrio.
		/// </summary>
		public static async Task AssertIgnoredTriggersAsync(this KioskSession kiosk, int after, params (string Trigger, WorkflowState State)[] expected)
		{
			if (expected.Length > 0) {
				await Poll.UntilAsync(() => kiosk.Log.IgnoredTriggersAfter(after).Count >= expected.Length,
										KioskUi.DefaultTimeout, $"ignorisani trigeri {string.Join(", ", expected)}", kiosk.EnsureClientAlive).ConfigureAwait(false);
			}
			var ignored = kiosk.Log.IgnoredTriggersAfter(after);
			kiosk.Trace($"ignorisani trigeri: {(ignored.Count == 0 ? "nema" : string.Join(", ", ignored))}");
			Xunit.Assert.Equal(expected, ignored.Select(i => (i.Trigger, i.State)));
		}

		/// <summary>
		/// Home dok se čeka server: po vremenima iz loga klijenta (ne zavisi od brzine UIA-a) Home je stigao do klijenta
		/// pre odgovora, a Reset tek kad je odgovor stigao. <paramref name="entered"/> je ulazak u stanje provere (zahtev
		/// kreće u njegovom OnEntry), <paramref name="serverDelay"/> zadato kašnjenje servera.
		/// </summary>
		public static void AssertResetWaitedForServer(this KioskSession kiosk, Transition entered, int homeMark, Transition reset, TimeSpan serverDelay)
		{
			var homeLine = kiosk.Log.FirstLineAfter(homeMark, "executing GoToStartPageCommand")
							?? throw new E2EException("Home nije stigao do klijenta (nema GoToStartPageCommand u logu).");
			var sent = kiosk.Log.TimeOf(entered.Line);
			var home = kiosk.Log.TimeOf(homeLine) - sent;
			var resetAfter = kiosk.Log.TimeOf(reset.Line) - sent;
			kiosk.Trace($"od slanja zahteva: Home {home.TotalMilliseconds:0} ms, Reset {resetAfter.TotalMilliseconds:0} ms (kašnjenje servera {serverDelay.TotalMilliseconds:0} ms)");
			Xunit.Assert.True(home < serverDelay - TimeSpan.FromMilliseconds(300),
								$"Home je stigao {home.TotalMilliseconds:0} ms posle slanja — praktično uz odgovor; test nije proverio zakasneli odgovor.");
			Xunit.Assert.True(resetAfter >= serverDelay - TimeSpan.FromMilliseconds(200),
								$"Reset je stigao {resetAfter.TotalMilliseconds:0} ms posle slanja — pre odgovora servera ({serverDelay.TotalMilliseconds:0} ms).");
		}

		/// <summary>
		/// Odustani dok se čeka server: isti triger šalju i dugme i activator (posle prekinutog zahteva), pa drugi primerak
		/// sme biti ignorisan na Start-u — najviše jedan, i ništa drugo.
		/// </summary>
		public static void AssertAtMostDuplicateCancel(IReadOnlyList<IgnoredTrigger> ignored, string cancelTrigger)
		{
			Xunit.Assert.True(ignored.Count <= 1, $"Očekivan najviše jedan ignorisan {cancelTrigger}, a ignorisani su: {string.Join(", ", ignored)}");
			Xunit.Assert.All(ignored, i => Xunit.Assert.Equal((cancelTrigger, WorkflowState.Start), (i.Trigger, i.State)));
		}

		/// <summary>
		/// Klijent mora koristiti SimulatedPosClient sa zadatim scenarijem (<see cref="KioskOptions.WithPosSimulator"/>) —
		/// bez ovoga bi test tiho radio na pravom POS-u (LockerSimulator preko com0com).
		/// </summary>
		public static void AssertPosSimulator(this KioskSession kiosk, string scenario)
		{
			var warning = kiosk.Log.LinesAfter(0, "koristi se SIMULATOR").FirstOrDefault()
							?? throw new E2EException($"POS simulator nije uključen ({KioskOptions.PosSimulatorFile} nije pročitan) — klijent koristi pravi POS.");
			if (!warning.Contains($"scenario={scenario}", StringComparison.Ordinal)) {
				throw new E2EException($"POS simulator radi sa pogrešnim scenarijem (očekivan {scenario}): {warning}");
			}
			kiosk.Trace($"POS simulator: scenario={scenario}");
		}

		/// <summary>Otključavanja posle kursora moraju biti samo za <paramref name="door"/> (i bar jedno).</summary>
		public static async Task AssertOnlyDoorUnlockedAsync(this KioskSession kiosk, long unlockMark, int door)
		{
			var unlocks = await kiosk.Locker.GetUnlocksAsync(unlockMark).ConfigureAwait(false);
			Xunit.Assert.NotEmpty(unlocks);
			Xunit.Assert.All(unlocks, u => Xunit.Assert.Equal(door, u.Door));
		}
	}
}
