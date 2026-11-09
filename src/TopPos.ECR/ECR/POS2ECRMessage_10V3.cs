using System;
using System.Collections;

namespace ECR;

internal class POS2ECRMessage_10V3 : POS2ECRMessageV3
{
	private ArrayList al = new ArrayList();

	private bool valid = false;

	private int SizeOfIncomingMessage = 0;

	private char[] receivedChars;

	public string identifier;

	public string terminalId;

	public string sourceId;

	public string sequentialNumber;

	public string transactionType;

	public string transactionFlag;

	public string transactionNumber;

	public string batchNumber;

	public string transactionDate;

	public string transactionTime;

	public string transactionAmount1;

	public string amountExponent;

	public string amountCurrency;

	public string cardDataSource;

	public string cardNumber;

	public string expirationDate;

	public string authorizationCode;

	public string tidNumber;

	public string midNumber;

	public string companyName;

	public string displayMessage;

	public string inputData;

	public string emvData;

	public string signatureLinePrintingFlag;

	public string acquirerName;

	public string debitTransactionCount;

	public string debitTransactionAmount;

	public string refundTransactionCount;

	public string refundTransactionAmount;

	public string moreMessagesFlag;

	public string installmentsNumber;

	public string fullResponseCode;

	public string transactionStatus;

	public string spdhTerminalTotals;

	public string spdhHostTotals;

	public string pinBlock;

	public string cardHolderName;

	public string retrievalReferenceNumber;

	public string PinFlag;

	public string transactionAmount2;

	public string payServices;

	public string balanceInquiry;

	public string offlineCryptogram;

	public string loyaltyData;

	public string formattedTextToPrint;

	public string dccNumber;

	public string dccProvider;

	public string dccExchangeRate;

	public string dccExchangeRateDate;

	public string dccMarkUpPercent;

	public string dccAmount;

	public string dccDisclaimer;

	public string dccStatus;

	public string dccCurrency;

	public string ipReferenceNumber;

	public string PersonalVehicleCardData;

	public string mifareCardType;

	public string mifareCardUID;

	public string F4Gcode;

	public string radcomSpecificData;

	public string AcquirerID;

	public new static POS2ECRMessage_10V3 create(char[] received)
	{
		POS2ECRMessage_10V3 pOS2ECRMessage_10V = new POS2ECRMessage_10V3();
		pOS2ECRMessage_10V.receivedChars = received;
		pOS2ECRMessage_10V.fill(received);
		char c = received[received.Length - 1];
		char[] array = new char[received.Length - 1];
		Array.Copy(received, 1, array, 0, received.Length - 1);
		char c2 = LRC.calculate(array);
		pOS2ECRMessage_10V.valid = true;
		return pOS2ECRMessage_10V;
	}

	private void fill(char[] received)
	{
		char[] separator = new char[4]
		{
			COMChars.EOT,
			COMChars.ETX,
			COMChars.STX,
			COMChars.FS
		};
		string text = new string(received);
		string[] array = text.Split(separator);
		SizeOfIncomingMessage = array.Length;
		identifier = array[1].Substring(0, 2);
		terminalId = array[1].Substring(2, 2);
		sourceId = array[1].Substring(4, 2);
		sequentialNumber = array[1].Substring(6, 4);
		transactionType = array[1].Substring(10, 2);
		transactionFlag = array[1].Substring(12, 2);
		transactionNumber = array[1].Substring(14, 6);
		batchNumber = array[1].Substring(20, 4);
		transactionDate = array[1].Substring(24, 6);
		transactionTime = array[1].Substring(30, 6);
		transactionAmount1 = array[2];
		amountExponent = array[4];
		amountCurrency = array[5];
		cardDataSource = array[6];
		cardNumber = array[7];
		expirationDate = array[8];
		authorizationCode = array[11];
		tidNumber = array[12];
		midNumber = array[13];
		companyName = array[14];
		displayMessage = array[21];
		inputData = array[14];
		emvData = array[24];
		signatureLinePrintingFlag = array[25];
		if (array[26].Length == 42)
		{
			acquirerName = array[26].Substring(0, 10);
			debitTransactionCount = array[26].Substring(10, 4);
			debitTransactionAmount = array[26].Substring(14, 12);
			refundTransactionCount = array[26].Substring(26, 4);
			refundTransactionAmount = array[26].Substring(30, 12);
		}
		if (array[26].Length == 32)
		{
			acquirerName = array[14];
			debitTransactionCount = array[26].Substring(0, 4);
			debitTransactionAmount = array[26].Substring(4, 12);
			refundTransactionCount = array[26].Substring(16, 4);
			refundTransactionAmount = array[26].Substring(20, 12);
		}
		moreMessagesFlag = ReturnFieldValue(27, array);
		installmentsNumber = ReturnFieldValue(28, array);
		fullResponseCode = ReturnFieldValue(29, array);
		transactionStatus = ReturnFieldValue(30, array);
		spdhTerminalTotals = ReturnFieldValue(31, array);
		spdhHostTotals = ReturnFieldValue(32, array);
		pinBlock = ReturnFieldValue(33, array);
		cardHolderName = ReturnFieldValue(34, array);
		retrievalReferenceNumber = ReturnFieldValue(35, array);
		PinFlag = ReturnFieldValue(36, array);
		transactionAmount2 = ReturnFieldValue(37, array);
		payServices = ReturnFieldValue(38, array);
		balanceInquiry = ReturnFieldValue(39, array);
		offlineCryptogram = ReturnFieldValue(40, array);
		loyaltyData = ReturnFieldValue(41, array);
		formattedTextToPrint = ReturnFieldValue(42, array);
		dccNumber = ReturnFieldValue(43, array);
		dccProvider = ReturnFieldValue(44, array);
		dccExchangeRate = ReturnFieldValue(45, array);
		dccExchangeRateDate = ReturnFieldValue(46, array);
		dccMarkUpPercent = ReturnFieldValue(47, array);
		dccAmount = ReturnFieldValue(48, array);
		dccDisclaimer = ReturnFieldValue(49, array);
		dccStatus = ReturnFieldValue(50, array);
		dccCurrency = ReturnFieldValue(51, array);
		ipReferenceNumber = ReturnFieldValue(52, array);
		PersonalVehicleCardData = ReturnFieldValue(53, array);
		mifareCardType = ReturnFieldValue(54, array);
		mifareCardUID = ReturnFieldValue(55, array);
		F4Gcode = ReturnFieldValue(56, array);
		radcomSpecificData = ReturnFieldValue(57, array);
		AcquirerID = ReturnFieldValue(58, array);
	}

