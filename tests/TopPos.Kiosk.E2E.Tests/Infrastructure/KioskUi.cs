using System.Diagnostics;
using System.Drawing;

namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>
	/// UI klijenta preko pristupačnosti platforme, po AutomationId-u (doc/plan-e2e-ui-testing.md u v2, sekcija 3.2):
	/// dugmad "&lt;Stranica&gt;.&lt;Komanda&gt;", koren stranice "Page.&lt;ImeStranice&gt;", tastatura "Keypad.0-9|Clear|Confirm",
	/// zaglavlje "Header.Home|Language|Rotate".
	/// v4: Windows — UI Automation (<see cref="UiaKioskUi"/>, v2 KioskUi); Linux — AT-SPI (faza E2). Scenariji vide samo ovaj
	/// interfejs; razlike platformi su u implementacijama.
	/// </summary>
	public abstract class KioskUi : IDisposable
	{
		public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

		/// <summary>Čeka glavni prozor klijenta (startup sam čeka skener i POS do ~20 s).</summary>
		public static Task<KioskUi> AttachAsync(Process process, TimeSpan timeout, Action? guard, Action<string> trace)
		{
#if WINDOWS
			return UiaKioskUi.AttachAsync(process, timeout, guard, trace);
#else
			throw new PlatformNotSupportedException("UI kioska na ovoj platformi još nije podržan (Linux: AT-SPI, faza E2).");
#endif
		}

		/// <summary>Čeka da element postoji, da je vidljiv i omogućen.</summary>
		public abstract Task WaitForAsync(string automationId, TimeSpan? timeout = null);

		/// <summary>Čeka da element nije prikazan (ne postoji ili je van ekrana).</summary>
		public abstract Task WaitForHiddenAsync(string automationId, TimeSpan? timeout = null);

		/// <summary>Čeka da je prikazana stranica (koren "Page.&lt;ime&gt;").</summary>
		public abstract Task WaitForPageAsync(string pageName, TimeSpan? timeout = null);

		/// <summary>Pritisak dugmeta kroz pristupačnost (pokreće Command; ne prolazi kroz miš).</summary>
		public abstract Task TapAsync(string automationId, TimeSpan? timeout = null);

		/// <summary>Kuca cifre na ekranskoj tastaturi i opciono potvrđuje.</summary>
		public async Task TypeOnKeypadAsync(string digits, bool confirm = true)
		{
			foreach (var digit in digits) {
				await TapAsync($"Keypad.{digit}").ConfigureAwait(false);
			}
			if (confirm) {
				await TapAsync("Keypad.Confirm").ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Pravi klik mišem (down/up) — za dugmad bez Command-a, npr. Home. <paramref name="hold"/> &gt; LongPressThreshold
		/// (3 s) daje dug pritisak.
		/// </summary>
		public abstract Task PressWithMouseAsync(string automationId, TimeSpan? hold = null, bool requireEnabled = true);

		/// <summary>Tačka za klik mišem (sredina kontrole), uzeta unapred — klik posle toga ne zove pristupačnost.</summary>
		public abstract Task<Point> GetClickablePointAsync(string automationId);

		/// <summary>Klik mišem na tačku; <paramref name="what"/> je samo za trace.</summary>
		public abstract Task ClickAtAsync(Point point, string what, TimeSpan? hold = null);

		/// <summary>Čeka tekst čiji natpis sadrži <paramref name="text"/>; vraća ceo natpis.</summary>
		public abstract Task<string> WaitForTextAsync(string text, TimeSpan? timeout = null);

		/// <summary>Vidljivi deo elementa na ekranu i glavni prozor.</summary>
		public abstract Task<(Rectangle Element, Rectangle Window)> GetBoundsAsync(string automationId);

		/// <summary>Natpis elementa; null = element nije u stablu.</summary>
		public abstract Task<string?> GetTextAsync(string automationId);

		public abstract Task<bool> IsEnabledAsync(string automationId);

		/// <summary>Snimak prozora (za dijagnostiku pri padu). Vraća putanju ili null.</summary>
		public abstract string? TryCaptureScreenshot(string path);

		public abstract void Dispose();
	}
}
