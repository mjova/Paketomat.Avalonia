using Flatsoft.Common;
using Flatsoft.Common.UI;
using Microsoft.Extensions.Logging;
using TopPos.Service;

namespace TopPos.Kiosk.ViewModels
{
	public class CourierLockerBoxAvailabilityViewModel : ViewModelBase, IWorkflowViewModel
	{
        private readonly TimeSpan _fetchTimeout;
        private string? _currentStatusMessageLanguageKey;

        /// <summary>
        /// Jedna aktivacija ekrana i handler kraja te aktivacije (sesije). Fetch, rokovi i klik stare aktivacije (posle
        /// Deactivate, ili kad je ekran već aktiviran za sledeću sesiju) ne diraju ekran i ne zovu handler sledeće sesije.
        /// Fetch i njegov token pripadaju aktivaciji i prave se pri aktivaciji: fetch koji krene kasnije (Task.Run) ne otkazuje
        /// i ne zamenjuje fetch sledeće aktivacije.
        /// </summary>
        private sealed class Activation(Func<ValueTask>? complete,
                                        Func<CancellationToken, ValueTask<StatusPaketomata?>>? fetch, TimeSpan fetchTimeout)
        {
            public Func<ValueTask>? Complete { get; } = complete;
            public Func<CancellationToken, ValueTask<StatusPaketomata?>>? Fetch { get; } = fetch;
            public CancellationTokenSource CtsFetch { get; } = new(fetchTimeout);
        }
        private volatile Activation? _activation;
        // v4: promena aktivacije (Activate, Deactivate) i upis rezultata fetch-a su pod istim lock-om. Bez njega fetch stare
        // aktivacije koji je prošao proveru može upisati tabelu posle Deactivate, pa sledeći kurir vidi stare brojeve.
        private readonly Lock _activationLock = new();

        public CourierLockerBoxAvailabilityViewModel(TimeSpan fetchTimeout, LanguageManager langManager, ILogger? logger)
			: base(langManager, logger)
		{
            _fetchTimeout = fetchTimeout;
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

        private IEnumerable<StatusPoVeliciniPozicije>? _boxSizeAvailabilityStatuses;
        public IEnumerable<StatusPoVeliciniPozicije>? BoxSizeAvailabilityStatuses
        {
            get => _boxSizeAvailabilityStatuses;
            set {
                SetProperty(ref _boxSizeAvailabilityStatuses, value);
            }
        }

        protected override void OnLanguageChanged()
        {
            base.OnLanguageChanged();   // updates all indexer properties

            UpdateStatusMessage();
        }

        private void UpdateStatusMessage(string? languageKey = null)
        {
            _currentStatusMessageLanguageKey = languageKey ?? _currentStatusMessageLanguageKey;
            StatusMessage = _currentStatusMessageLanguageKey != null? this[_currentStatusMessageLanguageKey] : null;
        }


        public void Activate()
        {
            _logger?.LogInformation($"{GetType().Name}: aktivacija.");

            var activation = new Activation(this.HandleCompleteClicked, this.FuncFetchLockerBoxAvailabilityInfo, _fetchTimeout);
            lock (_activationLock) {
                BoxSizeAvailabilityStatuses = null;     // v4: nikad tabela prethodne aktivacije
                UpdateStatusMessage("CourierLockerBoxAvailabilityPage.FetchingLockerBoxAvailability");
                _activation = activation;
            }
            Task.Run(() => FetchLockerBoxAvailabilityInfo(activation)).ConfigureAwait(false);
        }

        private async Task FetchLockerBoxAvailabilityInfo(Activation activation)
        {
            try
            {
                var funcFetch = activation.Fetch;
                if (funcFetch == null) {
                    _logger?.LogWarning($"{GetType().Name}: {nameof(FuncFetchLockerBoxAvailabilityInfo)} is not set, cannot fetch locker box availability info.");
                    return;
                }   
                var lockerBoxAvailability = await funcFetch(activation.CtsFetch.Token); // No ConfigureAwait(false) here, StartCountdownTimer and StartScanTimer must run on the UI thread
                lock (_activationLock) {
                    if (activation != _activation) {
                        _logger?.LogInformation($"{GetType().Name}: dostupnost sanduka stare aktivacije ekrana se ne prikazuje.");
                        return;
                    }
                    if (lockerBoxAvailability == null) {
                        BoxSizeAvailabilityStatuses = null;
                        UpdateStatusMessage("CourierLockerBoxAvailabilityPage.AvailabilityFetchFailed");
                    }
                    else {
                        BoxSizeAvailabilityStatuses = [.. lockerBoxAvailability.StatusiPoVelicinamaPozicija];
                    }
                }
            }
            catch (OperationCanceledException) {
                _logger?.LogWarning($"{GetType().Name}: {nameof(FuncFetchLockerBoxAvailabilityInfo)} timed out waiting for locker box availability info.");
                await Complete(activation).ConfigureAwait(false);
            }
            catch (Exception ex) {
                _logger?.LogError($"{GetType().Name}: {nameof(FuncFetchLockerBoxAvailabilityInfo)}: exception: {ex.Message}.");
                await Complete(activation).ConfigureAwait(false);
            }
        }

        public void Deactivate()
        {
            _logger?.LogInformation($"{GetType().Name}: deaktivacija.");
            Activation? activation;
            lock (_activationLock) {
                activation = _activation;
                _activation = null;
                BoxSizeAvailabilityStatuses = null;
            }
            // Bez Dispose: Complete iste aktivacije može još da otkazuje (Cancel zatvara i tajmer roka).
            activation?.CtsFetch.Cancel();
        }

        private async ValueTask Complete(Activation? activation)
        {
            if (activation == null || activation != _activation) {
                _logger?.LogInformation($"{GetType().Name}: kraj stare aktivacije ekrana se ignoriše.");
                return;
            }
            try { 
                activation.CtsFetch.Cancel();
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
        public Func<CancellationToken, ValueTask<StatusPaketomata?>>? FuncFetchLockerBoxAvailabilityInfo { get; set; }
	}
}
