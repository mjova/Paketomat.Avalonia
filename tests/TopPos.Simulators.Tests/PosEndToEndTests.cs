using System.Collections.Concurrent;
using ECR;
using Flatsoft.Common;
using TopPos.Kiosk;
using TopPos.LockerSimulator.Pos;
using TopPos.Simulators.Serial;
using Xunit;

namespace TopPos.LockerSimulator.Tests
{
	[CollectionDefinition(Name, DisableParallelization = true)]
	public class EcrStaticStateCollection
	{
		// TopPos.ECR drži konfiguraciju i stanje porta u statičkim poljima — ECR testovi idu jedan po jedan.
		public const string Name = "ECR static state";
	}

	/// <summary>
	/// Pravi TopPos.ECR (EcrPort.DoTransactionV3, kao PosClient) preko virtuelnog para portova protiv simulatora.
	/// Windows: pokreće se samo kad je postavljena promenljiva POS_SIM_E2E_PORTS="COM20,COM21" (com0com: ECR strana, simulator);
	/// ako je postavljena a par ne radi, testovi padaju. v4, Linux: bez promenljive test sam pravi pty par (<see cref="PtySerialPair"/>). POS_SIM_E2E_ECRLOG=1 uključuje ECR log (bin/.../Log/std_log_*.log).
	/// </summary>
	[Collection(EcrStaticStateCollection.Name)]
	public sealed class PosEndToEndTests : IAsyncLifetime
	{
		private const string PortsVariable = "POS_SIM_E2E_PORTS";
		private const string SkipReason = $"Promenljiva {PortsVariable} nije postavljena (npr. \"COM20,COM21\" za com0com par); na Linuxu par pravi sam test.";
		private static readonly TimeSpan __transactionTimeout = TimeSpan.FromSeconds(40);

		private static readonly ConcurrentQueue<string> __ecrMessages = new();
		private static bool __handlersAttached;

		private readonly string? _ecrPort;
		private readonly string? _simulatorPort;
		private readonly PtySerialPair? _pair;
		private readonly CancellationTokenSource _cts = new();
		private SerialPosWire? _wire;
		private PosTerminalEngine _engine = default!;
		private Task _run = Task.CompletedTask;

		public PosEndToEndTests()
		{
			string[]? ports = Environment.GetEnvironmentVariable(PortsVariable)?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
			if (ports is { Length: 2 }) {
				(_ecrPort, _simulatorPort) = (ports[0], ports[1]);
			}
			else if (OperatingSystem.IsLinux()) {
				_pair = new PtySerialPair(Path.Combine(Path.GetTempPath(), $"toppos-sim-{Guid.NewGuid():N}", "ttyPOS"), logger: null);
				(_ecrPort, _simulatorPort) = (_pair.KioskPortName, _pair.SimulatorPortName);
			}
		}

		public async Task InitializeAsync()
		{
			if (_ecrPort == null || _simulatorPort == null) {
				return;
			}
			ConfigureEcr(_ecrPort);
			__ecrMessages.Clear();

			_wire = new SerialPosWire(new ComPortConfiguration { PortName = _simulatorPort, BaudRate = 9600, DataBits = 8, Parity = "None" });
			_engine = new PosTerminalEngine(_wire, new PosSimulatorOptions {
				SpeedFactor = 0.05,
				CardWaitTimeout = TimeSpan.FromSeconds(10),
				AutoCustomerDelay = TimeSpan.FromMilliseconds(100),
			});
			_wire.Start();
			_run = _engine.RunAsync(_cts.Token);

			var deadline = DateTime.UtcNow.AddSeconds(5);
			while (!_wire.Status.EndsWith("otvoren")) {
				if (DateTime.UtcNow > deadline) {
					throw new InvalidOperationException($"{PortsVariable} je postavljen, ali simulator ne može da otvori {_simulatorPort}: {_wire.Status}");
				}
				await Task.Delay(50);
			}
			if (_pair != null && !_pair.WaitForSimulator(TimeSpan.FromSeconds(5))) {
				throw new InvalidOperationException($"Pumpa pty para nije videla otvoren {_simulatorPort}");
			}
		}

		public async Task DisposeAsync()
		{
			_cts.Cancel();
			if (_wire != null) {
				await _wire.DisposeAsync();
			}
			await _run;
			if (_pair != null) {
				_pair.Dispose();
				Directory.Delete(Path.GetDirectoryName(_pair.KioskPortName)!, recursive: true);
			}
			_cts.Dispose();
		}

