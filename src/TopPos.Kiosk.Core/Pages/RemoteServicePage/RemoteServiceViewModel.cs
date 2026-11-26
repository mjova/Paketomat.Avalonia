using Microsoft.Extensions.Logging;
using Flatsoft.Common;
using Flatsoft.Common.UI;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.AgentCommunication;

namespace TopPos.Kiosk.ViewModels
{
	/// <summary>
	/// Stranica servisne sesije Agenta (<see cref="RemoteServiceSession"/>): prijava servisera kodom (tastatura ili QR sa
	/// skenera), zatim crtež rasporeda kao u <c>TopPos.Technician</c> (vertikale, boksovi po redu, visina po veličini) sa
	/// otvaranjem pojedinačnih vrata, vertikale ili svih vrata, i kraj u dva dodira.
	///
	/// Stanje se čita iz sesije pri svakoj njenoj promeni i jednom u sekundi (odbrojavanje). Promene sesije stižu sa
	/// pool niti, a svojstva se menjaju i odatle (v4: PropertyChanged ide na UI nit kroz UiThread); kolekcije se prave jednom, pri
	/// vezivanju sesije, pre prikaza stranice.
	/// </summary>
	public sealed class RemoteServiceViewModel : ViewModelBase, IWorkflowViewModel
	{
		private static readonly TimeSpan __refreshInterval = TimeSpan.FromSeconds(1);

		private readonly BarcodeScannerClient _barcodeScannerClient;
		private readonly string _lockerId;
		private readonly TimeProvider _timeProvider;
		private readonly Lock _refreshLock = new();
		private RemoteServiceSession? _session;
		private Dictionary<int, RemoteServiceBoxViewModel> _boxesByDoor = [];
		private ITimer? _refreshTimer;
		private bool _endConfirmPending;
		private int _endConfirmVersion;     // verzija otvaranja uz prikazani spisak vrata (RemoteServiceSession.DoorsPossiblyOpen)

		public RemoteServiceViewModel(BarcodeScannerClient barcodeScannerClient, LanguageManager langManager, string lockerId, ILogger? logger,
									  TimeProvider? timeProvider = null)
			: base(langManager, logger)
		{
			_barcodeScannerClient = barcodeScannerClient;
			_lockerId = lockerId;
			_timeProvider = timeProvider ?? TimeProvider.System;
			this.Keypad = new NumericKeypadViewModel(langManager, "RemoteServicePage.CodePlaceholder") {
								MinimumCodeLength = RemoteServiceLimits.MinPresenceCodeLength,
								MaximumCodeLength = RemoteServiceLimits.MaxPresenceCodeLength,
							};
			this.OpenAllCommand = AsyncCommand.Create(() => OpenAsync(null, all: true));
			this.EndCommand = AsyncCommand.Create(EndAsync);
		}

		public NumericKeypadViewModel Keypad { get; }

		/// <summary>Naslov posle prijave servisera, sa brojem paketomata.</summary>
		public string ActiveTitle => this["RemoteServicePage.ActiveTitle"].Replace("%s", _lockerId);

		protected override void OnLanguageChanged()
		{
			base.OnLanguageChanged();
			OnPropertyChanged(nameof(ActiveTitle));
		}

		public IAsyncCommand OpenAllCommand { get; }

		public IAsyncCommand EndCommand { get; }

		private IReadOnlyList<RemoteServiceColumnViewModel> _columns = [];
		public IReadOnlyList<RemoteServiceColumnViewModel> Columns {
			get => _columns;
			private set => SetProperty(ref _columns, value);
		}

		private IReadOnlyList<RemoteServiceBoxViewModel> _boxes = [];
		public IReadOnlyList<RemoteServiceBoxViewModel> Boxes {
			get => _boxes;
			private set => SetProperty(ref _boxes, value);
		}

		private double _canvasWidth;
		public double CanvasWidth {
			get => _canvasWidth;
			private set => SetProperty(ref _canvasWidth, value);
		}

		private double _canvasHeight;
		public double CanvasHeight {
			get => _canvasHeight;
			private set => SetProperty(ref _canvasHeight, value);
		}

		private bool _isAwaitingTechnician;
		/// <summary>Faza prijave: tastatura i skener.</summary>
		public bool IsAwaitingTechnician {
			get => _isAwaitingTechnician;
			private set => SetProperty(ref _isAwaitingTechnician, value);
		}