	public string ReturnFieldValue(int valueIndex, string[] arrayOfStrings)
	{
		if (valueIndex > SizeOfIncomingMessage - 2)
		{
			return string.Empty;
		}
		return arrayOfStrings[valueIndex];
	}

	public override bool isValid()
	{
		return valid;
	}

	public override bool isHold()
	{
		return false;
	}

	public override bool isError()
	{
		return false;
	}

	public void updateTransactionParams(ref TransParamsV3 tp)
	{
		tp.TransactionNumber = transactionNumber;
		if (!string.IsNullOrEmpty(transactionAmount1))
		{
			tp.TransactionAmount1 = long.Parse(transactionAmount1);
		}
		tp.AuthorizationCode = authorizationCode;
		tp.InstallmentsNumber = installmentsNumber;
		tp.TransactionFlag = transactionFlag;
		tp.BatchNumber = batchNumber;
		tp.TransactionDate = transactionDate;
		tp.TransactionTime = transactionTime;
		tp.CardDataSource = cardDataSource;
		tp.CardNumber = cardNumber;
		tp.TidNumber = tidNumber;
		tp.MidNumber = midNumber;
		tp.CompanyName = companyName;
		tp.displayMessage = displayMessage;
		tp.EmvData = emvData;
		tp.SignatureLine = signatureLinePrintingFlag;
		tp.FullResponseCode = fullResponseCode;
		tp.TransactionStatus = transactionStatus;
		tp.PINBlock = pinBlock;
		tp.CardholderName = cardHolderName;
		tp.retrievalReferenceNumber = retrievalReferenceNumber;
		tp.PinFlag = PinFlag;
		if (!string.IsNullOrEmpty(transactionAmount2))
		{
			tp.CashAmount = int.Parse(transactionAmount2);
		}
		tp.PayServices = payServices;
		tp.BalanceInquiry = balanceInquiry;
		tp.offlineCryptogram = offlineCryptogram;
		tp.LoyaltyData = loyaltyData;
		tp.FormattedTextToPrint = formattedTextToPrint;
		tp.dccStatus = dccStatus;
		addDccData(ref tp);
		tp.IPReferenceNumber = ipReferenceNumber;
		tp.PersonalVehicleCardData = PersonalVehicleCardData;
	}

	public void addTotal(ref TransParamsV3 tp)
	{
		TotalV3 totalV = new TotalV3();
		totalV.AcquirerName = acquirerName;
		totalV.DebitTransactionCount = debitTransactionCount;
		totalV.DebitTransactionAmount = debitTransactionAmount;
		totalV.RefundTransactionCount = refundTransactionCount;
		totalV.RefundTransactionAmount = refundTransactionAmount;
		totalV.BatchNumber = batchNumber;
		totalV.SpdhTerminalTotals = spdhTerminalTotals;
		totalV.SpdhHostTotals = spdhHostTotals;
		totalV.MoreMessages = moreMessagesFlag;
		tp.addTotalItem(totalV);
	}

