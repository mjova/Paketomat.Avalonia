using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Flatsoft.Common;
using Flatsoft.Common.UI;
using TopPos.Agent.Contracts;

namespace TopPos.Kiosk.ViewModels
{
	/// <summary>
	/// Stranica potvrde preuzimanja na zahtev Agenta (<see cref="Workflow.WorkflowState.RecipientRemoteConfirmation"/>): korisnik
	/// kuca kod prisustva koji mu je operater podrške izdiktirao. Proveru koda i izlaske sa stranice vodi activator kroz handlere;
	/// stranica prikazuje preostalo vreme i pokušaje i javlja istek roka.
	///
	/// Rok teče od <see cref="Prepare"/> i dodir ga ne produžava (Agent vidi isti rok). Preostalo vreme se osvežava jednom u
	/// sekundi sa pool niti; svojstva se menjaju i odatle (WPF prenosi PropertyChanged na UI nit).
	/// </summary>
	public sealed class RecipientRemoteConfirmationViewModel : ViewModelBase, IWorkflowViewModel
	{
		private static readonly TimeSpan __refreshInterval = TimeSpan.FromSeconds(1);

		private readonly TimeProvider _timeProvider;
		private readonly Lock _refreshLock = new();
		private ITimer? _refreshTimer;
		private DateTimeOffset _deadline;
		private int _remainingAttempts;
		private int _submitting;
		private bool _expired;

		public RecipientRemoteConfirmationViewModel(LanguageManager langManager, ILogger? logger, TimeProvider? timeProvider = null)
			: base(langManager, logger)
		{
			_timeProvider = timeProvider ?? TimeProvider.System;
			this.Keypad = new NumericKeypadViewModel(langManager, "RecipientRemoteConfirmationPage.CodePlaceholder") {
								MinimumCodeLength = RecipientPickupLimits.MinPresenceCodeLength,
								MaximumCodeLength = RecipientPickupLimits.MaxPresenceCodeLength,
							};
			this.CancelCommand = new AsyncCommand<bool>(async (o, ct) => {
					_logger?.LogDebug($"{nameof(RecipientRemoteConfirmationViewModel)}: executing {nameof(CancelCommand)}.");
					// v4: „Otkaži“ zauzima izlaz odmah, bez 200 ms odziva klika: tačan kod potvrđen u tom razmaku je ranije otvarao vrata.
					// Posle roka (tajmer ga još nije video) ishod je istek, ne odustajanje — isti rok kao pri potvrdi koda.
					if (IsExpiredNow()) {
						return true;
					}
					var handler = this.HandleCancelClicked;
					if (handler != null) {
						await handler();
					}
					return true;
				});
		}

		public NumericKeypadViewModel Keypad { get; }

		public ICommand CancelCommand { get; }

		/// <summary>Uneti kod; vraća true kad je kod prihvaćen. Dok se jedan kod proverava, novi se ne predaje.</summary>
		public Func<string, ValueTask<bool>>? HandleCodeConfirmed { get; set; }

		public Func<ValueTask>? HandleCancelClicked { get; set; }

		/// <summary>Rok za unos je istekao; poziva se jednom, sa pool niti.</summary>
		public Func<ValueTask>? HandleExpired { get; set; }

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

		private string? _noticeText;
		/// <summary>Ishod poslednjeg unosa (pogrešan kod).</summary>
		public string? NoticeText {
			get => _noticeText;
			private set => SetProperty(ref _noticeText, value);
		}

		/// <summary>Rok i broj pokušaja za ovaj ulazak na stranicu; poziva se pre <see cref="Activate"/>.</summary>
		public void Prepare(TimeSpan codeTimeout, int remainingAttempts)
		{
			lock (_refreshLock) {
				_deadline = _timeProvider.GetUtcNow() + codeTimeout;
				_remainingAttempts = remainingAttempts;
				_expired = false;
			}
			this.NoticeText = null;
			Refresh();
		}

		/// <summary>Posle pogrešnog koda: poruka i novi broj preostalih pokušaja.</summary>
		public void ShowWrongCode(int remainingAttempts)
		{
			lock (_refreshLock) {
				_remainingAttempts = remainingAttempts;
			}
			this.NoticeText = this["RecipientRemoteConfirmationPage.WrongCode"].Replace("%s", $"{remainingAttempts}");
			Refresh();
		}

		public void Activate()
		{
			_logger?.LogInformation($"{GetType().Name}: aktivacija.");
			this.Keypad.HandleCodeConfirmed = this.OnCodeConfirmed;
			this.Keypad.Clear();
			lock (_refreshLock) {
				_refreshTimer = _timeProvider.CreateTimer(_ => Refresh(), null, __refreshInterval, __refreshInterval);
			}
		}

		public void Deactivate()
		{
			_logger?.LogInformation($"{GetType().Name}: deaktivacija.");
			this.Keypad.HandleCodeConfirmed = null;
			this.Keypad.Clear();
			lock (_refreshLock) {
				_refreshTimer?.Dispose();
				_refreshTimer = null;
			}
			this.HandleCodeConfirmed = null;
			this.HandleCancelClicked = null;
			this.HandleExpired = null;
		}

		private async ValueTask<bool> OnCodeConfirmed(string code)
		{
			var handler = this.HandleCodeConfirmed;
			if (handler == null || Interlocked.Exchange(ref _submitting, 1) == 1) {
				return false;
			}
			// v4: rok se proverava i pri potvrdi (ne samo tajmerom, jednom u sekundi): kod posle roka pokreće istek, ne proveru.
			if (IsExpiredNow()) {
				Volatile.Write(ref _submitting, 0);
				this.Keypad.Clear();
				return false;
			}
			try {
				return await handler(code).ConfigureAwait(false);
			}
			finally {
				this.Keypad.Clear();
				Volatile.Write(ref _submitting, 0);
			}
		}

		/// <summary>v4: osvežava rok (posle roka jednom javlja istek) i kaže da li je rok prošao.</summary>
		private bool IsExpiredNow()
		{
			Refresh();
			lock (_refreshLock) {
				return _expired;
			}
		}

		private void Refresh()
		{
			Func<ValueTask>? expired = null;
			lock (_refreshLock) {
				var remaining = _deadline - _timeProvider.GetUtcNow();
				this.CountdownText = this["RecipientRemoteConfirmationPage.Countdown"].Replace("%s", Format(remaining));
				this.AttemptsText = this["RecipientRemoteConfirmationPage.AttemptsLeft"].Replace("%s", $"{_remainingAttempts}");
				if (remaining <= TimeSpan.Zero && !_expired && _refreshTimer != null) {
					_expired = true;
					expired = this.HandleExpired;
				}
			}
			if (expired != null) {
				_ = NotifyExpired(expired);
			}
		}

		private async Task NotifyExpired(Func<ValueTask> expired)
		{
			try {
				await expired().ConfigureAwait(false);
			}
			catch (Exception ex) {
				// Poziva ga tajmer: neuhvaćen izuzetak se ne bi video.
				_logger?.LogError(ex, $"{GetType().Name}: obrada isteka roka nije uspela");
			}
		}

		private static string Format(TimeSpan remaining)
		{
			var seconds = Math.Max(0, (int) Math.Ceiling(remaining.TotalSeconds));
			return $"{seconds / 60}:{seconds % 60:00}";
		}
	}
}