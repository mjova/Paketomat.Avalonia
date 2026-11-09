using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Flatsoft.Common;
using Flatsoft.Common.UI;
using Microsoft.Extensions.Logging;

namespace TopPos.Client.WPF.ViewModels
{
	public class RecipientFiscalReceiptViewModel : ViewModelBase, IWorkflowViewModel
	{
        private readonly TimeSpan _fetchTimeout;
        private readonly TimeSpan _scanTimeout;
        private DispatcherTimer? _scanTimer;
        private DispatcherTimer? _countdownTimer;
        private int? _countdownSeconds;
        private CancellationTokenSource? _ctsFetch;
        private string? _currentStatusMessageLanguageKey;
        private string? _currentHintMessageLanguageKey;

        /// <summary>
        /// Jedna aktivacija ekrana i handler kraja te aktivacije (sesije). Fetch, rokovi i klik stare aktivacije (posle
        /// Deactivate, ili kad je ekran već aktiviran za sledeću sesiju) ne diraju ekran i ne zovu handler sledeće sesije.
        /// </summary>
        private sealed class Activation(Func<ValueTask>? complete)
        {
            public Func<ValueTask>? Complete { get; } = complete;
        }
        private volatile Activation? _activation;

        public RecipientFiscalReceiptViewModel(TimeSpan fetchTimeout, TimeSpan scanTimeout, LanguageManager langManager, ILogger? logger)
			: base(langManager, logger)
		{
            _fetchTimeout = fetchTimeout;
            _scanTimeout = scanTimeout;
            this.CompleteCommand = new AsyncCommand<object?>(async (o, ct) =>
                                    {
                                        _logger?.LogInformation($"{GetType().Name}: {nameof(CompleteCommand)}: button clicked.");
                                        var activation = _activation;   // pre odziva: aktivacija na kojoj je kliknuto
                                        await DelayForClickFeedback(ct);
                                        await Complete(activation).ConfigureAwait(false);
                                        return null;
                                    });
        }

        private string? _infoMessage;
        public string? StatusMessage 
		{ 
			get => _infoMessage;
			set {
				SetProperty(ref _infoMessage, value);
			}
		}

        private string? _hintMessage;
        public string? HintMessage
        {
            get => _hintMessage;
            set {
                SetProperty(ref _hintMessage, value);
            }
        }

        private BitmapImage? _fiscalReceiptImage;
        public BitmapImage? FiscalReceiptImage
        {
            get => _fiscalReceiptImage;
            set {
                SetProperty(ref _fiscalReceiptImage, value);
            }
        }

        private string? _nextCountdown;
        public string? NextCountdown
        {
            get => _nextCountdown;
            set {
                SetProperty(ref _nextCountdown, value);
            }
        }

        protected override void OnLanguageChanged()
        {
            base.OnLanguageChanged();   // updates all indexer properties

            UpdateStatusMessage();
            UpdateHintMessage();
        }

        private void UpdateStatusMessage(string? languageKey = null)
        {
            _currentStatusMessageLanguageKey = languageKey ?? _currentStatusMessageLanguageKey;
            StatusMessage = _currentStatusMessageLanguageKey != null? this[_currentStatusMessageLanguageKey] : null;
        }

        private void UpdateHintMessage(string? languageKey = null)
        {
            _currentHintMessageLanguageKey = languageKey ?? _currentHintMessageLanguageKey;
            HintMessage = _currentHintMessageLanguageKey != null? this[_currentHintMessageLanguageKey] : null;
        }

        public void Activate()
        {
            _logger?.LogInformation($"{GetType().Name}: aktivacija.");

            UpdateStatusMessage("RecipientFiscalReceiptPage.ProcessingFiscalReceipt");
            UpdateHintMessage("RecipientFiscalReceiptPage.HintSkipIfNotNeeded");

            var activation = new Activation(this.HandleCompleteClicked);
            _activation = activation;
            _ = FetchFiscalReceiptImage(activation).ConfigureAwait(false);
            StartCountdownTimer((int)_fetchTimeout.TotalSeconds);
        }

