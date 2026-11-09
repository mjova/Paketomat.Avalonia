using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Timers;
using System.Xml;
using ECR.V1;

namespace ECR;

public static class EcrPortV1
{
	private static SerialPort SP;

	private static EcrMessage ReceivedEcrMessage;

	private static System.Timers.Timer T1;

	private static System.Timers.Timer T2;

	public static bool ComPortIsOpen;

	private static bool AckReceived;

	private static bool NackReceived;

	private static bool AckTimeOut;

	private static bool FuncTimeOut;

	private static bool invalidInput;

	private static bool configFound;

	private static string prodKey;

	private static string portName;

	private static string baudRate;

	private static string dataBits;

	private static string stopBits;

	private static string parity;

	private static string retryTimeout;

	private static string functionTimeout;

	private static string enableLogging;

	private static string detailedLogging;

	private static string sendRespMessage13;

	private static bool enableLoggingBool;

	private static bool detailedLoggingBool;

	private static bool sendRespMessage13Bool;

	static EcrPortV1()
	{
		SP = new SerialPort();
		ReceivedEcrMessage = null;
		T1 = new System.Timers.Timer();
		T2 = new System.Timers.Timer();
		ComPortIsOpen = false;
		AckReceived = false;
		NackReceived = false;
		AckTimeOut = false;
		FuncTimeOut = false;
		invalidInput = false;
		configFound = false;
		prodKey = "";
		portName = "";
		baudRate = "";
		dataBits = "";
		stopBits = "";
		parity = "";
		retryTimeout = "";
		functionTimeout = "";
		enableLogging = "";
		detailedLogging = "";
		sendRespMessage13 = "";
		enableLoggingBool = false;
		detailedLoggingBool = false;
		sendRespMessage13Bool = false;
		if (Convert.ToDouble(EcrPort.DLLVersion, CultureInfo.InvariantCulture) <= 2.1)
		{
			configFound = readConfig("ecr.cfg");
		}
		else
		{
			configFound = readXMLConfig("ecrcfg.xml");
		}
		if (configFound)
		{
			initEcrPort();
		}
	}

	private static void initEcrPort()
	{
		EcrMessage.InvalidInputData += InvalidInputDataHandler;
		int result = 0;
		int.TryParse(retryTimeout, out result);
		if (result > 0)
		{
			T1.Interval = result;
		}
		T1.AutoReset = false;
		T1.Elapsed += RetryTimerEvent;
		int result2 = 0;
		int.TryParse(functionTimeout, out result2);
		if (result2 > 0)
		{
			T2.Interval = result2;
		}
		T2.AutoReset = false;
		T2.Elapsed += TimeoutTimerEvent;
		SP.PortName = portName;
		int result3 = 0;
		int.TryParse(baudRate, out result3);
		if (result3 > 0)
		{
			SP.BaudRate = result3;
		}
		int result4 = 0;
		int.TryParse(dataBits, out result4);
		if (result4 == 7 || result4 == 8)
		{
			SP.DataBits = result4;
		}
		try
		{
			SP.StopBits = (StopBits)Enum.Parse(typeof(StopBits), stopBits);
		}
		catch
		{
		}
		try
		{
			SP.Parity = (Parity)Enum.Parse(typeof(Parity), parity);
		}
		catch
		{
		}
		SP.ReadTimeout = 500;
		SP.NewLine = Convert.ToChar(3).ToString();
		SP.DataReceived += MessageReceived;
	}

	private static void InvalidInputDataHandler()
	{
		invalidInput = true;
	}

