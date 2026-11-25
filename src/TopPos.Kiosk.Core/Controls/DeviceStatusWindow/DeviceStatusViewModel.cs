using System.Windows.Input;
using Flatsoft.Common;
using Flatsoft.Common.UI;
using Microsoft.Extensions.Logging;

namespace TopPos.Kiosk.ViewModels
{
	/// <summary>
	/// Status uređaja (dug pritisak na Home): da li su barkod skener i POS terminal povezani, uz verziju programa i git hash.
	/// v2 <c>Controls/DeviceStatusWindow</c>. v4: prikazuje se kao sloj preko glavnog prozora, ne kao zaseban prozor (pod
	/// cage-om drugi prozor nije pouzdan). Zato su zatvaranje i otkazivanje provera ovde (v2: u prozoru), a vlasnik čeka
	/// <see cref="Closed"/> umesto <c>ShowDialog</c>. Verzija i git hash dolaze iz <see cref="ApplicationContext"/> (v2: čitanje
	/// <c>paketomat.ver</c> i <c>App.ApplicationContext</c>).
	/// </summary>
	public class DeviceStatusViewModel : ViewModelBase
	{
		private readonly BarcodeScannerClient? _barcodeScannerClient;
		private readonly IPosClient? _posClient;
		private readonly string? _versionLabel;
		// Zatvaranje otkazuje provere. CTS se ne uklanja: provera može i dalje da čita token (v2 DeviceStatusWindow.OnClosed).
		private readonly CancellationTokenSource _ctsChecks = new();
		private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

		private string? _currentTime;
		private string? _appVersion;
		private bool _isBarcodeScannerChecking;
		private bool _isPosTerminalChecking;
		private string? _barcodeScannerStatus;
		private string? _posTerminalStatus;

		public DeviceStatusViewModel(BarcodeScannerClient? barcodeScannerClient,
									IPosClient? posClient,
									ApplicationContext? applicationContext,
									LanguageManager languageManager,
									ILogger? logger)
			: base(languageManager, logger)
		{
			_barcodeScannerClient = barcodeScannerClient;
			_posClient = posClient;
			_versionLabel = applicationContext?.VersionLabel;
			this.BuildId = applicationContext?.GitHash ?? string.Empty;
			this.CloseCommand = new RelayCommand(_ => Close());
		}

		/// <summary>Pauza pre provere (v2: 2 s), da se vidi da provera traje; testovi je skraćuju.</summary>
		public TimeSpan CheckDelay { get; init; } = TimeSpan.FromSeconds(2);

		/// <summary>Ukupan rok jedne provere (v4, kao startna provera u <c>KioskHost</c>); istek znači „Nije povezan“.</summary>
		public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(10);

		public string? CurrentTime
		{
			get => _currentTime;
			private set => SetProperty(ref _currentTime, value);
		}

		public string? AppVersion
		{
			get => _appVersion;
			private set => SetProperty(ref _appVersion, value);
		}

		public string BuildId { get; }

		public bool IsBarcodeScannerChecking
		{
			get => _isBarcodeScannerChecking;
			private set => SetProperty(ref _isBarcodeScannerChecking, value);
		}

		public bool IsPosTerminalChecking
		{
			get => _isPosTerminalChecking;
			private set => SetProperty(ref _isPosTerminalChecking, value);
		}

		public string? BarcodeScannerStatus
		{
			get => _barcodeScannerStatus;
			private set => SetProperty(ref _barcodeScannerStatus, value);
		}

		public string? PosTerminalStatus
		{
			get => _posTerminalStatus;
			private set => SetProperty(ref _posTerminalStatus, value);
		}

		/// <summary>Dugme „Zatvori“.</summary>
		public ICommand CloseCommand { get; }

		/// <summary>Završava se kad se sloj zatvori (v2: povratak iz <c>ShowDialog</c>).</summary>
		public Task Closed => _closed.Task;

