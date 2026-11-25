using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Flatsoft.Common;
using TopPos.Kiosk.UI.Views;
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
		private static LanguageManager Languages()
		{
			var languages = new LanguageManager(null);
			languages.LoadLanguageResources(Path.Combine(AppContext.BaseDirectory, "Languages"), ".lang", "sr");
			languages.LoadBrandOverrides(Path.Combine(AppContext.BaseDirectory, "Brands", "DailyExpress"), ".lang");
			return languages;
		}

		private static BarcodeScannerClient Scanner()
			=> new(new BarcodeScannerConfiguration { ComPort = new ComPortConfiguration { PortName = "COM-NONE" } }, logger: null, CancellationToken.None);

		public static TheoryData<string> Pages => new() { "Start", "CodeInput", "PhoneInput", "Payment", "FiscalReceipt", "PackagePickup", "DeviceStatus",
													"CourierManualLogin", "CourierBoxAvailabilityLoading", "CourierBoxAvailability", "CourierPickupConfirmation",
													"CourierPickupPrompt", "CourierPackagePickup", "CourierPackagePickupCloseDoor", "CourierBarcodeScanPickup", "CourierBarcodeScanDeposit" };

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
			_ => throw new ArgumentOutOfRangeException(nameof(page))
		};

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

			Assert.IsType<Avalonia.Media.SolidColorBrush>(resources["ApplicationBackgroundColor"]);
			Assert.IsType<Avalonia.Media.Imaging.Bitmap>(resources["ApplicationLogo"]);
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
	}
}