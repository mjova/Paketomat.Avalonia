using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Flatsoft.Common;
using TopPos.Kiosk.UI.Views;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.AgentCommunication;
using TopPos.Kiosk.UI.Controls;
using TopPos.Kiosk.ViewModels;
using TopPos.Service;
using Xunit;

namespace TopPos.Kiosk.UI.Tests
{
	/// <summary>
	/// Svaki ekran toka (primalac, prijava kurira) se prikazuje sa pravim ViewModel-om, jezicima i resursima aplikacije (stilovi, crteži, slike)
	/// i iscrtava (Skia). Sa <c>TOPPOS_UI_SNAPSHOTS</c> (folder) snimci se čuvaju za vizuelnu proveru, npr. na Linuxu bez ekrana.
	/// </summary>
	public class ViewRenderTests
	{
		private static string BrandDirectory(string brand) => Path.Combine(AppContext.BaseDirectory, "Brands", brand);

		private static LanguageManager Languages(string language = "sr", string brand = "DailyExpress")
		{
			var languages = new LanguageManager(null);
			languages.LoadLanguageResources(Path.Combine(AppContext.BaseDirectory, "Languages"), ".lang", language);
			languages.LoadBrandOverrides(BrandDirectory(brand), ".lang");
			return languages;
		}

		private static BarcodeScannerClient Scanner()
			=> new(new BarcodeScannerConfiguration { ComPort = new ComPortConfiguration { PortName = "COM-NONE" } }, logger: null, CancellationToken.None);

		public static TheoryData<string> Pages => new() { "Start", "CodeInput", "PhoneInput", "Payment", "FiscalReceipt", "PackagePickup", "DeviceStatus",
													"CourierManualLogin", "CourierBoxAvailabilityLoading", "CourierBoxAvailability", "CourierPickupConfirmation",
													"CourierPickupPrompt", "CourierPackagePickup", "CourierPackagePickupCloseDoor", "CourierBarcodeScanPickup", "CourierBarcodeScanDeposit",
													"CourierDepositPrompt", "CourierPackageDeposit", "CourierDepositConfirmation", "DepositConfirmationFailedUnknown", "DepositConfirmationFailedRejected",
													"SenderBarcodeScan", "SenderPackageDeposit", "StartWithDeposit",
													"RecipientRemoteConfirmation", "RemoteServiceLogin", "RemoteServiceActive" };

