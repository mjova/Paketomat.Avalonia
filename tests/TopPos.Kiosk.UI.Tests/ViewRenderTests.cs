using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Flatsoft.Common;
using TopPos.Kiosk.UI.Views;
using TopPos.Kiosk.ViewModels;
using Xunit;

namespace TopPos.Kiosk.UI.Tests
{
	/// <summary>
	/// Svaki ekran toka primaoca se prikazuje sa pravim ViewModel-om, jezicima i resursima aplikacije (stilovi, crteži, slike)
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

		public static TheoryData<string> Pages => new() { "Start", "CodeInput", "PhoneInput", "Payment", "FiscalReceipt", "PackagePickup" };

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
			_ => throw new ArgumentOutOfRangeException(nameof(page))
		};

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