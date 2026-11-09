using Flatsoft.Common;
using Flatsoft.Common.UI;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using TopPos.Client.WPF.Pages;
using TopPos.Client.WPF.Workflow;
using TopPos.LockController;
using TopPos.PenPosService;
using Stateless;
using TopPos.Service;

namespace TopPos.Client.WPF.ViewModels
{
	/// <summary>
	/// Pogled koji activator klase (Faza 5) imaju na MainWindowViewModel: deljeni servisi/trackeri,
	/// deljene UI komponente, i host operacije (navigacija stranica, poruke, door helpers, fire reset).
	/// MWVM implementira ovaj interfejs i prosleđuje sebe kao host svakom activatoru.
	///
	/// Svrha: activatori dobijaju jednu zavisnost (host) umesto ~15 polja, a tela premeštenih
	/// <c>GoTo*</c> metoda ostaju skoro netaknuta jer <see cref="WorkflowActivatorBase"/> izlaže
	/// članove istih imena kao originalna MWVM polja.
	/// </summary>
	internal interface IWorkflowHost
	{
		// ─── Konfiguracija / servisi / trackeri ────────────────────────────────
		AppConfiguration Configuration { get; }
		ILockControllerClient LockControllerClient { get; }
		/// <summary>Pauzira DoorWatcher i čeka njegov poziv kontroleru u toku (pre otvaranja vrata).</summary>
		Task PauseDoorWatcherAsync();
		TopPosServiceClient ServiceClient { get; }
		BarcodeScannerClient BarcodeScannerClient { get; }
		IPosClient? PosClient { get; }
		PenPosServiceClient? PenPosServiceClient { get; }
		LanguageManager LanguageManager { get; }
		AudioPlayer AudioPlayer { get; }
		PaymentConfirmationSender PaymentConfirmationSender { get; }
		ServerConfirmationSender ServerConfirmationSender { get; }
		BarcodeScanTracker BarcodeScanTracker { get; }
		OperationCountdownTimer OperationCountdownTimer { get; }
		WorkflowContext WorkflowContext { get; }
		WorkflowMachine WorkflowMachine { get; }
		ILogger? Logger { get; }

		// ─── Deljene UI komponente (koriste ih više activatora) ─────────────────
		CourierBarcodeScanViewModel CourierBarcodeScanViewModel { get; }
		CourierBarcodeScanPage CourierBarcodeScanPage { get; }

		// ─── Host operacije ─────────────────────────────────────────────────────
		/// <summary>Lokalizacija po ključu (zamena za ViewModelBase indekser <c>this[key]</c>).</summary>
		string Localize(string key);
		ValueTask DisplayMessage(string message, MessageType messageType, int? displayTimeInSeconds = null);
		void RemoveMessage();
		void SetCurrentPage(UserControl page);
		IWorkflowViewModel? CurrentWorkflowViewModel { get; set; }
		void SetCurrentStep(int stepNumber);
		string MakeCurrentLanguagePath(string filename);
		ValueTask FireResetAsync();

		/// <summary>
		/// Sesija čiji se OnEntry izvršava (aktivna rezervacija koordinatora), ili null. OnEntry je hvata za callback-ove koji se
		/// izvršavaju kasnije (skener, tracker, dugme posle odziva, rok ekrana, fetch): oni rade i okidaju trigere samo dok je ta
		/// sesija aktivna — inače bi zakasneli callback stare sesije prekinuo ili pomerio sledeću.
		/// </summary>
		KioskLease? CurrentSession { get; }
		bool IsSessionActive(KioskLease? session);
		/// <summary>Triger koji se izvršava samo ako je <paramref name="session"/> i dalje aktivna kad dođe na red.</summary>
		Task FireInSession(KioskLease? session, WorkflowTrigger trigger);
		/// <inheritdoc cref="FireInSession(KioskLease?, WorkflowTrigger)"/>
		Task FireInSession<TArg>(KioskLease? session, StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<TArg> trigger, TArg arg);
		/// <summary>
		/// Posao koji kiosk mora da završi pre oslobađanja (povrat paketa, potvrda poslata sa dugmeta): pokreće se samo dok je
		/// sesija aktivna, a nova sesija ne počinje pre njegovog kraja. Null = sesija se završava, posao nije pokrenut.
		/// </summary>
		Task? RunInSession(KioskLease? session, Func<Task> work);
		/// <summary>
		/// Pokreće praćenje vrata za tekuću sesiju; vraćeni Task se završava kad se praćenje završi (posle callback-a, ili bez
		/// njega ako je zaustavljeno ili se sesija završila). Van aktivne sesije praćenje se ne pokreće.
		/// </summary>
		Task TrackDoorStatus(int doorNumber, DoorStatusCode targetStatusCode, Func<DoorStatusCode?, ValueTask> notifyComplete);
		ValueTask HandleDoorActionFailure(int doorNumber, DoorStatusCode? unexpectedStatusCode, int messageDisplayTime,
										Func<ValueTask>? gotoPage, Serilog.ILogger contextLogger, CancellationToken cancellationToken);
	}
}