		private static (object ViewModel, Type View) Create(string page, LanguageManager languages) => page switch {
			"Start" => (new StartViewModel(Scanner(), "DX000111", languages, null), typeof(StartView)),
			"CodeInput" => (new RecipientCodeInputViewModel(Scanner(), 6, 11, languages, null), typeof(RecipientCodeInputView)),
			"PhoneInput" => (new RecipientPhoneInputViewModel(6, 11, languages, null), typeof(RecipientPhoneInputView)),
			"Payment" => (new RecipientPaymentViewModel(languages, "RecipientPaymentPage.ConnectingDevice", "RecipientPaymentPage.AmountInfoDetailsTemplate", 2, "RSD", null) { Amount = 1500m },
						  typeof(RecipientPaymentView)),
			"FiscalReceipt" => (new RecipientFiscalReceiptViewModel(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), languages, null)
						{ FiscalReceiptImage = Flatsoft.Common.UI.QrCodeService.GenerateQRCode("https://suf.purs.gov.rs/v/?vl=test") },
						typeof(RecipientFiscalReceiptView)),
			"PackagePickup" => (new RecipientPackagePickupViewModel(languages, null) { PickupInstructions = "Molimo Vas, preuzmite paket iz pretinca 5 i zatvorite vrata" },
						typeof(RecipientPackagePickupView)),
			// sloj preko glavnog prozora (dug pritisak na Home), posle provera: bez skenera i POS-a obe se završe odmah
			"DeviceStatus" => (CheckedDeviceStatus(languages), typeof(DeviceStatusView)),
			"CourierManualLogin" => (new CourierManualLoginViewModel(2, 11, languages, null), typeof(CourierManualLoginView)),
			"CourierBoxAvailabilityLoading" => (new CourierLockerBoxAvailabilityViewModel(TimeSpan.FromSeconds(20), languages, null)
						{ StatusMessage = languages.CurrentLanguage!.TextEntries["CourierLockerBoxAvailabilityPage.FetchingLockerBoxAvailability"] },
						typeof(CourierLockerBoxAvailabilityView)),
			"CourierBoxAvailability" => (new CourierLockerBoxAvailabilityViewModel(TimeSpan.FromSeconds(20), languages, null) {
							BoxSizeAvailabilityStatuses = [
								new StatusPoVeliciniPozicije { Velicina = "S", Slobodno = 12, Zauzeto = 3, ZaPreuzimanje = 1 },
								new StatusPoVeliciniPozicije { Velicina = "M", Slobodno = 4, Zauzeto = 8, ZaPreuzimanje = 0 },
								new StatusPoVeliciniPozicije { Velicina = "L", Slobodno = 0, Zauzeto = 6, ZaPreuzimanje = 2 }
							]
						},
						typeof(CourierLockerBoxAvailabilityView)),
			"CourierPickupConfirmation" => (new CourierPickupConfirmationViewModel(languages, null), typeof(CourierPickupConfirmationView)),
			"CourierPickupPrompt" => (new CourierPickupPromptViewModel(languages, null) { RemainingExpiredPackagesCount = 3 }, typeof(CourierPickupPromptView)),
			"CourierPackagePickup" => (new CourierPackagePickupViewModel(languages, null), typeof(CourierPackagePickupView)),
			"CourierPackagePickupCloseDoor" => (new CourierPackagePickupViewModel(languages, null) { ShowCloseDoorMessage = true }, typeof(CourierPackagePickupView)),
			"CourierBarcodeScanPickup" => (new CourierBarcodeScanViewModel(languages, null) { Barcode = "DX123456789RS", PackagePickup = true },
						typeof(CourierBarcodeScanView)),
			"CourierBarcodeScanDeposit" => (new CourierBarcodeScanViewModel(languages, null), typeof(CourierBarcodeScanView)),
			"CourierDepositPrompt" => (new CourierDepositPromptViewModel(languages, null) { ExpiredPackagesPickedUp = true }, typeof(CourierDepositPromptView)),
			"CourierPackageDeposit" => (new CourierPackageDepositViewModel(languages, null) { Barcode = "DX123456789RS" }, typeof(CourierPackageDepositView)),
			"CourierDepositConfirmation" => (new CourierDepositConfirmationViewModel(languages, null), typeof(CourierDepositConfirmationView)),
			"DepositConfirmationFailedUnknown" => (FailedDeposit(languages, DepositConfirmResult.Unknown, null), typeof(DepositConfirmationFailedView)),
			"StartWithDeposit" => (new StartViewModel(Scanner(), "DX000111", languages, null) { IsPackageDepositionEnabled = true }, typeof(StartView)),
			"RecipientRemoteConfirmation" => (RemoteConfirmation(languages), typeof(RecipientRemoteConfirmationView)),
			"RemoteServiceLogin" => (RemoteService(languages, loggedIn: false), typeof(RemoteServiceView)),
			"RemoteServiceActive" => (RemoteService(languages, loggedIn: true), typeof(RemoteServiceView)),
			"SenderBarcodeScan" => (new SenderBarcodeScanViewModel(languages, null), typeof(SenderBarcodeScanView)),
			"SenderPackageDeposit" => (new SenderPackageDepositViewModel(languages, null) { Barcode = "DX123456789RS" }, typeof(SenderPackageDepositView)),
			"DepositConfirmationFailedRejected" => (FailedDeposit(languages, DepositConfirmResult.Rejected, "Pozicija nije rezervisana."), typeof(DepositConfirmationFailedView)),
			_ => throw new ArgumentOutOfRangeException(nameof(page))
		};

		private static RecipientRemoteConfirmationViewModel RemoteConfirmation(LanguageManager languages)
		{
			var vm = new RecipientRemoteConfirmationViewModel(languages, null);
			vm.Prepare(TimeSpan.FromSeconds(120), 3);
			vm.ShowWrongCode(2);
			return vm;
		}

