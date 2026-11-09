
using System.Reflection;
using System.Text;
using System.Transactions;
using ECR;
using Microsoft.Extensions.Logging;

namespace TopPos.Kiosk
{


	public class PosClient : IPosClient
	{

		/// <summary>
		/// POS-ECR API specification v2.17.pdf
		/// Page 9
		/// RETURN VALUES
		/// </summary>
		public enum ResponseCodes
		{
			NotValidValue = -1,       // Dodeljena vrednost pre poziva DoTransactionV3
			Success = 0,              // Komunikacija s POS terminalom je uspešna, proveriti TransactionFlag
			ComUnavailable = 1,       // Neuspešna komunikacija - POS terminal nije dostupan na COM portu
									  // OVO MOŽE DA SE JAVI AKO JE POKRENUTA NOVA TRANSAKCIJA DOK POS TERMINAL ČEKA KARTICU OD PRETHODNE!!
			NoAckOrNack = 2,          // Neuspešna komunikacija - Grečka u komunikaciji s POS terminalom
			FunctionTimeout = 3,      // Neuspešna komunikacija - Ako nakon poslatog zahteva POS bude nedostupan (npr. otkačim napajanje s POS) (Config.FunctionTimeout=150s)
			InvalidInput = 4,         // Neuspešna komunikacija
			CfgFileMissing = 5,       // Neuspešna komunikacija - Greška u config
			InvalidProductKey = 6,    // Neuspešna komunikacija - Greška u config
			PosError = 7,             // Neuspešna komunikacija - Neispravan format teksta/datuma poslat terminalu, otkačen com port (na primer)
			PosSocketUnreachable = 8, // Neuspešna komunikacija
			TransactionCanceled = 9   // Neuspešna komunikacija - Kada se pozove EcrPort.CancelTransaction(), ili kad korisnik prekine plaćanje na samom POS terminalu (poruka "PREKINUTO")
		}

		/// <summary>
		/// POS-ECR API specification v2.17.pdf
		/// Page 6
		/// Class TransParamsV3 has following properties:
		/// </summary>
		public enum TransactionFlagCodes
		{
			NotValidValue = -1,									// Kada je ResponseCode != 0, onda je TransactionFlag -1 (ne treba ga proveravati)
			ErrorInTheFormatOfTheRequestMessage = 0,			// ** Nisam dobio nikad
			TransactionAcceptedWithoutAuthorizationCode = 1,	// Uspešna transakcija
			TransactionAcceptedWithAuthorization = 2,			// Uspešna transakcija (verovatno je bio unet PIN kartice)
			TransactionRefused = 4,								// Banka je odbila naplatu (ovo nisam mogao da testiram)
			TerminalId = 5,                                     // ** Nisam dobio nikad
			CommError = 6,										// POS terminal nema internet konekciju (kartica provučena, ne može da izvrši naplatu)
			SleepMode = 7,                                      // ** Nisam dobio nikad
			PersonalOrVehicleCardData = 8                       // ** Nisam dobio nikad
		}

		public enum TransactionType
		{
			Sale = 1,
			GetCurrentTotals = 32,
			RestartTerminal = 82,
			ReturnTID= 96

		}

		public bool IsConnected { get; protected set; }

		private readonly ILogger? _logger;

		private readonly PosTerminalConfiguration _posConfig;
		private int _seqNumber;
		Func<string, ValueTask>? _posTerminalMessagesHandler;

		// Postavlja se na true kad od POS terminala stigne poruka "PREKINUTO" (korisnik je prekinuo
		// plaćanje na samom terminalu). Resetuje se na početku svake transakcije u DoTransaction().
		public const string CanceledByUserMessage = "PREKINUTO";
		private volatile bool _transactionCanceledByUser;

		// Poslednji završni odgovor terminala (10) u toku DoTransactionV3 — iz EcrPort.RawdataEvent; briše se pre svakog poziva.
		private volatile PosResponseFrame? _lastResponse;

		// ECR (biblioteka koju ne menjamo) pamti poslednji pročitani odgovor u statičkom polju CommunicationPortV3.readResult
		// i ne briše ga pre čekanja na ACK. Kad terminal ne potvrdi poruku, ostane char[] iz prethodne transakcije, cast u int
		// baci izuzetak i ECR vrati PosError umesto ComUnavailable (na isteku ACK-a ECR zatvori port, pa ponavljanja nema).
		// Zato se polje briše pre svakog DoTransactionV3. Pokriva samo ACK na prvu poruku transakcije; izgubljen ACK kasnije
		// u istoj transakciji i dalje daje PosError. Klijent koristi samo COM (DoTransactionV3), ne DoTransactionTcp.
		private const string EcrReadResultField = "ECR.CommunicationPortV3.readResult";
		private static readonly FieldInfo? __ecrReadResult = typeof(EcrPort).Assembly.GetType("ECR.CommunicationPortV3")
																?.GetField("readResult", BindingFlags.NonPublic | BindingFlags.Static);