		private bool _isActive;
		/// <summary>Serviser je prijavljen: crtež rasporeda i dugmad.</summary>
		public bool IsActive {
			get => _isActive;
			private set => SetProperty(ref _isActive, value);
		}

		private bool _canOpen;
		/// <summary>Dugmad za otvaranje i kraj rade samo dok nijedno otvaranje ne teče i sesija se ne završava.</summary>
		public bool CanOpen {
			get => _canOpen;
			private set => SetProperty(ref _canOpen, value);
		}

		private string? _countdownText;
		public string? CountdownText {
			get => _countdownText;
			private set => SetProperty(ref _countdownText, value);
		}

		private string? _attemptsText;
		public string? AttemptsText {
			get => _attemptsText;
			private set => SetProperty(ref _attemptsText, value);
		}

		private string? _infoText;
		/// <summary>Šta kiosk upravo radi (otvara vrata, proverava stanje na kraju).</summary>
		public string? InfoText {
			get => _infoText;
			private set => SetProperty(ref _infoText, value);
		}

		private string? _moduleErrorText;
		/// <summary>Moduli koji ne odgovaraju (i pri početku sesije); stoji na ekranu dok sesija traje.</summary>
		public string? ModuleErrorText {
			get => _moduleErrorText;
			private set => SetProperty(ref _moduleErrorText, value);
		}

		private string? _noticeText;
		/// <summary>Ishod poslednje radnje servisera (pogrešan kod, otvaranje u toku, potvrda kraja).</summary>
		public string? NoticeText {
			get => _noticeText;
			private set => SetProperty(ref _noticeText, value);
		}

		private string? _endButtonText;
		public string? EndButtonText {
			get => _endButtonText;
			private set => SetProperty(ref _endButtonText, value);
		}

		/// <summary>Sesija koju stranica prikazuje; poziva se pre <see cref="Activate"/>.</summary>
		public void Bind(RemoteServiceSession session)
		{
			Unbind();
			_session = session;
			_endConfirmPending = false;
			BuildLayout(session);
			this.NoticeText = null;
			session.Changed += OnSessionChanged;
			Refresh();
		}

		public void Activate()
		{
			_logger?.LogInformation($"{GetType().Name}: aktivacija (sesija {_session?.SessionId}).");
			_barcodeScannerClient.ProcessBarcode = this.HandleScannedBarcode;
			this.Keypad.HandleCodeConfirmed = this.HandleCodeConfirmed;
			this.Keypad.Clear();
			_refreshTimer = _timeProvider.CreateTimer(_ => Refresh(), null, __refreshInterval, __refreshInterval);
		}

		public void Deactivate()
		{
			_logger?.LogInformation($"{GetType().Name}: deaktivacija (sesija {_session?.SessionId}).");
			_barcodeScannerClient.ProcessBarcode = null;
			this.Keypad.HandleCodeConfirmed = null;
			this.Keypad.Clear();
			_refreshTimer?.Dispose();
			_refreshTimer = null;
			Unbind();
		}

		/// <summary>Dodir bilo gde na stranici produžava rok neaktivnosti.</summary>
		public void Touch() => _session?.Touch();

		private void Unbind()
		{
			if (_session != null) {
				_session.Changed -= OnSessionChanged;
				_session = null;
			}
		}

		private void OnSessionChanged() => Refresh();

		private void BuildLayout(RemoteServiceSession session)
		{
			var columns = new List<RemoteServiceColumnViewModel>();
			var boxes = new List<RemoteServiceBoxViewModel>();
			double height = 0;
			int index = 0;
			// Vertikala bez ijednih vrata ovog kiosk-a se ne crta; vertikale se ređaju bez rupa.
			foreach (var column in session.Layout.GroupBy(b => b.Column).OrderBy(g => g.Key)) {
				double x = index++ * RemoteServiceBoxViewModel.ColumnWidth;
				double y = RemoteServiceColumnViewModel.HeaderHeight;
				var columnBoxes = new List<RemoteServiceBoxViewModel>();
				foreach (var box in column.OrderBy(b => b.Row)) {
					var boxViewModel = new RemoteServiceBoxViewModel(box, x, y, door => OpenAsync([door], all: false));
					columnBoxes.Add(boxViewModel);
					y += boxViewModel.Height;
				}
				height = Math.Max(height, y);
				boxes.AddRange(columnBoxes);
				columns.Add(new RemoteServiceColumnViewModel(column.Key, x, columnBoxes.Select(b => b.DoorNumber).ToList(),
															 this[$"RemoteServicePage.Column"].Replace("%s", $"{column.Key}"),
															 this["RemoteServicePage.ColumnOpenAll"].Replace("%s", $"{columnBoxes.Count}"),
															 doors => OpenAsync(doors, all: false)));
			}
			_boxesByDoor = boxes.ToDictionary(b => b.DoorNumber);
			this.CanvasWidth = Math.Max(1, index) * RemoteServiceBoxViewModel.ColumnWidth;
			this.CanvasHeight = Math.Max(height, RemoteServiceColumnViewModel.HeaderHeight);
			this.Columns = columns;
			this.Boxes = boxes;
		}

