using System.Diagnostics;
using System.Drawing;
using System.Runtime.Versioning;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>
	/// v4: Windows implementacija <see cref="KioskUi"/> (v2 KioskUi).
	/// UI klijenta preko UI Automation-a (FlaUI). Kontrole se traže po AutomationId-u (doc/plan-e2e-ui-testing.md,
	/// sekcija 3.2): dugmad "&lt;Stranica&gt;.&lt;Komanda&gt;", koren stranice "Page.&lt;Ime&gt;", tastatura "Keypad.N",
	/// zaglavlje "Header.Home|Language|Rotate".
	///
	/// Svaki UIA poziv ima rok (<see cref="__callTimeout"/>): poziv koji visi ne sme da zaglavi test mimo timeout-a čekanja.
	/// </summary>
	[SupportedOSPlatform("windows")]
	public sealed class UiaKioskUi : KioskUi
	{
		private static readonly TimeSpan __callTimeout = TimeSpan.FromSeconds(10);

		private readonly UIA3Automation _automation = new() {
			ConnectionTimeout = TimeSpan.FromSeconds(5),
			TransactionTimeout = TimeSpan.FromSeconds(5),
		};
		private readonly Action? _guard;
		private readonly Action<string> _trace;
		private FlaUI.Core.Application? _application;

		private UiaKioskUi(Action? guard, Action<string> trace)
		{
			_guard = guard;
			_trace = trace;
		}

		public Window MainWindow { get; private set; } = default!;

		/// <summary>Čeka glavni prozor klijenta (startup sam čeka skener i POS do ~20 s).</summary>
		public static new async Task<KioskUi> AttachAsync(Process process, TimeSpan timeout, Action? guard, Action<string> trace)
		{
			var ui = new UiaKioskUi(guard, trace);
			ui.MainWindow = await Poll.UntilAsync(() => ui.CallAsync<Window?>(() => {
															// Po PID-u: FlaUI pravi sopstvenu ručku procesa (objekat iz Process.Start ostaje
															// za EnsureClientAlive). Attach čita MainModule, koji tek pokrenut proces još nema
															// (NRE u FlaUI-ju) — zato se ponavlja u Poll-u.
															ui._application ??= FlaUI.Core.Application.Attach(process.Id);
															return ui._application.GetMainWindow(ui._automation, TimeSpan.FromMilliseconds(500));
														}, "Attach/GetMainWindow"),
													timeout, "glavni prozor klijenta", guard).ConfigureAwait(false);
			trace($"UI: glavni prozor \"{ui.MainWindow.Title}\"");
			return ui;
		}

		public Task<AutomationElement?> FindAsync(string automationId)
			=> CallAsync<AutomationElement?>(() => MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), $"Find {automationId}");

		/// <summary>Čeka da element postoji, da je vidljiv i omogućen.</summary>
		public override Task WaitForAsync(string automationId, TimeSpan? timeout = null) => WaitForElementAsync(automationId, timeout);

		private Task<AutomationElement> WaitForElementAsync(string automationId, TimeSpan? timeout = null)
			=> Poll.UntilAsync(async () => await FindAsync(automationId).ConfigureAwait(false) is { } element
												&& await CallAsync(() => element.IsEnabled && !element.IsOffscreen, $"IsEnabled {automationId}").ConfigureAwait(false)
											? element : null,
								timeout ?? DefaultTimeout, $"element \"{automationId}\" (vidljiv i omogućen)", _guard);

		/// <summary>Čeka da element nije prikazan: ne postoji u UIA stablu ili je van ekrana (npr. Visibility=Collapsed).</summary>
		public override Task WaitForHiddenAsync(string automationId, TimeSpan? timeout = null)
			=> Poll.UntilAsync(async () => await FindAsync(automationId).ConfigureAwait(false) is not { } element
												|| await CallAsync(() => element.IsOffscreen, $"IsOffscreen {automationId}").ConfigureAwait(false),
								timeout ?? DefaultTimeout, $"element \"{automationId}\" sakriven", _guard);

		/// <summary>Čeka da je prikazana stranica (koren "Page.&lt;ime&gt;").</summary>
		public override async Task WaitForPageAsync(string pageName, TimeSpan? timeout = null)
		{
			await Poll.UntilAsync(() => FindAsync($"Page.{pageName}"), timeout ?? DefaultTimeout, $"stranica {pageName}", _guard).ConfigureAwait(false);
			_trace($"UI: stranica {pageName}");
		}

		/// <summary>Pritisak dugmeta preko Invoke pattern-a (pokreće Command; ne prolazi kroz miš).</summary>
		public override async Task TapAsync(string automationId, TimeSpan? timeout = null)
		{
			var element = await WaitForElementAsync(automationId, timeout).ConfigureAwait(false);
			await CallAsync(() => { element.AsButton().Invoke(); return true; }, $"Invoke {automationId}").ConfigureAwait(false);
			_trace($"UI: tap {automationId}");
		}

		/// <summary>
		/// Pravi klik mišem (down/up) — za dugmad bez Command-a, npr. Home (ButtonPressBehavior na PreviewMouse događajima).
		/// <paramref name="hold"/> &gt; LongPressThreshold (3 s) daje dug pritisak.
		/// </summary>
		public override async Task PressWithMouseAsync(string automationId, TimeSpan? hold = null, bool requireEnabled = true)
		{
			var element = requireEnabled
				? await WaitForElementAsync(automationId).ConfigureAwait(false)
				: await Poll.UntilAsync(() => FindAsync(automationId), DefaultTimeout, $"element \"{automationId}\"", _guard).ConfigureAwait(false);
			var point = await CenterOfAsync(element, automationId).ConfigureAwait(false);
			await ClickAtAsync(point, automationId, hold).ConfigureAwait(false);
		}

		/// <summary>
		/// Tačka za klik mišem (sredina kontrole), uzeta unapred — klik posle toga ne zove UIA. Kontrola mora ostati na istom
		/// mestu (npr. Header.Home).
		/// </summary>
		public override async Task<Point> GetClickablePointAsync(string automationId)
		{
			var element = await WaitForElementAsync(automationId).ConfigureAwait(false);
			return await CenterOfAsync(element, automationId).ConfigureAwait(false);
		}

		/// <summary>
		/// Klik mišem na tačku (bez UIA poziva); <paramref name="what"/> je samo za trace. Kursor se postavlja odmah:
		/// Mouse.MoveTo animira pomeranje od trenutne pozicije (~1,7 s za ~850 px), pa bi klik zavisio od toga gde je kursor bio.
		/// </summary>
		public override async Task ClickAtAsync(Point point, string what, TimeSpan? hold = null)
		{
			Mouse.Position = point;
			Mouse.Down(MouseButton.Left);
			await Task.Delay(hold ?? TimeSpan.FromMilliseconds(80)).ConfigureAwait(false);
			Mouse.Up(MouseButton.Left);
			_trace($"UI: miš {what} @ ({point.X}, {point.Y}) ({(hold ?? TimeSpan.FromMilliseconds(80)).TotalMilliseconds:0} ms)");
		}

		/// <summary>
		/// Sredina kontrole na ekranu. Ne GetClickablePoint: za Home je vraćao tačku na sredini gornje ivice dugmeta, gde klik
		/// lako promaši.
		/// </summary>
		private Task<Point> CenterOfAsync(AutomationElement element, string automationId)
			=> CallAsync(() => {
							MainWindow.SetForeground();
							var bounds = element.BoundingRectangle;
							return new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
						}, $"BoundingRectangle {automationId}");

		/// <summary>Esc glavnom prozoru (u e2e konfiguraciji gasi klijent uredno, kao zatvaranje programa).</summary>
		public async Task PressEscapeAsync()
		{
			await CallAsync(() => {
							MainWindow.SetForeground();
							Keyboard.Type(VirtualKeyShort.ESCAPE);
							return true;
						}, "Esc").ConfigureAwait(false);
			_trace("UI: taster Esc");
		}

		/// <summary>
		/// Čeka tekst (TextBlock) čiji natpis sadrži <paramref name="text"/> — za tekst bez AutomationId-ja (npr. kontakt
		/// podrške u podnožju). Vraća ceo natpis.
		/// </summary>
		public override async Task<string> WaitForTextAsync(string text, TimeSpan? timeout = null)
		{
			var found = await Poll.UntilAsync(() => CallAsync(() => MainWindow.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
																			.Select(e => e.Name)
																			.FirstOrDefault(n => n?.Contains(text, StringComparison.Ordinal) == true),
															$"FindText {text}"),
												timeout ?? DefaultTimeout, $"tekst \"{text}\"", _guard).ConfigureAwait(false);
			_trace($"UI: tekst \"{found.ReplaceLineEndings(" ")}\"");
			return found;
		}

		/// <summary>
		/// Vidljivi deo elementa na ekranu (WPF ga seče po roditeljima) i glavni prozor. Element ne mora biti omogućen
		/// (npr. Keypad.Confirm pre unosa koda).
		/// </summary>
		public override async Task<(Rectangle Element, Rectangle Window)> GetBoundsAsync(string automationId)
		{
			var element = await Poll.UntilAsync(() => FindAsync(automationId), DefaultTimeout, $"element \"{automationId}\"", _guard).ConfigureAwait(false);
			return await CallAsync(() => (element.BoundingRectangle, MainWindow.BoundingRectangle), $"BoundingRectangle {automationId}").ConfigureAwait(false);
		}

		/// <summary>Natpis elementa (TextBlock: Text); null = element nije u UIA stablu.</summary>
		public override async Task<string?> GetTextAsync(string automationId)
			=> await FindAsync(automationId).ConfigureAwait(false) is { } element
				? await CallAsync(() => element.Name, $"Name {automationId}").ConfigureAwait(false)
				: null;

		public override async Task<bool> IsEnabledAsync(string automationId)
			=> await FindAsync(automationId).ConfigureAwait(false) is { } element
				&& await CallAsync(() => element.IsEnabled, $"IsEnabled {automationId}").ConfigureAwait(false);

		/// <summary>Snimak prozora (za dijagnostiku pri padu). Vraća putanju ili null.</summary>
		public override string? TryCaptureScreenshot(string path)
		{
			try {
				Task.Run(() => Capture.Element(MainWindow).ToFile(path)).Wait(__callTimeout);
				return File.Exists(path) ? path : null;
			}
			catch (Exception) {
				return null;
			}
		}

		/// <summary>UIA poziv van test niti, sa rokom — visi li, test dobija izuzetak umesto da stoji zauvek.</summary>
		private async Task<T> CallAsync<T>(Func<T> call, string what)
		{
			try {
				return await Task.Run(call).WaitAsync(__callTimeout).ConfigureAwait(false);
			}
			catch (TimeoutException) {
				_trace($"UI: {what} nije završen za {__callTimeout.TotalSeconds:0} s");
				// TimeoutException (ne E2EException): Poll ga tretira kao neuspelu probu i pokušava ponovo do svog roka.
				throw new TimeoutException($"UIA poziv \"{what}\" nije završen za {__callTimeout.TotalSeconds:0} s");
			}
		}

		// Application se ne dispose-uje: proces gasi KioskSession (kill + čekanje izlaska).
		public override void Dispose() => _automation.Dispose();
	}
}