	private static void MessageReceived(object sender, SerialDataReceivedEventArgs e)
	{
		int num = 0;
		int num2 = 0;
		string text = "";
		byte[] buffer = new byte[1] { 6 };
		byte[] buffer2 = new byte[1] { 21 };
		switch (SP.ReadByte())
		{
		case 6:
			if (enableLoggingBool)
			{
				EcrLog.WriteAckNack("6", directionSending: false, LogMessageType.ACK, detailedLoggingBool, "POS2ECR");
			}
			AckReceived = true;
			break;
		case 21:
			if (enableLoggingBool)
			{
				EcrLog.WriteAckNack("15", directionSending: false, LogMessageType.NAK, detailedLoggingBool, "POS2ECR");
			}
			NackReceived = true;
			break;
		case 2:
			if (enableLoggingBool && detailedLoggingBool)
			{
				StringBuilder stringBuilder = new StringBuilder();
				int num3 = 0;
				int[] array = new int[512];
				DateTime now = DateTime.Now;
				Stopwatch stopwatch = new Stopwatch();
				stopwatch.Start();
				array[0] = 0;
				int num4 = 0;
				for (int i = 1; i < 512; i++)
				{
					num3 = SP.ReadByte();
					array[i] = stopwatch.Elapsed.Milliseconds;
					num4 = i;
					if (num3 == 3)
					{
						break;
					}
					stringBuilder = stringBuilder.Append((char)num3);
				}
				text = stringBuilder.ToString();
				num2 = SP.ReadByte();
				array[num4 + 1] = stopwatch.Elapsed.Milliseconds;
				string text2 = text.Insert(0, '\u0002'.ToString());
				text2 = text2.Insert(text2.Length, '\u0003'.ToString());
				text2 = text2.Insert(text2.Length, ((char)num2).ToString());
				EcrLog.WriteDetailed(text2.ToString(), directionSending: false, LogMessageType.MSG, now, array, "POS2ECR");
			}
			else
			{
				text = SP.ReadLine();
				num2 = SP.ReadByte();
				if (enableLoggingBool)
				{
					EcrLog.Write(text, directionSending: false, LogMessageType.MSG, num2);
				}
			}
			if (num2 == CalcLRC(text))
			{
				ReceivedEcrMessage = EcrMessage.Parse(text);
				SP.Write(buffer, 0, 1);
				if (enableLoggingBool)
				{
					EcrLog.WriteAckNack("6", directionSending: true, LogMessageType.ACK, detailedLoggingBool, "POS2ECR");
				}
			}
			else
			{
				SP.Write(buffer2, 0, 1);
				if (enableLoggingBool)
				{
					EcrLog.WriteAckNack("15", directionSending: true, LogMessageType.NAK, detailedLoggingBool, "POS2ECR");
				}
			}
			break;
		}
	}

	private static void RetryTimerEvent(object source, ElapsedEventArgs e)
	{
		AckTimeOut = true;
	}

	private static void TimeoutTimerEvent(object source, ElapsedEventArgs e)
	{
		FuncTimeOut = true;
	}