		/// <summary>Servisna sesija Agenta (bez transporta): prijava servisera ili raspored sa vratima u različitim stanjima.</summary>
		private static RemoteServiceViewModel RemoteService(LanguageManager languages, bool loggedIn)
		{
			static LayoutBox Box(int door, string size, int column, int row) => new() { DoorNumber = door, Size = size, Column = column, Row = row };
			var journal = new AgentOperationJournal(Directory.CreateTempSubdirectory("toppos-render-").FullName, logger: null);
			journal.Initialize(RemoteServiceManager.Recoverers);
			var session = new RemoteServiceSession(new RemoteServiceSessionOptions {
															SessionId = "s1",
															PresenceCode = "4729",
															IdleTimeout = TimeSpan.FromSeconds(600),
															LockerBoxCount = 16,
															Layout = [Box(1, BoxSizes.L, 1, 1), Box(2, BoxSizes.S, 1, 2), Box(3, BoxSizes.M, 1, 3),
																	  Box(9, BoxSizes.M, 2, 1), Box(10, BoxSizes.M, 2, 2), Box(11, BoxSizes.S, 2, 3)],
														}, new RenderController(), journal, null, logger: null);
			Assert.True(session.StartAsync().GetAwaiter().GetResult());
			var vm = new RemoteServiceViewModel(Scanner(), languages, "DX000111", null);
			vm.Bind(session);
			if (loggedIn) {
				Assert.Equal(PresenceCodeResult.Accepted, session.SubmitPresenceCodeAsync("4729").GetAwaiter().GetResult());
				var opened = session.StartOpenDoorsAsync([1, 9], all: false, DoorOpenSources.Screen).GetAwaiter().GetResult();
				Assert.Equal(AgentResultCodes.InProgress, opened.ResultCode);
				SpinWait.SpinUntil(() => !session.IsOpening, TimeSpan.FromSeconds(5));
				vm.Bind(session);   // osveži prikaz posle otvaranja (u kiosku: Changed i tajmer)
			}
			return vm;
		}