		/// <summary>Isto što radi PosClient.Initialize (vrednosti iz app-configuration.json kioska), uz kraće timeout-e.</summary>
		private static void ConfigureEcr(string portName)
		{
			Ecr.EnsureInitialized();
			Configuration.portName = portName;
			Configuration.baudRate = 9600;
			Configuration.dataBits = 8;
			Configuration.stopBits = 1;
			Configuration.parity = "None";
			Configuration.retryTimeout = 1000;
			Configuration.waitForAckTimeout = 2000;
			Configuration.functionTimeout = 20000;
			Configuration.enableLogging = Environment.GetEnvironmentVariable("POS_SIM_E2E_ECRLOG") == "1";
			Configuration.detailedLogging = Configuration.enableLogging;
			Configuration.amountCurrency = "941";
			Configuration.posMessageEncoding = Ecr.PosMessageEncoding;
			Configuration.deviceVendor = "Ingenico";
			Configuration.deviceVid = string.Empty;
			Configuration.devicePid = string.Empty;
			Configuration.isReadFRomFile = true;

			if (!__handlersAttached) {
				// EcrPort poziva statičke događaje bez provere na null (EcrPort.cs:122, :287).
				EcrPort.EcrMessageReceived += (id, message) => __ecrMessages.Enqueue($"{id}:{message}");
				EcrPort.EcrExtendedMessageReceived += (id, code, message) => __ecrMessages.Enqueue($"{id}:{code}:{message}");
				EcrPort.EcrAs24MessageReceived += (_, _, _, _) => { };
				EcrPort.RawdataEvent += (_, _) => { };
				__handlersAttached = true;
			}
		}

		private static async Task<(int ResponseCode, TransParamsV3 Params)> DoTransaction(string seq, string type, long amount = 150000)
		{
			return await Task.Run(() => {
				var tp = Ecr.NewTransaction(seq, type, amount);
				int responseCode = EcrPort.DoTransactionV3(ref tp);
				return (responseCode, tp);
			}).WaitAsync(__transactionTimeout);
		}

		private void WhenPhase(PosTerminalPhase phase, Action action)
		{
			int done = 0;
			_engine.StatusChanged += s => {
				if (s.Phase == phase && Interlocked.Exchange(ref done, 1) == 0) {
					action();
				}
			};
		}

		[SkippableFact]
		public async Task Sale_Approved()
		{
			Skip.If(_ecrPort == null, SkipReason);
			_engine.AutoCustomer = true;
			_engine.AutoCard = SimCard.MastercardContactless;

			var (rc, tp) = await DoTransaction("1001", "01");

			Assert.Equal(0, rc);
			Assert.Equal("02", tp.TransactionFlag);
			Assert.Equal("SIMT0001", tp.TidNumber);
			Assert.False(string.IsNullOrEmpty(tp.TransactionNumber));
			Assert.Contains("25:51:PROVUCI/UBACI KARTICU", __ecrMessages);
		}

		[SkippableFact]
		public async Task Sale_ChipWithPin_Approved()
		{
			Skip.If(_ecrPort == null, SkipReason);
			_engine.AutoCustomer = true;
			_engine.AutoCard = SimCard.VisaChip;

			var (rc, tp) = await DoTransaction("1002", "01");

			Assert.Equal(0, rc);
			Assert.Equal("02", tp.TransactionFlag);
			Assert.Contains("25:55:UNOS PIN-a", __ecrMessages);
		}

		[SkippableFact]
		public async Task Sale_Declined()
		{
			Skip.If(_ecrPort == null, SkipReason);
			_engine.AutoCustomer = true;
			_engine.AutoCard = SimCard.DinacardDeclined;

			var (rc, tp) = await DoTransaction("1003", "01");

			Assert.Equal(0, rc);
			Assert.Equal("04", tp.TransactionFlag);
		}

		[SkippableFact]
		public async Task Sale_CanceledByCustomerOnTerminal_IsPosErrorWithPrekinuto()
		{
			Skip.If(_ecrPort == null, SkipReason);
			WhenPhase(PosTerminalPhase.WaitingForCard, _engine.CustomerCancel);

			var (rc, _) = await DoTransaction("1004", "01");

			Assert.Equal(7, rc);	// PosError — PosClient ga po poruci PREKINUTO normalizuje u TransactionCanceled
			Assert.Contains("26:09:PREKINUTO", __ecrMessages);
		}

		[SkippableFact]
		public async Task Sale_CanceledByEcr_IsTransactionCanceled()
		{
			Skip.If(_ecrPort == null, SkipReason);
			WhenPhase(PosTerminalPhase.WaitingForCard, () => Task.Delay(200).ContinueWith(_ => EcrPort.CancelTransaction()));

			var (rc, _) = await DoTransaction("1005", "01");

			Assert.Equal(9, rc);	// TransactionCanceled
		}

		[SkippableFact]
		public async Task Tid_ReturnsTidNumber()
		{
			Skip.If(_ecrPort == null, SkipReason);

			var (rc, tp) = await DoTransaction("1006", "96", 100);

			Assert.Equal(0, rc);
			Assert.Equal("05", tp.TransactionFlag);
			Assert.Equal("SIMT0001", tp.TidNumber);
		}

