using System.Reflection;
using System.Text;
using ECR;

namespace TopPos.LockerSimulator.Tests
{
	/// <summary>
	/// Pristup pravom TopPos.ECR (internal tipovi) preko refleksije — TopPos.ECR se ne menja (bez InternalsVisibleTo).
	/// ECR ne proverava LRC, pa je ovo samo dopuna testovima okvira, ne dokaz da je format tačan.
	/// </summary>
	internal static class Ecr
	{
		// Isto kao kiosk (app-configuration.json → PosTerminal.Encoding).
		public const string PosMessageEncoding = "ISO-8859-5";

		private static readonly Assembly __ecr = typeof(EcrPort).Assembly;

		static Ecr()
		{
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			Configuration.posMessageEncoding = PosMessageEncoding;
		}

		public static void EnsureInitialized()
		{
			// pokreće statički konstruktor
		}

		/// <summary>Isto što PosClient.PrepareNewTransaction postavlja (seq se prosleđuje kao string — i neispravan, bez vodećih nula).</summary>
		public static TransParamsV3 NewTransaction(string seqNumber, string transactionType, long amount)
		{
			EnsureInitialized();
			return new TransParamsV3 {
				TerminalId = "00",
				SeqNumber = seqNumber,
				TransactionType = transactionType,
				PrinterFlag = "0",
				CashierId = "00",
				AmountCurrency = "941",
				TransactionNumber = "",
				TransactionAmount1 = amount,
				InsurancePolicyNumber = "",
				InstallmentsNumber = "",
				LanguageID = "00",
				CashierId2 = "",
				PayServices = "",
				TacData = "",
				IPReferenceNumber = "",
				AuthorizationCode = "",
				CashAmount = 0,
				FormattedTextToPrint = "",
				PrintData = Array.Empty<char>(),
				InputLabel = "",
				qrCodeData = "",
				specificProcessingFlag = "",
				radcomSpecificData = "",
				CardDataSource = "",
				CardNumber = "",
				TidNumber = "",
				MidNumber = "",
				CompanyName = "",
				EmvData = "",
				SignatureLine = "",
				FullResponseCode = "",
				TransactionStatus = "",
				PINBlock = "",
				CardholderName = "",
				displayMessage = "",
				PinFlag = "",
				BalanceInquiry = "",
				offlineCryptogram = "",
				LoyaltyData = "",
				dccStatus = "",
				PersonalVehicleCardData = "",
			};
		}

		/// <summary>Zahtev 00 tačno onako kako ga ECR šalje na port (ECR2POSMessage_00V3.toByteArray).</summary>
		public static byte[] SerializeRequest(TransParamsV3 tp)
		{
			var type = __ecr.GetType("ECR.ECR2POSMessage_00V3", throwOnError: true)!;
			object message = type.GetMethod("create", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { tp })!;
			return (byte[])type.GetMethod("toByteArray")!.Invoke(message, null)!;
		}

		/// <summary>Parsira okvir POS → ECR pravim parserom (POS2ECRMessageV3.create), kao CommunicationPortV3.readMessage + process.</summary>
		public static EcrMessage Parse(byte[] frame)
		{
			EnsureInitialized();
			var type = __ecr.GetType("ECR.POS2ECRMessageV3", throwOnError: true)!;
			char[] chars = frame.Select(b => (char)b).ToArray();
			object? message = type.GetMethod("create", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { chars });
			return new EcrMessage(message ?? throw new InvalidOperationException("ECR ne prepoznaje okvir (create je vratio null)."));
		}

		internal sealed class EcrMessage(object message)
		{
			public string TypeName => message.GetType().Name;

			public bool Call(string method) => (bool)message.GetType().GetMethod(method)!.Invoke(message, null)!;

			public string? Field(string name) => message.GetType().GetField(name)?.GetValue(message) as string;

			/// <summary>
			/// Isto što EcrConnectorV3.process radi sa završnim odgovorom 10: updateTransactionParams pa returnTicket
			/// (returnTicket parsira EMV podatke — tu je E2E test našao grešku koju sam parser ne vidi).
			/// </summary>
			public TransParamsV3 ApplyTo(TransParamsV3 tp)
			{
				var args = new object[] { tp };
				message.GetType().GetMethod("updateTransactionParams")!.Invoke(message, args);
				var updated = (TransParamsV3)args[0];
				updated.returnTicket();
				return updated;
			}
		}
	}
}
