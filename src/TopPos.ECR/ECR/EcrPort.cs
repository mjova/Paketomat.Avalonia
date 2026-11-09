using System;
using System.Diagnostics;
using System.IO.Ports;

namespace ECR;

public class EcrPort
{
	public enum DataDirection
	{
		ECR2POS,
		POS2ECR
	}

	public delegate void EcrMessageHandler(string messageIdentifier, string message);

	public delegate void EcrExtendedMessageHandler(string messageIdentifier, string code, string message);

	public delegate void EcrAs24MessageHandler(string messageIdentifier, string track2data, string message, string bfoc);

	public delegate void EcrFunctionMessageHandler(string messageIdentifier, string code, string message);

	public delegate void RawdataHandler(DataDirection direction, byte[] rawdata);

	private static string m_DLLVersion = "2.21";

	private static string m_ProtocolVersion = "2.54";

	private static bool m_enableRealtimeRawdataDebug = false;

	private static string m_posMessageEncoding = Configuration.posMessageEncoding;

	private static int m_functionTimeout = Configuration.functionTimeout;

	private static int m_waitForAckTimeout = Configuration.waitForAckTimeout;

	private static string m_configIpAddress = Configuration.ipaddress;

	private static bool m_isBussy = false;

	public static string DLLVersion => m_DLLVersion;

	public static string ProtocolVersion => m_ProtocolVersion;

	public static bool EnableRealtimeRawdataDebug
	{
		get
		{
			return m_enableRealtimeRawdataDebug;
		}
		set
		{
			m_enableRealtimeRawdataDebug = value;
		}
	}

	public static string PosMessageEncoding
	{
		get
		{
			return m_posMessageEncoding;
		}
		set
		{
			Configuration.posMessageEncoding = value;
			m_posMessageEncoding = value;
		}
	}

	public static int FunctionTimeoutMillis
	{
		get
		{
			return m_functionTimeout;
		}
		set
		{
			Configuration.functionTimeout = value;
			m_functionTimeout = value;
		}
	}

	public static int WaitForAckTimeoutMillis
	{
		get
		{
			return m_waitForAckTimeout;
		}
		set
		{
			Configuration.waitForAckTimeout = value;
			m_waitForAckTimeout = value;
		}
	}

	public static string ConfigIpAddress => m_configIpAddress;

	public static bool IsBussy
	{
		get
		{
			return m_isBussy;
		}
		internal set
		{
			m_isBussy = value;
		}
	}

	public static event EcrMessageHandler EcrMessageReceived;

	public static event EcrExtendedMessageHandler EcrExtendedMessageReceived;

	public static event EcrAs24MessageHandler EcrAs24MessageReceived;

	public static event EcrFunctionMessageHandler EcrFunctionMessageReceived;

	public static event RawdataHandler RawdataEvent;

	private static void EcrConnectorMessageReceived(string messageIdentifier, string message)
	{
		EcrPort.EcrMessageReceived(messageIdentifier, message);
	}

	private static void EcrConnectorExtendedMessageReceived(string messageIdentifier, string code, string message)
	{
		EcrPort.EcrExtendedMessageReceived(messageIdentifier, code, message);
	}

	private static void EcrConnectorAs24MessageReceived(string messageIdentifier, string track2data, string message, string bfoc)
	{
		EcrPort.EcrAs24MessageReceived(messageIdentifier, track2data, message, bfoc);
	}

	private static void EcrConnectorFunctionMessageReceived(string messageIdentifier, string code, string message)
	{
		EcrPort.EcrFunctionMessageReceived(messageIdentifier, code, message);
	}

	private static void EcrConnectorRawdataEvent(DataDirection direction, byte[] rawdata)
	{
		EcrPort.RawdataEvent(direction, rawdata);
	}

	public static string[] getAvailableSerialPortsForThisComputer()
	{
		return SerialPort.GetPortNames();
	}