		// ECR drži i ostalo stanje transakcije u statičkim poljima (port, tajmeri, otkazivanje), a PosClient ima jedan skup
		// handlera po instanci: dva istovremena DoTransaction-a (npr. CheckPresence iz dijagnostike tokom plaćanja) kvare jedan
		// drugom transakciju. Zato ceo DoTransaction ide pod jednim zaključavanjem; drugi poziv čeka da se prvi završi.
		private static readonly SemaphoreSlim __transactionLock = new(1, 1);

		public PosClient(
			PosTerminalConfiguration posTerminalConfiguration,
			ILogger? logger)
		{
			_posConfig= posTerminalConfiguration;
			_logger = logger;

			// Bez ovoga ne radi ecr.dll
			// na konzoli piše:
			// 'ISO-8859-2' is not a supported encoding name. For information on defining a custom encoding,
			// see the documentation for the Encoding.RegisterProvider method. (Parameter 'name')
			// ResponseCode je PosError
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);		// TODO

			Initialize();
			
			_seqNumber= Math.Abs(DateTime.Now.GetHashCode() % 10000);

			if (__ecrReadResult == null) {
				_logger?.LogWarning($"{nameof(PosClient)}: ECR nema polje {EcrReadResultField} — zaostali odgovor se ne briše (izgubljen ACK se javlja kao PosError).");
			}

			// CheckPresence(CancellationToken.None).ConfigureAwait(false);
		}


		private void Initialize()
		{
			// prepisano iz ecrcfg.xml
			ECR.Configuration.prodKeyE= _posConfig.ProductKey.KeyE;
			ECR.Configuration.prodKey= _posConfig.ProductKey.KeyE;
			ECR.Configuration.prodKeyW= _posConfig.ProductKey.KeyW;

			ECR.Configuration.portName= _posConfig.ComPort.PortName;
			ECR.Configuration.baudRate= _posConfig.ComPort.BaudRate;
			ECR.Configuration.dataBits= _posConfig.ComPort.DataBits;

			ECR.Configuration.stopBits= _posConfig.ComPort.StopBits == System.IO.Ports.StopBits.None ? 0 :
																	_posConfig.ComPort.StopBits == System.IO.Ports.StopBits.One ? 1 :
																	_posConfig.ComPort.StopBits == System.IO.Ports.StopBits.Two ? 2 : 0;

			// ili:
			// ECR.Configuration.stopBits= (int) _posConfig.ComPort.StopBits);
			// ali ima i vrednost 1.5 koji čiji ID je 3 (pa kao da izbegnem problem)

			ECR.Configuration.parity= _posConfig.ComPort.Parity;

			ECR.Configuration.ipaddress= _posConfig.IpAddress;

			ECR.Configuration.retryTimeout= _posConfig.RetryTimeout;
			ECR.Configuration.waitForAckTimeout= _posConfig.WaitForAckTimeout;
			ECR.Configuration.functionTimeout= _posConfig.FunctionTimeout;

			ECR.Configuration.enableLogging= _posConfig.Logging.Enabled;
			ECR.Configuration.detailedLogging= _posConfig.Logging.Detailed;

			ECR.Configuration.sendRespMessage13= _posConfig.ProtocolV1.SendRespMessage13;

			ECR.Configuration.amountCurrency= _posConfig.InputV2.AmountCurrency;
			ECR.Configuration.posMessageEncoding= _posConfig.Encoding;

			// Uključuje EcrPort.RawdataEvent (samo događaj, ne utiče na komunikaciju): odatle se uzima sirovi odgovor 10 za
			// poređenje sa zahtevom (LogResponseCheck).
			EcrPort.EnableRealtimeRawdataDebug = true;

			// prepisano iz Configuration()
			ECR.Configuration.deviceVendor = "Ingenico";
			ECR.Configuration.deviceVid = string.Empty;
			ECR.Configuration.devicePid = string.Empty;

			// fejkuj da je config učitan iz fajla
			ECR.Configuration.isReadFRomFile= true;

		}