		[SkippableFact]
		public async Task ShortSeq_IsRejectedWithPosError()
		{
			Skip.If(_ecrPort == null, SkipReason);

			var (rc, _) = await DoTransaction("205", "96", 100);

			Assert.Equal(7, rc);
			Assert.Contains("26:06:POGRESAN UNOS", __ecrMessages);
		}

		[SkippableFact]
		public async Task NoResponse_AfterSuccessfulTransaction_IsComUnavailableOrPosError()
		{
			Skip.If(_ecrPort == null, SkipReason);
			Assert.Equal(0, (await DoTransaction("1007", "96", 100)).ResponseCode);
			_engine.Faults.Arm(PosFault.NoResponse, 1);

			var (rc, _) = await DoTransaction("1008", "96", 100);

			// ECR posle isteka ACK-a zatvori port; ishod zavisi od zaostalog statičkog readResult (vidi logove 2026-06-02 i 2026-07-24).
			// Ovde se zove sam ECR; PosClient polje briše pre svakog poziva, pa kiosk dobija ComUnavailable (E2E test PosNoResponse).
			Assert.Contains(rc, new[] { 1, 7 });
		}

		/// <summary>
		/// Kioskov PosClient: CheckPresence (npr. iz dijagnostike) dok Sale čeka karticu ne sme da pokrene drugu ECR
		/// transakciju — čeka da se Sale završi, pa oba uspevaju. Bez zaključavanja drugi poziv padne na zauzetom portu i
		/// usput pokvari statičko stanje ECR-a i handlere plaćanja koje je u toku.
		/// </summary>
		[SkippableFact]
		public async Task PosClient_CheckPresenceDuringSale_WaitsForSale()
		{
			Skip.If(_ecrPort == null, SkipReason);
			var client = new PosClient(KioskPosConfiguration(_ecrPort!), logger: null);
			var waitingForCard = new TaskCompletionSource();
			WhenPhase(PosTerminalPhase.WaitingForCard, () => waitingForCard.TrySetResult());

			var sale = Task.Run(async () => await client.Sale(100, null, CancellationToken.None));
			await waitingForCard.Task.WaitAsync(__transactionTimeout);
			var presence = Task.Run(() => client.CheckPresence(CancellationToken.None));
			await Task.Delay(TimeSpan.FromMilliseconds(500));
			Assert.False(presence.IsCompleted, "CheckPresence nije čekao Sale koji je u toku.");

			_engine.PresentCard(SimCard.MastercardContactless);
			var (rc, flag, transactionNumber, _) = await sale.WaitAsync(__transactionTimeout);
			Assert.True(await presence.WaitAsync(__transactionTimeout));

			Assert.Equal(PosClient.ResponseCodes.Success, rc);
			Assert.Equal(PosClient.TransactionFlagCodes.TransactionAcceptedWithAuthorization, flag);
			Assert.False(string.IsNullOrEmpty(transactionNumber));
		}

		/// <summary>Kao app-configuration.json kioska (PosTerminal), uz timeout-e iz <see cref="ConfigureEcr"/>.</summary>
		private static PosTerminalConfiguration KioskPosConfiguration(string portName)
			=> new() {
				TerminalInstalled = true,
				ProductKey = new ProductKeyConfiguration { KeyE = "", KeyW = "" },
				ComPort = new ComPortConfiguration { PortName = portName, BaudRate = 9600, DataBits = 8, StopBits = System.IO.Ports.StopBits.One, Parity = "None" },
				IpAddress = "",
				RetryTimeout = Configuration.retryTimeout,
				WaitForAckTimeout = Configuration.waitForAckTimeout,
				FunctionTimeout = Configuration.functionTimeout,
				Logging = new LoggingConfiguration { Enabled = Configuration.enableLogging, Detailed = Configuration.detailedLogging },
				ProtocolV1 = new ProtocolV1Configuration(),
				InputV2 = new InputV2Configuration { AmountCurrency = "941" },
				Encoding = Ecr.PosMessageEncoding,
			};

		[SkippableFact]
		public async Task FreezeAfterAck_IsFunctionTimeout()
		{
			Skip.If(_ecrPort == null, SkipReason);
			_engine.Faults.Arm(PosFault.FreezeAfterAck, 1);
			int functionTimeout = Configuration.functionTimeout;
			Configuration.functionTimeout = 3000;
			try {
				var (rc, _) = await DoTransaction("1009", "01");

				Assert.Equal(3, rc);	// FunctionTimeout
			}
			finally {
				Configuration.functionTimeout = functionTimeout;
			}
		}
	}
}