		/// <summary>Zatvaranje (dugme ili gašenje programa): otkazuje provere koje još rade. Idempotentno.</summary>
		public void Close()
		{
			_ctsChecks.Cancel();
			StopTrackingLanguage();
			_closed.TrySetResult();
		}

		/// <summary>Pokreće provere skenera i POS-a; vraćeni Task se završava kad se obe završe ili otkažu.</summary>
		public Task Initialize()
		{
			CurrentTime = DateTime.Now.ToString("yy-MM-dd HH:mm:ss");
			AppVersion = FormatAppVersion(_versionLabel);

			return CheckDeviceStatusesAsync();
		}

		/// <summary>v2 ReadAppVersion: sadržaj <c>paketomat.ver</c> skraćen na 15 znakova, „N/A“ kad fajla nema.</summary>
		private static string FormatAppVersion(string? versionLabel)
		{
			if (versionLabel == null) {
				return "N/A";
			}
			return versionLabel.Length > 15 ? versionLabel[..15] : versionLabel;
		}

		private async Task CheckDeviceStatusesAsync()
		{
			var barcodeScannerTask = CheckPresenceAsync("Barcode scanner", _barcodeScannerClient == null ? null : _barcodeScannerClient.CheckPresence,
														checking => IsBarcodeScannerChecking = checking, status => BarcodeScannerStatus = status);
			var posTerminalTask = CheckPresenceAsync("POS terminal", _posClient == null ? null : _posClient.CheckPresence,
													checking => IsPosTerminalChecking = checking, status => PosTerminalStatus = status);

			await Task.WhenAll(barcodeScannerTask, posTerminalTask);
		}

		/// <summary>
		/// Provera jednog uređaja (v2: ista za skener i POS). v4: ukupan rok <see cref="CheckTimeout"/> kao startna provera
		/// (<c>KioskHost</c>); istek roka i greška znače „Nije povezan“. Posle zatvaranja se ishod ne prikazuje. Vraćeni Task se
		/// završava tek kad se pristup uređaju stvarno završi, i kad ishod više ne čeka (provera koja ne poštuje otkazivanje):
		/// koordinator do tada drži kiosk zauzetim.
		/// </summary>
		private async Task CheckPresenceAsync(string device, Func<CancellationToken, Task<bool>>? checkPresence,
											Action<bool> setChecking, Action<string?> setStatus)
		{
			var closeToken = _ctsChecks.Token;
			var present = false;
			Task<bool>? check = null;
			try {
				setChecking(true);
				setStatus(null);

				await Task.Delay(CheckDelay, closeToken);

				if (checkPresence != null) {
					// CTS se ne uklanja: provera može i dalje da čita token.
					var ctsCheck = CancellationTokenSource.CreateLinkedTokenSource(closeToken);
					ctsCheck.CancelAfter(CheckTimeout);
					check = Task.Run(() => checkPresence(ctsCheck.Token));
					present = await check.WaitAsync(CheckTimeout, closeToken);
				}
			}
			catch (TimeoutException) {
				_logger?.LogWarning($"{device} status check timed out after {CheckTimeout.TotalSeconds} s.");
			}
			catch (OperationCanceledException) when (!closeToken.IsCancellationRequested) {
				_logger?.LogWarning($"{device} status check timed out after {CheckTimeout.TotalSeconds} s.");
			}
			catch (OperationCanceledException) {
				_logger?.LogInformation($"{device} status check was cancelled.");
			}
			catch (Exception ex) {
				_logger?.LogWarning($"{device} status check failed: {ex.Message}");
			}
			finally {
				if (!closeToken.IsCancellationRequested) {
					setStatus(present ? this["DeviceStatus.Connected"] : this["DeviceStatus.Disconnected"]);
				}
				setChecking(false);
			}

			if (check is { IsCompleted: false }) {
				_logger?.LogWarning($"{device} status check still accessing the device; the kiosk stays busy until it returns.");
				try {
					await check;
				}
				catch (Exception ex) {
					_logger?.LogInformation($"{device} status check ended: {ex.Message}");
				}
				_logger?.LogInformation($"{device} status check returned.");
			}
		}
	}
}