		private TransParamsV3 PrepareNewTransaction(TransactionType type, decimal amount = 1)
		{
			TransParamsV3 transParamsV3= new TransParamsV3();

			transParamsV3.TerminalId = "00";
			//_seqNumber
			_seqNumber= (_seqNumber+1)%10000;
			// Seq mora imati tačno 4 cifre: terminal čita zaglavlje po fiksnim pozicijama, pa bi kraći seq pomerio
			// tip transakcije (npr. seq 205 + tip 96 → terminal vidi tip "60") i terminal vraća "POGRESAN UNOS".
			transParamsV3.SeqNumber = _seqNumber.ToString("0000");
			// transParamsV3.TransactionType = "01";
			transParamsV3.TransactionType = $"{(int)type:00}";
			transParamsV3.PrinterFlag = "0";
			transParamsV3.CashierId = "00";
			transParamsV3.AmountCurrency = _posConfig.InputV2.AmountCurrency;
			transParamsV3.TransactionNumber = "";
			transParamsV3.TransactionAmount1 = Convert.ToInt64(amount * 100);
			transParamsV3.InsurancePolicyNumber = "";
			transParamsV3.InstallmentsNumber = "";
			transParamsV3.LanguageID = "00";
			transParamsV3.CashierId2 = "";
			transParamsV3.PayServices = "";
			transParamsV3.TacData = "";
			transParamsV3.IPReferenceNumber = "";

			transParamsV3.AuthorizationCode = "";
			transParamsV3.CashAmount = 00;
			transParamsV3.FormattedTextToPrint = "";
			transParamsV3.PrintData = Array.Empty<char>();
			transParamsV3.InputLabel = "";
			transParamsV3.qrCodeData = "";

			transParamsV3.specificProcessingFlag = "";
			transParamsV3.radcomSpecificData = "";

			transParamsV3.CardDataSource = "";
			transParamsV3.CardNumber = "";
			transParamsV3.TidNumber = "";
			transParamsV3.MidNumber = "";
			transParamsV3.CompanyName = "";
			transParamsV3.EmvData = "";
			transParamsV3.SignatureLine = "";
			transParamsV3.FullResponseCode = "";
			transParamsV3.TransactionStatus = "";
			transParamsV3.PINBlock = "";
			transParamsV3.CardholderName = "";
			// transParamsV3.RetrievalReferenceNumber = “”;
			//transParamsV3.totals = Empty;
			transParamsV3.displayMessage = "";
			transParamsV3.PrintData = Array.Empty<char>();
			transParamsV3.CashierId2 = "";
			transParamsV3.PinFlag = "";
			// transParamsV3.transactions = Empty;
			transParamsV3.PayServices = "";
			transParamsV3.BalanceInquiry = "";
			transParamsV3.offlineCryptogram = "";
			transParamsV3.TacData = "";
			transParamsV3.LoyaltyData = "";
			transParamsV3.FormattedTextToPrint = "";
			transParamsV3.dccStatus = "";
			transParamsV3.IPReferenceNumber = "";
			transParamsV3.qrCodeData = "";
			transParamsV3.PersonalVehicleCardData = "";

			return transParamsV3;

		}