		/// <summary>Kontroler za snimak: vrata 1 se otvaraju, vrata 9 ne (zatvorena posle komande).</summary>
		private sealed class RenderController : TopPos.LockController.ILockControllerClient
		{
			public int NumberOfDoorsPerController => 8;
			public ValueTask<TopPos.LockController.DoorOpenAttempt> OpenDoorOnce(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
				=> ValueTask.FromResult(new TopPos.LockController.DoorOpenAttempt(true, doorNumber == 1 ? TopPos.LockController.DoorStatusCode.Open : TopPos.LockController.DoorStatusCode.Closed));
			public ValueTask<TopPos.LockController.DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
				=> ValueTask.FromResult<TopPos.LockController.DoorStatusCode[]?>(Enumerable.Repeat(TopPos.LockController.DoorStatusCode.Closed, 8).ToArray());
			public ValueTask<TopPos.LockController.DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, CancellationToken cancellationToken) => CheckDoorMultiple(moduleNumber, null, cancellationToken);
			public ValueTask<TopPos.LockController.DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => CheckDoorMultiple(moduleNumber, receiveTimeout, cancellationToken);
			public ValueTask<TopPos.LockController.DoorStatusCode?> OpenDoorSingle(int doorNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<TopPos.LockController.DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<TopPos.LockController.DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<TopPos.LockController.DoorStatusCode?> CheckDoorSingle(int doorNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<TopPos.LockController.DoorStatusCode?> CheckDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<TopPos.LockController.DoorStatusCode?> CheckDoorSingle(int doorNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
		}

		private static DepositConfirmationFailedViewModel FailedDeposit(LanguageManager languages, DepositConfirmResult result, string? serverMessage)
		{
			var vm = new DepositConfirmationFailedViewModel(languages, null);
			vm.SetResult(result, serverMessage);
			return vm;
		}

		private static DeviceStatusViewModel CheckedDeviceStatus(LanguageManager languages)
		{
			var vm = new DeviceStatusViewModel(barcodeScannerClient: null, posClient: null,
												new ApplicationContext { VersionLabel = "1.3.4", GitHash = "0123456789abcdef" }, languages, null) {
				CheckDelay = TimeSpan.Zero
			};
			Assert.True(vm.Initialize().IsCompleted);
			return vm;
		}

		public static TheoryData<string> Brands => new() { "Ananas", "CityExpress", "DailyExpress", "EuroExpress", "MNM", "Posta", "TopPos", "XExpress" };

		// Brend iz konfiguracije (General.Brand) mora postojati u paketu; bez foldera program staje na startu.
		[AvaloniaTheory]
		[MemberData(nameof(Brands))]
		public void Brand_theme_and_language_overrides_load(string brand)
		{
			var brandDirectory = Path.Combine(AppContext.BaseDirectory, "Brands", brand);
			var resources = new ResourceDictionary();

			BrandTheme.Apply(brandDirectory, resources);
			var languages = new LanguageManager(null);
			languages.LoadLanguageResources(Path.Combine(AppContext.BaseDirectory, "Languages"), ".lang", "sr");
			languages.LoadBrandOverrides(brandDirectory, ".lang");

			// Isti ključevi u svakom brendu (v2 tema): prikazi koriste DynamicResource, pa ključ koji nedostaje ne javlja grešku.
			Assert.Equal(__brandKeys, resources.Keys.Cast<string>().Order());
			Assert.IsType<Avalonia.Media.Imaging.Bitmap>(resources["ApplicationLogo"]);
			Assert.All(__brandKeys.Where(key => key != "ApplicationLogo"), key => Assert.IsType<Avalonia.Media.SolidColorBrush>(resources[key]));
		}

		/// <summary>Brendovi sa svojim tekstovima (v2 <c>Brands/&lt;Brand&gt;/sr.lang</c>, <c>en.lang</c>); ostali koriste osnovne.</summary>
		private static readonly string[] __brandsWithTexts = ["DailyExpress", "EuroExpress", "Posta", "TopPos"];

		// Tekstovi brenda (npr. Pošta: osmocifreni kod) postoje za oba jezika, menjaju postojeće ključeve i stvarno se primenjuju;
		// neispravan ili izostavljen fajl bi tiho ostavio osnovni tekst.
		[Theory]
		[MemberData(nameof(Brands))]
		public void Brand_texts_override_base_texts(string brand)
		{
			foreach (var language in new[] { "sr", "en" }) {
				var file = Path.Combine(BrandDirectory(brand), $"{language}.lang");
				Assert.Equal(__brandsWithTexts.Contains(brand), File.Exists(file));
				if (!File.Exists(file)) {
					continue;
				}
				var overrides = System.Text.Json.JsonSerializer.Deserialize<LanguageData>(File.ReadAllText(file))!.TextEntries;
				var baseTexts = new LanguageManager(null);
				baseTexts.LoadLanguageResources(Path.Combine(AppContext.BaseDirectory, "Languages"), ".lang", language);
				var texts = Languages(language, brand).CurrentLanguage!;

				Assert.Equal(language, texts.LanguageId);
				Assert.NotEmpty(overrides);
				Assert.All(overrides, entry => {
					Assert.True(baseTexts.CurrentLanguage!.TextEntries.ContainsKey(entry.Key), $"{brand} {language}: ključ {entry.Key} ne postoji u osnovnim tekstovima");
					Assert.Equal(entry.Value, texts.TextEntries[entry.Key]);
				});
			}
		}

		private static readonly string[] __brandKeys = [
			"AccentedForegroundColor1", "AccentedForegroundColor2", "ApplicationBackgroundColor", "ApplicationLogo", "InformationBackgroundColor",
			"InformationForegroundColor", "PopupMessageBackgroundColor", "PopupMessageForegroundColor", "StepsBackgroundColor"
		];

		public static TheoryData<string, string> PagesInBrands {
			get {
				var data = new TheoryData<string, string>();
				foreach (var page in Pages) {
					foreach (var brand in Brands) {
						data.Add(page.Data, brand.Data);
					}
				}
				return data;
			}
		}

		// Svaka stranica u svakom brendu (boje, logo, tekstovi brenda), bez grešaka povezivanja. Snimci: brands/{brand}.{page}.png.
		[AvaloniaTheory]
		[MemberData(nameof(PagesInBrands))]
		public void Page_renders_in_brand(string page, string brand)
		{
			using var errors = new BindingErrors();
			BrandTheme.Apply(BrandDirectory(brand), App.Current!.Resources);
			var (viewModel, viewType) = Create(page, Languages(brand: brand));
			var host = new ContentControl { Content = viewModel };
			var window = new Window { Width = 1280, Height = 800, Content = host, Background = (Avalonia.Media.IBrush) App.Current.Resources["ApplicationBackgroundColor"]! };
			window.Show();
			Dispatcher.UIThread.RunJobs();

			var frame = window.CaptureRenderedFrame();

			Assert.NotNull(frame);
			Assert.IsType(viewType, host.Presenter?.Child);
			Assert.Empty(errors.Messages);
			var folder = Environment.GetEnvironmentVariable("TOPPOS_UI_SNAPSHOTS");
			if (!string.IsNullOrEmpty(folder)) {
				Directory.CreateDirectory(Path.Combine(folder, "brands"));
				frame!.Save(Path.Combine(folder, "brands", $"{brand}.{page}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
			}
		}

		[AvaloniaTheory]
		[MemberData(nameof(Pages))]
		public void Page_renders_with_app_resources(string page)
		{
			BrandTheme.Apply(Path.Combine(AppContext.BaseDirectory, "Brands", "DailyExpress"), App.Current!.Resources);
			var (viewModel, viewType) = Create(page, Languages());
			var host = new ContentControl { Content = viewModel };
			var window = new Window { Width = 1280, Height = 600, Content = host, Background = (Avalonia.Media.IBrush) App.Current.Resources["ApplicationBackgroundColor"]! };
			window.Show();
			Dispatcher.UIThread.RunJobs();

			var frame = window.CaptureRenderedFrame();

			Assert.NotNull(frame);
			Assert.IsType(viewType, host.Presenter?.Child);
			var folder = Environment.GetEnvironmentVariable("TOPPOS_UI_SNAPSHOTS");
			if (!string.IsNullOrEmpty(folder)) {
				Directory.CreateDirectory(folder);
				frame!.Save(Path.Combine(folder, $"{page}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
			}
		}

		/// <summary>Veličina prozora po layoutu: rezolucije iz v2 konfiguracije, ostale po pravilima <see cref="ScreenLayout.FromSize"/>.</summary>
		private static readonly (ScreenLayout Layout, int Width, int Height)[] __screens = [
			(new(false, ScreenSize.Large), 1280, 800), (new(false, ScreenSize.Medium), 1024, 768), (new(false, ScreenSize.Small), 960, 600),
			(new(true, ScreenSize.Large), 800, 1280), (new(true, ScreenSize.Medium), 768, 1024), (new(true, ScreenSize.Small), 600, 1024)
		];

		public static TheoryData<string, int> PagesInLayouts {
			get {
				var data = new TheoryData<string, int>();
				foreach (var page in Pages) {
					for (var i = 0; i < __screens.Length; i++) {
						data.Add(page.Data, i);
					}
				}
				return data;
			}
		}

		/// <summary>Ime v2 layout fajla bez prefiksa: Landscape Large je „Landscape“, Portrait Large „Portrait“.</summary>
		private static string V2Suffix(ScreenLayout layout)
			=> layout.Size == ScreenSize.Large ? (layout.IsPortrait ? "Portrait" : "Landscape") : layout.ToString();

		/// <summary>
		/// Layout koji stranica mora da prikaže (ime tipa), nezavisno od toga koji tipovi postoje: v2 layout tog imena; stranice
		/// pošiljaoca imaju u v2 samo Landscape, LandscapeMedium i PortraitSmall, pa ostale veličine dobijaju najbliži
		/// (<see cref="LayoutContent.Select{T}"/>). <c>null</c>: stranica ima jedan prikaz za sve ekrane (v2 jedan šablon).
		/// </summary>
		private static string? ExpectedLayout(Type viewType, ScreenLayout layout)
		{
			var page = viewType.Name[..^"View".Length];
			if (viewType == typeof(RemoteServiceView) || viewType == typeof(RecipientRemoteConfirmationView) || viewType == typeof(DeviceStatusView)) {
				return null;
			}
			if (viewType == typeof(SenderBarcodeScanView) || viewType == typeof(SenderPackageDepositView)) {
				var suffix = (layout.IsPortrait, layout.Size) switch {
					(false, ScreenSize.Large) => "Landscape",
					(false, _) => "LandscapeMedium",
					(true, _) => "PortraitSmall"
				};
				return $"{page}{suffix}Layout";
			}
			return $"{page}{V2Suffix(layout)}Layout";
		}

		/// <summary>Layouti rezolucija iz v2 konfiguracije (flota): 1280×800, 1024×768, 600×1024; svaki jezik i brend (tekstovi brenda).</summary>
		public static TheoryData<string, int, string, string> PagesInFleetLayouts {
			get {
				var data = new TheoryData<string, int, string, string>();
				foreach (var page in Pages) {
					foreach (var screen in new[] { 0, 1, 5 }) {
						foreach (var language in new[] { "sr", "en" }) {
							foreach (var brand in Brands) {
								data.Add(page.Data, screen, language, brand.Data);
							}
						}
					}
				}
				return data;
			}
		}

		// Na ekranima iz flote nijedan vidljiv tekst (natpis, dugme, poruka) ne izlazi iz svoje kontrole ni iz prozora
		// (Inter je širi od Segoe UI iz v2, pa su neki v2 layouti na 600 px sekli tekst).
		[AvaloniaTheory]
		[MemberData(nameof(PagesInFleetLayouts))]
		public void Text_fits_on_fleet_screens(string page, int screen, string language, string brand)
		{
			using var errors = new BindingErrors();
			BrandTheme.Apply(BrandDirectory(brand), App.Current!.Resources);
			var (layout, width, height) = __screens[screen];
			var (viewModel, _) = Create(page, Languages(language, brand));
			var window = new Window { Width = width, Height = height, Content = new ContentControl { Content = viewModel } };
			LayoutContent.SetScreenLayout(window, layout);
			window.Show();
			Dispatcher.UIThread.RunJobs();

			var problems = new List<string>();
			foreach (var text in window.GetVisualDescendants().OfType<TextBlock>()) {
				if (!text.IsEffectivelyVisible || string.IsNullOrEmpty(text.Text) || text.Bounds.Width <= 0) {
					continue;
				}
				var room = text.Bounds.Width - text.Padding.Left - text.Padding.Right;
				// U prozoru, sa svim transformacijama (Viewbox smanjuje tastaturu)
				var toWindow = text.TransformToVisual(window) ?? Matrix.Identity;
				var left = new Point(0, 0).Transform(toWindow).X;
				var right = new Point(text.Bounds.Width, 0).Transform(toWindow).X;
				if (text.TextLayout.Width > room + 1) {
					problems.Add($"„{text.Text}“ ne staje: {text.TextLayout.Width:0} > {room:0}");
				}
				else if (left < -1 || right > width + 1) {
					problems.Add($"„{text.Text}“ izlazi iz prozora: x {left:0}..{right:0} od {width}");
				}
			}

			Assert.True(problems.Count == 0, $"{page} {layout} {language} {brand}: " + string.Join("; ", problems));
			Assert.Empty(errors.Messages);
		}

		// Svaka stranica u svakom layoutu, u prozoru te veličine (bez okvira glavnog prozora). Snimci: {page}.{layout}.png.
		[AvaloniaTheory]
		[MemberData(nameof(PagesInLayouts))]
		public void Page_renders_in_layout(string page, int screen)
		{
			using var errors = new BindingErrors();
			BrandTheme.Apply(Path.Combine(AppContext.BaseDirectory, "Brands", "DailyExpress"), App.Current!.Resources);
			var (layout, width, height) = __screens[screen];
			var (viewModel, viewType) = Create(page, Languages());
			var host = new ContentControl { Content = viewModel };
			var window = new Window { Width = width, Height = height, Content = host, Background = (Avalonia.Media.IBrush) App.Current.Resources["ApplicationBackgroundColor"]! };
			LayoutContent.SetScreenLayout(window, layout);
			window.Show();
			Dispatcher.UIThread.RunJobs();

			var frame = window.CaptureRenderedFrame();

			Assert.NotNull(frame);
			Assert.IsType(viewType, host.Presenter?.Child);
			var pageView = (Control) host.Presenter!.Child!;
			// Layout nasleđen od prozora (kao od glavnog prozora u radu), sa ViewModel-om stranice.
			var content = pageView.GetLogicalDescendants().OfType<LayoutContent>().SingleOrDefault();
			var expected = ExpectedLayout(viewType, layout);
			if (expected == null) {
				Assert.Null(content);
			}
			else {
				var shown = content?.Presenter?.Child;
				Assert.Equal(expected, shown?.GetType().Name);
				Assert.Same(viewModel, shown!.DataContext);
			}
			Assert.Empty(errors.Messages);
			var folder = Environment.GetEnvironmentVariable("TOPPOS_UI_SNAPSHOTS");
			if (!string.IsNullOrEmpty(folder)) {
				Directory.CreateDirectory(folder);
				frame!.Save(Path.Combine(folder, $"{page}.{layout}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
			}
		}
	}
}