	public void addTotalNew(ref TransParamsV3 tp)
	{
		if (AcquirerID == "")
		{
			TotalV3New totalV3New = new TotalV3New();
			totalV3New.AcquirerIssuerName = acquirerName;
			totalV3New.DebitTransactionCount = debitTransactionCount;
			totalV3New.DebitTransactionAmount = debitTransactionAmount;
			totalV3New.RefundTransactionCount = refundTransactionCount;
			totalV3New.RefundTransactionAmount = refundTransactionAmount;
			totalV3New.BatchNumber = batchNumber;
			totalV3New.SpdhTerminalTotals = spdhTerminalTotals;
			totalV3New.SpdhHostTotals = spdhHostTotals;
			totalV3New.MoreMessages = moreMessagesFlag;
			totalV3New.HostID = tidNumber;
			totalV3New.AcquirerID = AcquirerID;
			tp.addTotalNewItem(totalV3New);
		}
	}

	public TotalV3New getCurrentTotalNew()
	{
		TotalV3New totalV3New = new TotalV3New();
		totalV3New.AcquirerIssuerName = acquirerName;
		totalV3New.DebitTransactionCount = debitTransactionCount;
		totalV3New.DebitTransactionAmount = debitTransactionAmount;
		totalV3New.RefundTransactionCount = refundTransactionCount;
		totalV3New.RefundTransactionAmount = refundTransactionAmount;
		totalV3New.BatchNumber = batchNumber;
		totalV3New.SpdhTerminalTotals = spdhTerminalTotals;
		totalV3New.SpdhHostTotals = spdhHostTotals;
		totalV3New.MoreMessages = moreMessagesFlag;
		totalV3New.HostID = tidNumber;
		totalV3New.AcquirerID = AcquirerID;
		return totalV3New;
	}

	public TotalV3 getCurrentTotal()
	{
		TotalV3 totalV = new TotalV3();
		totalV.AcquirerName = acquirerName;
		totalV.DebitTransactionCount = debitTransactionCount;
		totalV.DebitTransactionAmount = debitTransactionAmount;
		totalV.RefundTransactionCount = refundTransactionCount;
		totalV.RefundTransactionAmount = refundTransactionAmount;
		totalV.BatchNumber = batchNumber;
		totalV.SpdhTerminalTotals = spdhTerminalTotals;
		totalV.SpdhHostTotals = spdhHostTotals;
		totalV.MoreMessages = moreMessagesFlag;
		return totalV;
	}

	public void addTransDet(ref TransParamsV3 tp)
	{
		TransDetV3 transDetV = new TransDetV3();
		transDetV.InvoiceNumber = transactionNumber;
		transDetV.DateTime = transactionDate + transactionTime;
		transDetV.TransactionName = transactionType;
		transDetV.Pan = cardNumber;
		transDetV.CardName = companyName;
		transDetV.ResponseCode = fullResponseCode;
		transDetV.ApprovalCode = authorizationCode;
		transDetV.TransactionAmount = transactionAmount1;
		transDetV.CashAmount = transactionAmount2;
		transDetV.AmountCurrency = amountCurrency;
		transDetV.InstallmentsNumber = installmentsNumber;
		transDetV.CardDataSource = cardDataSource;
		transDetV.EMVData = emvData;
		transDetV.SignatureLine = signatureLinePrintingFlag;
		transDetV.RRN = retrievalReferenceNumber;
		transDetV.PinFlag = PinFlag;
		tp.addTransItem(transDetV);
	}

	public TransDetV3 getCurrentTrans()
	{
		TransDetV3 transDetV = new TransDetV3();
		transDetV.InvoiceNumber = transactionNumber;
		transDetV.DateTime = transactionDate + transactionTime;
		transDetV.TransactionName = transactionType;
		transDetV.Pan = cardNumber;
		transDetV.CardName = companyName;
		transDetV.ResponseCode = fullResponseCode;
		transDetV.ApprovalCode = authorizationCode;
		transDetV.TransactionAmount = transactionAmount1;
		transDetV.CashAmount = transactionAmount2;
		transDetV.AmountCurrency = amountCurrency;
		transDetV.InstallmentsNumber = installmentsNumber;
		transDetV.CardDataSource = cardDataSource;
		transDetV.EMVData = emvData;
		transDetV.SignatureLine = signatureLinePrintingFlag;
		transDetV.RRN = retrievalReferenceNumber;
		transDetV.PinFlag = PinFlag;
		return transDetV;
	}

	public void addDccData(ref TransParamsV3 tp)
	{
		DCCData dCCData = new DCCData();
		dCCData.dccNumber = dccNumber;
		dCCData.dccProvider = dccProvider;
		dCCData.dccCurrency = dccCurrency;
		dCCData.dccExchangeRate = dccExchangeRate;
		dCCData.dccExchangeRateDate = dccExchangeRateDate;
		dCCData.dccMarkUpPercent = dccMarkUpPercent;
		dCCData.dccAmount = dccAmount;
		dCCData.dccDisclaimer = dccDisclaimer;
		tp.AddDccData(dCCData);
	}

	public byte[] toByteArray()
	{
		int num = 0;
		byte[] array = new byte[receivedChars.Length];
		char[] array2 = receivedChars;
		foreach (char value in array2)
		{
			try
			{
				array[num++] = Convert.ToByte(value);
			}
			catch (OverflowException)
			{
			}
		}
		return array;
	}
}