        private async Task FetchFiscalReceiptImage(Activation activation)
        {
            try
            {
                var funcFetch = this.FuncFetchFiscalReceiptImage;
                if (funcFetch == null) {
                    _logger?.LogWarning($"{GetType().Name}: {nameof(FuncFetchFiscalReceiptImage)} is not set, cannot fetch image.");
                    return;
                }   
                FiscalReceiptImage = null;
                _ctsFetch?.Cancel();
                _ctsFetch?.Dispose();
                _ctsFetch = new CancellationTokenSource(_fetchTimeout);
                var image = await funcFetch(_ctsFetch.Token); // No ConfigureAwait(false) here, StartCountdownTimer and StartScanTimer must run on the UI thread
                if (activation != _activation) {
                    _logger?.LogInformation($"{GetType().Name}: fiskalni račun stare aktivacije ekrana se ne prikazuje.");
                    return;
                }
                FiscalReceiptImage = image;
                if (FiscalReceiptImage == null) {
                    UpdateStatusMessage("RecipientFiscalReceiptPage.ReceiptProcessingFailed");
                    UpdateHintMessage("RecipientFiscalReceiptPage.HintSkipToPickup");
                    var wait = TimeSpan.FromSeconds(10);// TODO: make this configurable
                    StartCountdownTimer((int) wait.TotalSeconds);   
                    await Task.Delay(wait).ConfigureAwait(false);
                    await Complete(activation).ConfigureAwait(false);
                }
                else {
                    
                    UpdateStatusMessage("RecipientFiscalReceiptPage.ScanQRCode");
                    UpdateHintMessage();
                    StartCountdownTimer((int) _scanTimeout.TotalSeconds);
                    StartScanTimer(activation);
                }
            }
            catch (OperationCanceledException) {
                _logger?.LogWarning($"{GetType().Name}: {nameof(FuncFetchFiscalReceiptImage)} timed out waiting for fiscal receipt image.");
                await Complete(activation).ConfigureAwait(false);
            }
            catch (Exception ex) {
                _logger?.LogError($"{GetType().Name}: {nameof(FuncFetchFiscalReceiptImage)}: exception: {ex.Message}.");
                await Complete(activation).ConfigureAwait(false);
            }
        }

        private void StartCountdownTimer(int seconds)
        {
            UIThreadSwitcher.RunOnUiThread(() => {
                _countdownSeconds = Math.Abs(seconds);
                _countdownTimer?.Stop();
                _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _countdownTimer.Tick += (s, e) =>
                                    {
                                        _countdownSeconds--;
                                        if (_countdownSeconds <= 0) {
                                            _countdownTimer?.Stop();
                                        }
                                        else {
                                            NextCountdown = this["RecipientFiscalReceiptPage.NextCountdownTemplate"].Replace("%s", $"{_countdownSeconds}");
                                        }
                                    };
                _countdownTimer.Start();
            });
        }

        private void StartScanTimer(Activation activation)
        {
            UIThreadSwitcher.RunOnUiThread(() => {
                _scanTimer?.Stop();
                _scanTimer = new DispatcherTimer { Interval = _scanTimeout };
                _scanTimer.Tick += async (s, e) =>
                                    {
                                        _scanTimer?.Stop();
                                        _logger?.LogInformation($"{GetType().Name}: scan timeout reached.");
                                        await Complete(activation);
                                    };
                _scanTimer.Start();
            });
        }

        public void Deactivate()
        {
            _logger?.LogInformation($"{GetType().Name}: deaktivacija.");
            _activation = null;
            FiscalReceiptImage = null;
            _countdownTimer?.Stop();
            _scanTimer?.Stop();
            _ctsFetch?.Cancel();
            _ctsFetch?.Dispose();
            _ctsFetch = null;
        }

        private async ValueTask Complete(Activation? activation)
        {
            if (activation == null || activation != _activation) {
                _logger?.LogInformation($"{GetType().Name}: kraj stare aktivacije ekrana se ignoriše.");
                return;
            }
            try { 
                _countdownTimer?.Stop();
                _scanTimer?.Stop();
                if (activation.Complete != null) {
                    await activation.Complete();
                }
            }
            catch (Exception ex) {
                _logger?.LogError($"{GetType().Name}: {nameof(Complete)}: exception: {ex.Message}.");
            }
        }

        public IAsyncCommand CompleteCommand { get; }
        public Func<ValueTask>? HandleCompleteClicked { get; set; }
        public Func<CancellationToken, ValueTask<BitmapImage?>>? FuncFetchFiscalReceiptImage { get; set; }
	}
}