		private void Refresh()
		{
			lock (_refreshLock) {
				var session = _session;
				if (session == null) {
					return;
				}
				var phase = session.Phase;
				bool ending = session.IsEnding || phase == RemoteServicePhases.Ended;
				bool opening = session.IsOpening;
				var now = _timeProvider.GetUtcNow();
				// Kraj iz portala ili zbog isteka dok stoji poziv na potvrdu: dugme „Potvrdi kraj" više ne postoji. Otvaranje (i iz
				// portala) menja vrata koja su možda otvorena, pa prikazani spisak više ne važi.
				if ((ending || opening) && _endConfirmPending) {
					_endConfirmPending = false;
					this.NoticeText = null;
				}

				this.IsAwaitingTechnician = phase == RemoteServicePhases.AwaitingTechnician && !ending;
				this.IsActive = phase == RemoteServicePhases.Active || (ending && session.LastOutcomeByDoor.Count > 0);
				this.CanOpen = phase == RemoteServicePhases.Active && !ending && !opening;
				this.AttemptsText = this["RemoteServicePage.AttemptsLeft"].Replace("%s", $"{session.RemainingAuthAttempts}");
				this.CountdownText = ending ? null
					: phase == RemoteServicePhases.AwaitingTechnician
						? this["RemoteServicePage.AuthCountdown"].Replace("%s", Format(session.AuthDeadline - now))
						: opening ? null : this["RemoteServicePage.IdleCountdown"].Replace("%s", Format(session.IdleDeadline - now));
				var doorInProgress = session.DoorInProgress;
				this.InfoText = ending ? this["RemoteServicePage.Ending"]
							  : doorInProgress is { } door ? this["RemoteServicePage.Opening"].Replace("%s", $"{door}")
							  : null;
				var failedModules = session.FailedModules;
				this.ModuleErrorText = failedModules.Count == 0 ? null
					: this["RemoteServicePage.ModuleError"].Replace("%s", string.Join(", ", failedModules));
				this.EndButtonText = _endConfirmPending ? this["RemoteServicePage.EndConfirm"] : this["RemoteServicePage.End"];

				var outcomes = session.LastOutcomeByDoor;
				foreach (var box in _boxesByDoor.Values) {
					var state = box.DoorNumber == doorInProgress ? RemoteServiceBoxState.Opening
							  : outcomes.TryGetValue(box.DoorNumber, out var outcome) ? StateOf(outcome)
							  : RemoteServiceBoxState.NotOpened;
					box.SetState(state, StatusTextOf(state));
				}
			}
		}

		private async ValueTask<bool> HandleCodeConfirmed(string code)
		{
			var session = _session;
			if (session == null) {
				return false;
			}
			PresenceCodeResult result;
			try {
				result = await session.SubmitPresenceCodeAsync(code).ConfigureAwait(false);
			}
			catch (Exception ex) {
				// Pozivalac je async void handler tastature: neuhvaćen izuzetak bi oborio proces.
				_logger?.LogError(ex, $"{GetType().Name}: provera koda nije uspela");
				return false;
			}
			this.Keypad.Clear();
			this.NoticeText = result switch {
				PresenceCodeResult.Rejected => this["RemoteServicePage.WrongCode"].Replace("%s", $"{session.RemainingAuthAttempts}"),
				PresenceCodeResult.Failed => this["RemoteServicePage.AuthFailed"],
				_ => null,
			};
			Refresh();
			return result == PresenceCodeResult.Accepted;
		}