		// ECR delegati su async void: izuzetak koji iz njih izađe ne može se uhvatiti kod pozivaoca i obara proces
		// (usred plaćanja). Zato se hvata ovde — gubi se samo prikaz poruke terminala, transakcija teče dalje.
		private async void EcrMessageReceivedHandler(string messageIdentifier, string message)
		{
			try {
				_logger?.LogInformation($"{nameof(PosClient)}[{_posConfig.ComPort.PortName}]: Called ECR delegate {nameof(EcrPort.EcrMessageReceived)}, Parameters: messageIdentifier={messageIdentifier}, message={message}");
				if (_posTerminalMessagesHandler != null) {
					await _posTerminalMessagesHandler(message).ConfigureAwait(false);
				}
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(PosClient)}: {nameof(EcrMessageReceivedHandler)} exception: {ex.Message}");
			}
		}

		private async void EcrExtendedMessageReceivedHandler(string messageIdentifier, string code, string message)
		{
			try {
				_logger?.LogInformation($"{nameof(PosClient)}[{_posConfig.ComPort.PortName}]: Called ECR delegate {nameof(EcrPort.EcrExtendedMessageReceived)}, Parameters: messageIdentifier={messageIdentifier}, code={code}, message={message}");
				if (string.Equals(message?.Trim(), CanceledByUserMessage, StringComparison.OrdinalIgnoreCase)) {
					// Korisnik je prekinuo plaćanje na POS terminalu. Pamtimo to da bismo u DoTransaction()
					// razlikovali prekid od stvarne HW greške (ECR i u jednom i u drugom slučaju vrati PosError).
					_transactionCanceledByUser = true;
				}
				if (_posTerminalMessagesHandler != null) {
					await _posTerminalMessagesHandler(message).ConfigureAwait(false);
				}
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(PosClient)}: {nameof(EcrExtendedMessageReceivedHandler)} exception: {ex.Message}");
			}
		}

		private void EcrAs24MessageReceivedHandler(string messageIdentifier, string track2data, string message, string bfoc) 
			=> _logger?.LogInformation($"{nameof(PosClient)}[{_posConfig.ComPort.PortName}]: Called ECR delegate {nameof(EcrPort.EcrAs24MessageReceived)}, Parameters: messageIdentifier={messageIdentifier}, track2data={track2data}, message={message}, bfoc={bfoc}");

		// Događaj stiže iz ECR-a usred transakcije: izuzetak ne sme da izađe. Pamti se poslednji odgovor 10 (završni odgovor).
		private void RawdataEventHandler(EcrPort.DataDirection direction, byte[] rawdata)
		{
			try {
				_logger?.LogDebug($"{nameof(PosClient)}[{_posConfig.ComPort.PortName}]: Called ECR delegate {nameof(EcrPort.RawdataEvent)}, Parameters: direction={direction}, length={rawdata?.Length}");
				if (direction == EcrPort.DataDirection.POS2ECR && rawdata != null && PosResponseFrame.TryParse(rawdata) is { } response) {
					_lastResponse = response;
				}
			}
			catch (Exception ex) {
				_logger?.LogWarning($"{nameof(PosClient)}: {nameof(RawdataEventHandler)} exception: {ex.Message}");
			}
		}

		/// <summary>
		/// Poredi završni odgovor terminala sa poslatim zahtevom (seq, tip, iznos, valuta) i proverava LRC — samo za log; ishod
		/// naplate se ne menja (u ECR logovima tri paketomata odstupanja nije bilo).
		/// </summary>
		// Izuzetak ne sme da izađe: poziva se pre čitanja flag-a transakcije, pa bi odobrena naplata ispala neuspela.
		private void LogResponseCheck(TransactionType type, string seq, string transactionType, long amountInPara)
		{
			try {
				var response = _lastResponse;
				if (response == null) {
					_logger?.LogWarning($"{nameof(PosClient)}[{_posConfig.ComPort.PortName}]: {type}: završni odgovor terminala (10) nije zabeležen; poređenje sa zahtevom (seq={seq}) nije urađeno.");
					return;
				}
				var differences = response.Differences(seq, transactionType, type == TransactionType.Sale ? amountInPara : null, _posConfig.InputV2.AmountCurrency);
				if (differences.Count > 0) {
					_logger?.LogError($"{nameof(PosClient)}[{_posConfig.ComPort.PortName}]: {type}: odgovor terminala ne odgovara zahtevu ({string.Join(", ", differences)}); " +
										$"odgovor: {response}. Ishod transakcije se ne menja.");
				}
				else {
					_logger?.LogInformation($"{nameof(PosClient)}[{_posConfig.ComPort.PortName}]: {type}: odgovor terminala odgovara zahtevu ({response}).");
				}
			}
			catch (Exception ex) {
				_logger?.LogWarning($"{nameof(PosClient)}: {nameof(LogResponseCheck)} exception: {ex.Message}");
			}
		}

		private void ClearStaleEcrReadResult()
		{
			_lastResponse = null;	// odgovor prethodnog poziva ne sme da se poredi sa sledećim
			try {
				__ecrReadResult?.SetValue(null, null);
			}
			catch (Exception ex) {
				_logger?.LogWarning($"{nameof(PosClient)}: brisanje {EcrReadResultField} nije uspelo: {ex.Message}");
			}
		}

		private void SetMessageHandlers()
		{
			EcrPort.EcrMessageReceived += EcrMessageReceivedHandler;
			EcrPort.EcrExtendedMessageReceived += EcrExtendedMessageReceivedHandler;
			EcrPort.EcrAs24MessageReceived += EcrAs24MessageReceivedHandler;
			EcrPort.RawdataEvent += RawdataEventHandler;
		}

		private void RemoveMessageHandlers()
		{
			EcrPort.EcrMessageReceived -= EcrMessageReceivedHandler;
			EcrPort.EcrExtendedMessageReceived -= EcrExtendedMessageReceivedHandler;
			EcrPort.EcrAs24MessageReceived -= EcrAs24MessageReceivedHandler;
			EcrPort.RawdataEvent -= RawdataEventHandler;
		}

		public async ValueTask<(PosClient.ResponseCodes, TransactionFlagCodes, string?, string?)> DoTransaction(TransactionType type, decimal amount, Func<string, ValueTask>? posTerminalMessagesHandler, CancellationToken cancellationToken)
		{
			ResponseCodes response = ResponseCodes.NotValidValue;
			TransactionFlagCodes transactionFlag = TransactionFlagCodes.NotValidValue;
			string? transactionNumber = null;
			string? tidNumber = null;

			// Pre zaključavanja se ne dira ništa zajedničko: handleri su isti delegati za istu instancu, pa bi njihovo
			// skidanje u finally skinulo handlere transakcije koja je u toku.
			if (__transactionLock.CurrentCount == 0) {
				_logger?.LogInformation($"{nameof(PosClient)}[{_posConfig.ComPort.PortName}]: {type} čeka završetak POS transakcije koja je u toku.");
			}
			try {
				await __transactionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) {
				_logger?.LogWarning($"{nameof(PosClient)}[{_posConfig.ComPort.PortName}]: {type} otkazan dok je čekao drugu POS transakciju.");
				return (response, transactionFlag, transactionNumber, tidNumber);
			}

			_posTerminalMessagesHandler = posTerminalMessagesHandler;
			_transactionCanceledByUser = false;
			try {

				SetMessageHandlers();

				TransParamsV3 transParamsV3 = PrepareNewTransaction(type, amount);
				// ECR iznos u transParamsV3 prepisuje vrednošću iz odgovora, pa se zahtev pamti pre poziva
				var (requestSeq, requestType, requestAmount) = (transParamsV3.SeqNumber, transParamsV3.TransactionType, transParamsV3.TransactionAmount1);

				using var registration = cancellationToken.Register(() => {
					_logger?.LogInformation($"{nameof(PosClient)}[{_posConfig.ComPort.PortName}]: Cancellation received.");
					EcrPort.CancelTransaction();
				});

				await Task.Yield();

				ClearStaleEcrReadResult();
				response = (ResponseCodes) EcrPort.DoTransactionV3(ref transParamsV3);

				bool transactionCanceled = false;
				if(response == ResponseCodes.FunctionTimeout && !cancellationToken.IsCancellationRequested) {
					// ponavljamo transakciju 
					_logger?.LogError($"{nameof(PosClient)}[{_posConfig.ComPort.PortName}]: DoTransactionV3 error: {response.ToString()}. Sending same same request.");
					ClearStaleEcrReadResult();
					response = (ResponseCodes) EcrPort.DoTransactionV3(ref transParamsV3);
					if(response != ResponseCodes.Success && !cancellationToken.IsCancellationRequested) {
						// u slučaju greške, otkazujemo transakciju
						_logger?.LogError($"{nameof(PosClient)}[{_posConfig.ComPort.PortName}]: Unsuccessful DoTransactionV3: {response.ToString()}. Canceling transaction.");
						transParamsV3.TransactionType= "11";    // Cancel Transaction
						ClearStaleEcrReadResult();
						var cancelResponse = (ResponseCodes) EcrPort.DoTransactionV3(ref transParamsV3);
						_logger?.LogError($"{nameof(PosClient)}[{_posConfig.ComPort.PortName}]: Canceling DoTransactionV3 response: {cancelResponse.ToString()}, TransactionFlag={transParamsV3.TransactionFlag}.");
						// Odgovor na otkazivanje NIJE rezultat transakcije: i kad terminal prihvati 11 (flag 01/02), naplata
						// nije izvršena. Vraćamo grešku same transakcije, da pozivalac ne bi poslao potvrdu plaćanja.
						transactionCanceled = true;
						// Ishod na terminalu je nepoznat (istek vremena) - kupac je možda ipak zadužen; podaci za poređenje sa izveštajem banke.
						_logger?.LogError($"{nameof(PosClient)}[{_posConfig.ComPort.PortName}]: transakcija {type} sa nepoznatim ishodom: SeqNumber={transParamsV3.SeqNumber}, Amount={amount:F2}, odgovor={response.ToString()}.");
					}
				}

				if (response == ResponseCodes.Success && !transactionCanceled) {
					LogResponseCheck(type, requestSeq, requestType, requestAmount);
				}

				transactionFlag = !transactionCanceled && transParamsV3.TransactionFlag != null ?
								(TransactionFlagCodes) Int32.Parse(transParamsV3.TransactionFlag) :
								TransactionFlagCodes.NotValidValue;

				if(response != ResponseCodes.Success && _transactionCanceledByUser) {
					// Korisnik je prekinuo plaćanje na samom POS terminalu (poruka "PREKINUTO").
					// ECR vraća PosError, pa ovde normalizujemo na TransactionCanceled da bi pozivalac
					// mogao da razlikuje prekid od stvarne greške u radu terminala.
					response = ResponseCodes.TransactionCanceled;
				}

				if(response == ResponseCodes.Success) {
					if(transactionFlag == TransactionFlagCodes.TransactionAcceptedWithoutAuthorizationCode || transactionFlag == TransactionFlagCodes.TransactionAcceptedWithAuthorization) {
						if(type == TransactionType.Sale) {
							// TODO: treba da vrati TransactionNumber, da bi se upisao u PosTransakcijaId pri potvrdi plaćanja
							transactionNumber= transParamsV3.TransactionNumber;
							tidNumber= transParamsV3.TidNumber;
						}
						/*
						else if(type == TransactionType.GetCurrentTotals) {
							transactionNumber= transParamsV3.Ticket;
						}
						*/
					}
					else if(transactionFlag == TransactionFlagCodes.TerminalId) {
						transactionNumber= "";
						tidNumber= transParamsV3.TidNumber;
					}
				}

				_logger?.LogInformation($"{nameof(PosClient)}[{_posConfig.ComPort.PortName}]: {nameof(Sale)}: returned: {response.ToString()}, {transactionFlag.ToString()}");

			}
			catch(Exception ex) {
				_logger?.LogError($"{nameof(PosClient)}[{_posConfig.ComPort.PortName}]: {nameof(Sale)} Exception: {ex.Message}");
			}
			finally {
				RemoveMessageHandlers();
				__transactionLock.Release();
			}

			return (response, transactionFlag, transactionNumber, tidNumber);
		}

		/// <summary>
		/// DoTransaction - Sale
		/// </summary>
		/// <param name="amount"></param>
		/// <param name="posTerminalMessagesHandler"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public async ValueTask<(PosClient.ResponseCodes, TransactionFlagCodes, string?, string?)> Sale(decimal amount, Func<string, ValueTask>? posTerminalMessagesHandler, CancellationToken cancellationToken)
		{
			return await DoTransaction(TransactionType.Sale, amount, posTerminalMessagesHandler, cancellationToken);
		}

		/*
		public async ValueTask<(PosClient.ResponseCodes, TransactionFlagCodes, string?)> GetCurrentTotals(Func<string, ValueTask>? posTerminalMessagesHandler, CancellationToken cancellationToken)
		{
			return await DoTransaction(TransactionType.GetCurrentTotals, 0, posTerminalMessagesHandler, cancellationToken);
		}


		public async ValueTask<(PosClient.ResponseCodes, TransactionFlagCodes, string?)> RestartTerminal(Func<string, ValueTask>? posTerminalMessagesHandler, CancellationToken cancellationToken)
		{
			return await DoTransaction(TransactionType.RestartTerminal, 0, posTerminalMessagesHandler, cancellationToken);
		}
		*/

		/// <summary>
		/// Get Terminal ID Number
		/// </summary>
		/// <param name="posTerminalMessagesHandler"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public async ValueTask<(PosClient.ResponseCodes, TransactionFlagCodes, string?, string?)> GetTidNumber(Func<string, ValueTask>? posTerminalMessagesHandler, CancellationToken cancellationToken)
		{
			return await DoTransaction(TransactionType.ReturnTID, 1, posTerminalMessagesHandler, cancellationToken);
		}
		

		public async Task<bool> CheckPresence(CancellationToken cancellationToken)
		{
			try {
				var (status, transactionFlag, transactionNumber, tidNumber) = await GetTidNumber(null, cancellationToken);
				this.IsConnected = status == ResponseCodes.Success;
				return this.IsConnected;
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(PosClient)}[{_posConfig.ComPort.PortName}]: {nameof(CheckPresence)}: exception: {ex.Message}");
				this.IsConnected = false;
				return false;
			}
		}
	}

}