	public static void CancelTransaction()
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		if (EcrConnectorV3.HoldMessageCode == "54")
		{
			EcrConnectorV3.CancelTransactionFlag = 0;
		}
		else
		{
			EcrConnectorV3.CancelTransactionFlag = 1;
		}
	}

	public static int DoTransaction(ref string transactionType, ref int transactionAmount, ref int installments, ref string respCode, ref string pan, ref string invoiceNumber, ref string approvalCode, ref string date, ref string time, ref string issuerName, ref string terminalNo, ref string batchNumber, ref string transactionCount, ref string transactionTotal, ref string refundCount, ref string refundTotal, ref string activationCode)
	{
		try
		{
			return EcrPortV1.DoTransaction(ref transactionType, ref transactionAmount, ref installments, ref respCode, ref pan, ref invoiceNumber, ref approvalCode, ref date, ref time, ref issuerName, ref terminalNo, ref batchNumber, ref transactionCount, ref transactionTotal, ref refundCount, ref refundTotal, ref activationCode);
		}
		catch (Exception)
		{
			return 7;
		}
	}

	public static int DoTransactionV2(ref TransParams tp)
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		try
		{
			EcrConnectorV2 ecrConnectorV = new EcrConnectorV2();
			ecrConnectorV.EcrConnectorMessageReceived += EcrConnectorMessageReceived;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			return ecrConnectorV.DoTransaction(ref tp);
		}
		catch (Exception ex)
		{
			Console.WriteLine(ex.Message);
			return 7;
		}
	}

	public static int DoTransactionV2(ref TransParams tp, string portName, int baudRate)
	{
		try
		{
			EcrConnectorV2 ecrConnectorV = new EcrConnectorV2();
			ecrConnectorV.EcrConnectorMessageReceived += EcrConnectorMessageReceived;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			return ecrConnectorV.DoTransaction(ref tp, portName, baudRate);
		}
		catch (Exception ex)
		{
			Console.WriteLine(ex.Message);
			return 7;
		}
	}

	public static int DoTransactionV2(ref TransParams tp, string portName, int baudRate, int dataBits, Parity parity, StopBits stopBits)
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		try
		{
			EcrConnectorV2 ecrConnectorV = new EcrConnectorV2();
			ecrConnectorV.EcrConnectorMessageReceived += EcrConnectorMessageReceived;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			return ecrConnectorV.DoTransaction(ref tp, portName, baudRate, dataBits, parity, stopBits);
		}
		catch (Exception ex)
		{
			Console.WriteLine(ex.Message);
			return 7;
		}
	}

	public static int DoTransactionV2(string TerminalId, string SeqNumber, string TransactionType, string PrinterFlag, string CashierId, ref string TransactionNumber, ref int TransactionAmount1, string AmountCurrency, ref string AuthorizationCode, string InputLabel, string InsurancePolicyNumber, ref string InstallmentsNumber, ref string TransactionFlag, ref string BatchNumber, ref string TransactionDate, ref string TransactionTime, ref string CardDataSource, ref string CardNumber, ref string TidNumber, ref string MidNumber, ref string CompanyName, ref string EmvData, ref string SignatureLine, ref string AcquirerName, ref string DebitTransactionCount, ref string DebitTransactionAmount, ref string RefundTransactionCount, ref string RefundTransactionAmount, ref string MoreMessages, ref string FullResponseCode, ref string TransactionStatus, ref string SpdhTerminalTotals, ref string SpdhHostTotals, ref string PINBlock, ref string CardholderName)
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		TransParams tp = default(TransParams);
		tp.TerminalId = TerminalId;
		tp.SeqNumber = SeqNumber;
		tp.TransactionType = TransactionType;
		tp.PrinterFlag = PrinterFlag;
		tp.CashierId = CashierId;
		tp.TransactionNumber = TransactionNumber;
		tp.TransactionAmount1 = TransactionAmount1;
		tp.AmountCurrency = AmountCurrency;
		tp.AuthorizationCode = AuthorizationCode;
		tp.InputLabel = InputLabel;
		tp.InsurancePolicyNumber = InsurancePolicyNumber;
		tp.InstallmentsNumber = InstallmentsNumber;
		tp.TransactionFlag = TransactionFlag;
		tp.BatchNumber = BatchNumber;
		tp.TransactionDate = TransactionDate;
		tp.TransactionTime = TransactionTime;
		tp.CardDataSource = CardDataSource;
		tp.CardNumber = CardNumber;
		tp.TidNumber = TidNumber;
		tp.MidNumber = MidNumber;
		tp.CompanyName = CompanyName;
		tp.EmvData = EmvData;
		tp.SignatureLine = SignatureLine;
		tp.AcquirerName = AcquirerName;
		tp.DebitTransactionCount = DebitTransactionCount;
		tp.DebitTransactionAmount = DebitTransactionAmount;
		tp.RefundTransactionCount = RefundTransactionCount;
		tp.RefundTransactionAmount = RefundTransactionAmount;
		tp.MoreMessages = MoreMessages;
		tp.FullResponseCode = FullResponseCode;
		tp.TransactionStatus = TransactionStatus;
		tp.SpdhTerminalTotals = SpdhTerminalTotals;
		tp.SpdhHostTotals = SpdhHostTotals;
		tp.PINBlock = PINBlock;
		tp.CardholderName = CardholderName;
		try
		{
			EcrConnectorV2 ecrConnectorV = new EcrConnectorV2();
			ecrConnectorV.EcrConnectorMessageReceived += EcrConnectorMessageReceived;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			return ecrConnectorV.DoTransaction(ref tp);
		}
		catch (Exception)
		{
			return 7;
		}
	}

	public static int DoTransactionV3(ref TransParamsV3 tp)
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		int num = 7;
		IsBussy = true;
		try
		{
			EcrConnectorV3 ecrConnectorV = new EcrConnectorV3();
			ecrConnectorV.EcrConnectorMessageReceived += EcrConnectorMessageReceived;
			ecrConnectorV.EcrConnectorExtendedMessageReceived += EcrConnectorExtendedMessageReceived;
			ecrConnectorV.EcrConnectorAs24MessageReceived += EcrPort.EcrAs24MessageReceived.Invoke;
			if (EnableRealtimeRawdataDebug)
			{
				ecrConnectorV.EcrConnectorRawdataEvent += EcrConnectorRawdataEvent;
			}
			num = ecrConnectorV.DoTransaction(ref tp);
		}
		catch (Exception ex)
		{
			Console.WriteLine(ex.Message);
			EcrLog.WriteException("POS Error COM :", ex);
			num = 7;
		}
		IsBussy = false;
		EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		return num;
	}

	public static int DoTransactionV3(ref TransParamsV3 tp, string portName, int baudRate)
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		int num = 7;
		IsBussy = true;
		try
		{
			EcrConnectorV3 ecrConnectorV = new EcrConnectorV3();
			ecrConnectorV.EcrConnectorMessageReceived += EcrConnectorMessageReceived;
			ecrConnectorV.EcrConnectorExtendedMessageReceived += EcrConnectorExtendedMessageReceived;
			ecrConnectorV.EcrConnectorAs24MessageReceived += EcrPort.EcrAs24MessageReceived.Invoke;
			if (EnableRealtimeRawdataDebug)
			{
				ecrConnectorV.EcrConnectorRawdataEvent += EcrConnectorRawdataEvent;
			}
			num = ecrConnectorV.DoTransaction(ref tp, portName, baudRate);
		}
		catch (Exception ex)
		{
			Console.WriteLine(ex.Message);
			EcrLog.WriteException("POS Error COM :", ex);
			num = 7;
		}
		IsBussy = false;
		EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		return num;
	}

	public static int DoTransactionV3(ref TransParamsV3 tp, string portName, int baudRate, int dataBits, Parity parity, StopBits stopBits)
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		int num = 7;
		IsBussy = true;
		try
		{
			EcrConnectorV3 ecrConnectorV = new EcrConnectorV3();
			ecrConnectorV.EcrConnectorMessageReceived += EcrConnectorMessageReceived;
			ecrConnectorV.EcrConnectorExtendedMessageReceived += EcrConnectorExtendedMessageReceived;
			ecrConnectorV.EcrConnectorAs24MessageReceived += EcrPort.EcrAs24MessageReceived.Invoke;
			if (EnableRealtimeRawdataDebug)
			{
				ecrConnectorV.EcrConnectorRawdataEvent += EcrConnectorRawdataEvent;
			}
			num = ecrConnectorV.DoTransaction(ref tp, portName, baudRate, dataBits, parity, stopBits);
		}
		catch (Exception ex)
		{
			Console.WriteLine(ex.Message);
			EcrLog.WriteException("POS Error COM :", ex);
			num = 7;
		}
		IsBussy = false;
		EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		return num;
	}

	public static int DoTransactionTcp(ref TransParamsV3 tp)
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		int num = 7;
		IsBussy = true;
		try
		{
			EcrConnectorV3 ecrConnectorV = new EcrConnectorV3();
			ecrConnectorV.EcrConnectorMessageReceived += EcrConnectorMessageReceived;
			ecrConnectorV.EcrConnectorExtendedMessageReceived += EcrConnectorExtendedMessageReceived;
			ecrConnectorV.EcrConnectorAs24MessageReceived += EcrPort.EcrAs24MessageReceived.Invoke;
			if (EnableRealtimeRawdataDebug)
			{
				ecrConnectorV.EcrConnectorRawdataEvent += EcrConnectorRawdataEvent;
			}
			num = ecrConnectorV.DoTransactionTcp(ref tp);
		}
		catch (Exception ex)
		{
			Console.WriteLine(ex.Message);
			EcrLog.WriteException("POS Error ETH :", ex);
			return 7;
		}
		IsBussy = false;
		EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		return num;
	}

	public static int DoTransactionTcp(ref TransParamsV3 tp, string ipaddress)
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		int num = 7;
		IsBussy = true;
		try
		{
			EcrConnectorV3 ecrConnectorV = new EcrConnectorV3();
			ecrConnectorV.EcrConnectorMessageReceived += EcrConnectorMessageReceived;
			ecrConnectorV.EcrConnectorExtendedMessageReceived += EcrConnectorExtendedMessageReceived;
			ecrConnectorV.EcrConnectorAs24MessageReceived += EcrPort.EcrAs24MessageReceived.Invoke;
			if (EnableRealtimeRawdataDebug)
			{
				ecrConnectorV.EcrConnectorRawdataEvent += EcrConnectorRawdataEvent;
			}
			num = ecrConnectorV.DoTransactionTcp(ref tp, ipaddress);
		}
		catch (Exception e)
		{
			EcrLog.WriteException("POS Error ETH :", e);
			num = 7;
		}
		IsBussy = false;
		EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		return num;
	}
}