		// QR sa telefona servisera nosi isti broj kao kod na portalu. Posle prijave skener se ne koristi.
		private async ValueTask<bool> HandleScannedBarcode(string barcode)
		{
			if (_session?.Phase != RemoteServicePhases.AwaitingTechnician) {
				_logger?.LogInformation($"{GetType().Name}: barkod {LogMask.Credential(barcode)} zanemaren — serviser je već prijavljen.");	// može biti kartica kurira — maskiran
				return false;
			}
			return await HandleCodeConfirmed(barcode).ConfigureAwait(false);
		}

		private async Task OpenAsync(IReadOnlyList<int>? doors, bool all)
		{
			var session = _session;
			if (session == null) {
				return;
			}
			session.Touch();
			_endConfirmPending = false;
			var start = await session.StartOpenDoorsAsync(doors, all, DoorOpenSources.Screen).ConfigureAwait(false);
			this.NoticeText = start.ResultCode switch {
				AgentResultCodes.InProgress => null,
				AgentResultCodes.Busy => this["RemoteServicePage.Busy"],
				_ => start.Message,
			};
			Refresh();
		}

		// Prvi dodir pokaže vrata koja po poslednjem poznatom stanju nisu zatvorena, drugi potvrđuje kraj.
		private async Task EndAsync()
		{
			var session = _session;
			if (session == null) {
				return;
			}
			session.Touch();
			if (!_endConfirmPending) {
				ShowEndPrompt(session);
				return;
			}
			// Dok se čita stanje vrata (na Serial-u sekundama) ostaje samo „Proveravam stanje vrata…", bez poziva na potvrdu.
			// Kraj počinje samo za prikazani spisak: sesija proverava verziju otvaranja pod svojim lock-om (portal je mogao da
			// otvori i završi otvaranje posle ovog dodira).
			_endConfirmPending = false;
			this.NoticeText = null;
			var (code, _) = await session.EndFromScreenAsync(_endConfirmVersion).ConfigureAwait(false);
			if (code == RemoteServiceSession.EndPromptOutdated) {
				ShowEndPrompt(session);
			}
			else if (code == AgentResultCodes.Busy) {
				this.NoticeText = this["RemoteServicePage.Busy"];
				Refresh();
			}
		}

		// Prvi dodir: vrata koja su možda otvorena i verzija otvaranja za koju potvrda važi.
		private void ShowEndPrompt(RemoteServiceSession session)
		{
			var possiblyOpen = session.DoorsPossiblyOpen(out _endConfirmVersion);
			_endConfirmPending = true;
			this.NoticeText = possiblyOpen.Count == 0
				? this["RemoteServicePage.EndPrompt"]
				: this["RemoteServicePage.EndPromptDoorsOpen"].Replace("%s", string.Join(", ", possiblyOpen));
			Refresh();
		}

		private static RemoteServiceBoxState StateOf(string outcome) => outcome switch {
			DoorOpenOutcomes.OpenConfirmed => RemoteServiceBoxState.Open,
			DoorOpenOutcomes.OpenNotConfirmed => RemoteServiceBoxState.DidNotOpen,
			DoorOpenOutcomes.ModuleUnavailable => RemoteServiceBoxState.ModuleUnavailable,
			DoorOpenOutcomes.NotStarted => RemoteServiceBoxState.NotStarted,
			_ => RemoteServiceBoxState.Unknown,
		};

		private string StatusTextOf(RemoteServiceBoxState state) => state switch {
			RemoteServiceBoxState.NotOpened => "",
			RemoteServiceBoxState.Opening => this["RemoteServicePage.Box.Opening"],
			RemoteServiceBoxState.Open => this["RemoteServicePage.Box.Open"],
			RemoteServiceBoxState.DidNotOpen => this["RemoteServicePage.Box.DidNotOpen"],
			RemoteServiceBoxState.ModuleUnavailable => this["RemoteServicePage.Box.ModuleUnavailable"],
			RemoteServiceBoxState.NotStarted => this["RemoteServicePage.Box.NotStarted"],
			_ => this["RemoteServicePage.Box.Unknown"],
		};

		private static string Format(TimeSpan remaining)
		{
			var seconds = Math.Max(0, (int) Math.Ceiling(remaining.TotalSeconds));
			return $"{seconds / 60}:{seconds % 60:00}";
		}
	}