	internal static int DoTransaction(ref string transactionType, ref int transactionAmount, ref int installments, ref string respCode, ref string pan, ref string invoiceNumber, ref string approvalCode, ref string date, ref string time, ref string issuerName, ref string terminalNo, ref string batchNumber, ref string transactionCount, ref string transactionTotal, ref string refundCount, ref string refundTotal, ref string activationCode)
	{
		if (!configFound)
		{
			return 5;
		}
		if (ComPortOpen() == 1)
		{
			return 1;
		}
		int num = 0;
		ReceivedEcrMessage = null;
		FuncTimeOut = false;
		T2.Enabled = false;
		T2.Enabled = true;
		TransactionType ttype;
		try
		{
			ttype = (TransactionType)Enum.Parse(typeof(TransactionType), transactionType);
		}
		catch
		{
			return 4;
		}
		EcrMessage ecrMessage = new EcrMessage(ttype, MessageType.InitiateTransaction);
		ecrMessage.FldTransactionAmount1 = transactionAmount;
		ecrMessage.FldInstallments = installments;
		ecrMessage.FldInvoiceNumber = invoiceNumber;
		ecrMessage.FldActivationCode = activationCode;
		invoiceNumber = "";
		if (invalidInput)
		{
			invalidInput = false;
			return 4;
		}
		num = SendMessage(ecrMessage);
		if (num != 0)
		{
			return num;
		}
		while (ReceivedEcrMessage == null && !FuncTimeOut)
		{
		}
		if (FuncTimeOut)
		{
			FuncTimeOut = false;
			EcrLog.WriteReturn("3 - Failure (Function TimeOut)", detailedLoggingBool);
			return 3;
		}
		EcrMessage receivedEcrMessage = ReceivedEcrMessage;
		ReceivedEcrMessage = null;
		while (ReceivedEcrMessage == null && !FuncTimeOut)
		{
		}
		if (FuncTimeOut)
		{
			FuncTimeOut = false;
			EcrLog.WriteReturn("3 - Failure (Function TimeOut)", detailedLoggingBool);
			return 3;
		}
		EcrMessage receivedEcrMessage2 = ReceivedEcrMessage;
		ReceivedEcrMessage = null;
		transactionAmount = receivedEcrMessage2.FldTransactionAmount1;
		installments = receivedEcrMessage2.FldInstallments;
		respCode = receivedEcrMessage2.FldRespCode;
		invoiceNumber = receivedEcrMessage2.FldInvoiceNumber;
		pan = receivedEcrMessage2.FldPan;
		approvalCode = receivedEcrMessage2.FldApprovalCode;
		date = receivedEcrMessage2.FldDate;
		time = receivedEcrMessage2.FldTime;
		issuerName = receivedEcrMessage2.FldIssuerName;
		terminalNo = receivedEcrMessage2.FldTerminalNo;
		batchNumber = receivedEcrMessage2.FldBatchNumber;
		transactionCount = receivedEcrMessage2.FldTransactionCount;
		transactionTotal = receivedEcrMessage2.FldTransactionTotal;
		refundCount = receivedEcrMessage2.FldRefundCount;
		refundTotal = receivedEcrMessage2.FldRefundTotal;
		if (sendRespMessage13Bool)
		{
			EcrMessage msg = new EcrMessage(ttype, MessageType.AuthorizationResultResp);
			num = SendMessage(msg);
			if (num != 0)
			{
				return num;
			}
		}
		else
		{
			Thread.Sleep(10);
		}
		if (enableLoggingBool)
		{
			EcrLog.WriteEnd(detailedLoggingBool);
		}
		ComPortClose();
		return 0;
	}

	private static int ComPortOpen()
	{
		try
		{
			SP.Close();
			SP.Open();
		}
		catch
		{
			return 1;
		}
		ComPortIsOpen = true;
		return 0;
	}

	private static int ComPortClose()
	{
		try
		{
			SP.Close();
		}
		catch
		{
			return 1;
		}
		ComPortIsOpen = false;
		return 0;
	}

	private static int ComPortWrite(string MsgString)
	{
		for (int i = 0; i < 3; i++)
		{
			ResetAckFlags();
			try
			{
				if (enableLoggingBool && detailedLoggingBool)
				{
					char[] array = new char[1];
					int length = MsgString.Length;
					int[] array2 = new int[length];
					DateTime now = DateTime.Now;
					Stopwatch stopwatch = new Stopwatch();
					stopwatch.Start();
					int num = 0;
					foreach (char c in MsgString)
					{
						array[0] = c;
						SP.Write(array, 0, 1);
						array2[num] = stopwatch.Elapsed.Milliseconds;
						num++;
					}
					EcrLog.WriteDetailed(MsgString, directionSending: true, LogMessageType.MSG, now, array2, "ECR2POS");
				}
				else
				{
					SP.Write(MsgString);
					if (enableLoggingBool)
					{
						EcrLog.Write(MsgString, directionSending: true, LogMessageType.MSG, 0);
					}
				}
			}
			catch
			{
				return 1;
			}
			T1.Enabled = true;
			while (!AckReceived && !NackReceived && !AckTimeOut)
			{
			}
			T1.Enabled = false;
			if (AckReceived)
			{
				return 0;
			}
		}
		EcrLog.WriteReturn("2 - Failure (No ACK or NACK)", detailedLoggingBool);
		return 2;
	}

	private static int CalcLRC(string receivedMessage)
	{
		int num = 3;
		foreach (char c in receivedMessage)
		{
			num ^= c;
		}
		return num;
	}

	private static void ResetAckFlags()
	{
		AckReceived = false;
		NackReceived = false;
		AckTimeOut = false;
	}