	/// <summary>Stanje boksa na crtežu: ishod poslednjeg otvaranja u ovoj sesiji.</summary>
	public enum RemoteServiceBoxState
	{
		/// <summary>U ovoj sesiji nije otvarano.</summary>
		NotOpened,
		Opening,
		Open,
		/// <summary>Kontroler je javio zatvorena vrata posle komande.</summary>
		DidNotOpen,
		/// <summary>Komanda je poslata, odgovor nije stigao: vrata su možda otvorena.</summary>
		Unknown,
		ModuleUnavailable,
		NotStarted,
	}

	/// <summary>
	/// Jedna pozicija na crtežu. Dimenzije su u jedinicama crteža (kao u <c>TopPos.Technician</c>); stranica ceo crtež
	/// skalira preko Viewbox-a, pa su odnosi visina S/M/L očuvani na svakom ekranu.
	/// </summary>
	public sealed class RemoteServiceBoxViewModel : Flatsoft.Common.UI.ViewModelBase
	{
		public const double ColumnWidth = 260;

		// v4: boje kao tekst (#RRGGBB); prikaz ih pretvara u četku (v2: zamrznut WPF SolidColorBrush).
		private const string __notOpened = "#EEF1F5";
		private const string __opening = "#F0D050";
		private const string __open = "#E05A5A";
		private const string __didNotOpen = "#F0A85C";
		private const string __unknown = "#C4CBD6";

		public RemoteServiceBoxViewModel(LayoutBox box, double x, double y, Func<int, Task> open)
		{
			this.DoorNumber = box.DoorNumber;
			this.SizeText = box.Size;
			this.X = x;
			this.Y = y;
			this.Height = box.Size switch {
				BoxSizes.S => 100,
				BoxSizes.L => 240,
				_ => 160,
			};
			this.TapCommand = AsyncCommand.Create(() => open(this.DoorNumber));
		}

		public int DoorNumber { get; }

		public string SizeText { get; }

		public string AutomationId => $"RemoteServicePage.Door.{this.DoorNumber}";

		public double X { get; }

		public double Y { get; }

		public double Width => ColumnWidth;

		public double Height { get; }

		public IAsyncCommand TapCommand { get; }

		private RemoteServiceBoxState _state = RemoteServiceBoxState.NotOpened;
		public RemoteServiceBoxState State {
			get => _state;
			private set {
				if (SetProperty(ref _state, value)) {
					OnPropertyChanged(nameof(Background));
				}
			}
		}

		private string _statusText = "";
		public string StatusText {
			get => _statusText;
			private set => SetProperty(ref _statusText, value);
		}

		public string Background => this.State switch {
			RemoteServiceBoxState.Opening => __opening,
			RemoteServiceBoxState.Open => __open,
			RemoteServiceBoxState.DidNotOpen => __didNotOpen,
			RemoteServiceBoxState.NotOpened => __notOpened,
			_ => __unknown,
		};

		public void SetState(RemoteServiceBoxState state, string statusText)
		{
			this.State = state;
			this.StatusText = statusText;
		}
	}

	/// <summary>Vertikala sa dugmetom iznad nje: otvara sva njena vrata, redom odozgo.</summary>
	public sealed class RemoteServiceColumnViewModel : Flatsoft.Common.UI.ViewModelBase
	{
		/// <summary>Visina trake sa dugmadima iznad vertikala, u jedinicama crteža.</summary>
		public const double HeaderHeight = 110;

		public RemoteServiceColumnViewModel(int column, double x, IReadOnlyList<int> doors, string label, string doorCountText,
											Func<IReadOnlyList<int>, Task> open)
		{
			this.Column = column;
			this.X = x;
			this.Doors = doors;
			this.Label = label;
			this.DoorCountText = doorCountText;
			this.OpenCommand = AsyncCommand.Create(() => open(this.Doors));
		}

		public int Column { get; }

		public IReadOnlyList<int> Doors { get; }

		public string AutomationId => $"RemoteServicePage.Column.{this.Column}";

		public double X { get; }

		public double Y => 0;

		public double Width => RemoteServiceBoxViewModel.ColumnWidth;

		public double Height => HeaderHeight;

		public string Label { get; }

		public string DoorCountText { get; }

		public IAsyncCommand OpenCommand { get; }
	}
}