	private static int SendMessage(EcrMessage Msg)
	{
		string text = Msg.FldMessageType switch
		{
			MessageType.InitiateTransaction => ComposeTransactionReq(Msg), 
			MessageType.AuthorizationResultResp => ComposeAuthorizationAns(Msg), 
			_ => "", 
		};
		int value = CalcLRC(text);
		text = text.Insert(0, Convert.ToChar(2).ToString());
		text = text.Insert(text.Length, Convert.ToChar(3).ToString());
		text = text.Insert(text.Length, Convert.ToChar(value).ToString());
		return ComPortWrite(text);
	}

	private static string ComposeTransactionReq(EcrMessage Msg)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append(TypeField(Msg.FldTransactionType, Msg.FldMessageType));
		stringBuilder.Append(NumField(Msg.FldEcrNumber, 0, feeldSep: true));
		stringBuilder.Append(NumField(Msg.FldEcrReceiptNumber, 0, feeldSep: true));
		stringBuilder.Append(NumField(Msg.FldTransactionAmount1, 12, feeldSep: true));
		stringBuilder.Append(NumField(Msg.FldTransactionAmount2, 12, feeldSep: true));
		stringBuilder.Append(AlphaField(Msg.FldProductCodes, feeldSep: true));
		stringBuilder.Append(AlphaField(Msg.FldApprovalCode, feeldSep: true));
		stringBuilder.Append(AlphaField(Msg.FldInvoiceNumber, feeldSep: true));
		stringBuilder.Append(NumField(Msg.FldCashierId, 4, feeldSep: false));
		stringBuilder.Append(AlphaField(Msg.FldDocumentId, feeldSep: true));
		stringBuilder.Append(AlphaField(Msg.FldInputLabel, feeldSep: true));
		if (Msg.FldInstallments > 0)
		{
			stringBuilder.Append(NumField(Msg.FldInstallments, 2, feeldSep: true));
		}
		else
		{
			stringBuilder.Append(AlphaField("", feeldSep: true));
		}
		stringBuilder.Append(AlphaField(Msg.FldInsurancePolicy, feeldSep: true));
		stringBuilder.Append(AlphaField(Msg.FldActivationCode, feeldSep: true));
		return stringBuilder.ToString();
	}

	private static string ComposeAuthorizationAns(EcrMessage Msg)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append(TypeField(Msg.FldTransactionType, Msg.FldMessageType));
		return stringBuilder.ToString();
	}

	private static string TypeField(TransactionType Ttype, MessageType Mtype)
	{
		if (Ttype != 0 && Mtype != 0)
		{
			string text = Ttype.ToString();
			int num = (int)Mtype;
			return text + num + ".";
		}
		return ".";
	}

	private static string NumField(int val, int decPlc, bool feeldSep)
	{
		string text = "";
		if (feeldSep)
		{
			text = '\u001c'.ToString();
		}
		if (val == -1)
		{
			return text;
		}
		if (decPlc > 0)
		{
			StringBuilder stringBuilder = new StringBuilder();
			int num = decPlc - val.ToString().Length;
			for (int i = 0; i < num; i++)
			{
				stringBuilder.Append('0');
			}
			return stringBuilder.ToString() + val + text;
		}
		return val + text;
	}

	private static string AlphaField(string val, bool feeldSep)
	{
		string text = "";
		if (feeldSep)
		{
			text = '\u001c'.ToString();
		}
		if (val.Length == 0)
		{
			return text;
		}
		return val + text;
	}

	private static bool readConfig(string cfgFileName)
	{
		if (File.Exists(cfgFileName))
		{
			using (StreamReader streamReader = new StreamReader(cfgFileName))
			{
				string text;
				while ((text = streamReader.ReadLine()) != null)
				{
					if (!text.StartsWith(";"))
					{
						processLine(text, "Prod_keyE", ref prodKey);
						processLine(text, "Port_name", ref portName);
						processLine(text, "Baud_rate", ref baudRate);
						processLine(text, "Data_bits", ref dataBits);
						processLine(text, "Stop_bits", ref stopBits);
						processLine(text, "Parity", ref parity);
						processLine(text, "Retry_timeout", ref retryTimeout);
						processLine(text, "Function_timeout", ref functionTimeout);
						processLine(text, "Enable_logging", ref enableLogging);
						processLine(text, "Detailed_logging", ref detailedLogging);
						processLine(text, "Send_RespMessage13", ref sendRespMessage13);
					}
				}
			}
			if (enableLogging == "1" || enableLogging == "true" || enableLogging == "True" || enableLogging == "TRUE")
			{
				enableLoggingBool = true;
			}
			else
			{
				enableLoggingBool = false;
			}
			if (detailedLogging == "1" || detailedLogging == "true" || detailedLogging == "True" || detailedLogging == "TRUE")
			{
				detailedLoggingBool = true;
			}
			else
			{
				detailedLoggingBool = false;
			}
			if (sendRespMessage13 == "1" || sendRespMessage13 == "true" || sendRespMessage13 == "True" || sendRespMessage13 == "TRUE")
			{
				sendRespMessage13Bool = true;
			}
			else
			{
				sendRespMessage13Bool = false;
			}
			return true;
		}
		return false;
	}

	private static bool readXMLConfig(string cfgFileName)
	{
		if (File.Exists(cfgFileName))
		{
			prodKey = readDataFromXML(cfgFileName, "/Configuration/ProductKey/Prod_keyE");
			portName = readDataFromXML(cfgFileName, "/Configuration/COMport/Port_name");
			baudRate = readDataFromXML(cfgFileName, "/Configuration/COMport/Baud_rate");
			dataBits = readDataFromXML(cfgFileName, "/Configuration/COMport/Data_bits");
			stopBits = readDataFromXML(cfgFileName, "/Configuration/COMport/Stop_bits");
			parity = readDataFromXML(cfgFileName, "/Configuration/COMport/Parity");
			retryTimeout = readDataFromXML(cfgFileName, "/Configuration/TimeOut/Retry_timeout");
			functionTimeout = readDataFromXML(cfgFileName, "/Configuration/TimeOut/Function_timeout");
			enableLogging = readDataFromXML(cfgFileName, "/Configuration/Log/Enable_logging");
			detailedLogging = readDataFromXML(cfgFileName, "/Configuration/Log/Detailed_logging");
			sendRespMessage13 = readDataFromXML(cfgFileName, "/Configuration/ProtocolV1/Send_RespMessage13");
			if (enableLogging == "1" || enableLogging == "true" || enableLogging == "True" || enableLogging == "TRUE")
			{
				enableLoggingBool = true;
			}
			else
			{
				enableLoggingBool = false;
			}
			if (detailedLogging == "1" || detailedLogging == "true" || detailedLogging == "True" || detailedLogging == "TRUE")
			{
				detailedLoggingBool = true;
			}
			else
			{
				detailedLoggingBool = false;
			}
			if (sendRespMessage13 == "1" || sendRespMessage13 == "true" || sendRespMessage13 == "True" || sendRespMessage13 == "TRUE")
			{
				sendRespMessage13Bool = true;
			}
			else
			{
				sendRespMessage13Bool = false;
			}
			return true;
		}
		return false;
	}

	private static int processLine(string line, string propertyName, ref string value)
	{
		int length = propertyName.Length;
		if (line.StartsWith(propertyName))
		{
			line = line.Remove(0, length);
			line = line.Replace('=', ' ');
			int num = line.IndexOf(';');
			length = line.Length;
			if (num != -1)
			{
				line = line.Remove(num, length - num);
			}
			line = line.Trim();
			line = line.Replace("\\n", "\n");
			value = line;
			return 0;
		}
		return 1;
	}

	private static string readDataFromXML(string filename, string singleNode)
	{
		try
		{
			XmlDocument xmlDocument = new XmlDocument();
			xmlDocument.Load(filename);
			XmlNode xmlNode = xmlDocument.DocumentElement.SelectSingleNode(singleNode);
			return xmlNode.InnerText;
		}
		catch (Exception)
		{
			return "";
		}
	}
}
