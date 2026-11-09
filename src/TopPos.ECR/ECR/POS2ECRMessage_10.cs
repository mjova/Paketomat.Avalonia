using System;

namespace ECR;

internal class POS2ECRMessage_10 : POS2ECRMessage
{
	private bool valid = false;

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

	public new static POS2ECRMessage_10 create(char[] received)
	{
		POS2ECRMessage_10 pOS2ECRMessage_ = new POS2ECRMessage_10();
		pOS2ECRMessage_.receivedChars = received;
		pOS2ECRMessage_.fill(received);
		char c = received[received.Length - 1];
		char[] array = new char[received.Length - 1];
		Array.Copy(received, 1, array, 0, received.Length - 1);
		char c2 = LRC.calculate(array);
		pOS2ECRMessage_.valid = true;
		return pOS2ECRMessage_;
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
		moreMessagesFlag = array[27];
		installmentsNumber = array[28];
		fullResponseCode = array[29];
		transactionStatus = array[30];
		spdhTerminalTotals = array[31];
		spdhHostTotals = array[32];
		pinBlock = array[33];
		cardHolderName = array[34];
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

	public void updateTransactionParams(ref TransParams tp)
	{
		tp.TransactionNumber = transactionNumber;
		if (!string.IsNullOrEmpty(transactionAmount1))
		{
			tp.TransactionAmount1 = int.Parse(transactionAmount1);
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
		tp.EmvData = emvData;
		tp.SignatureLine = signatureLinePrintingFlag;
		if ("0".Equals(moreMessagesFlag))
		{
			tp.AcquirerName = acquirerName;
			tp.DebitTransactionCount = debitTransactionCount;
			tp.DebitTransactionAmount = debitTransactionAmount;
			tp.RefundTransactionCount = refundTransactionCount;
			tp.RefundTransactionAmount = refundTransactionAmount;
			tp.SpdhTerminalTotals = spdhTerminalTotals;
			tp.SpdhHostTotals = spdhHostTotals;
		}
		tp.MoreMessages = moreMessagesFlag;
		tp.FullResponseCode = fullResponseCode;
		tp.TransactionStatus = transactionStatus;
		tp.PINBlock = pinBlock;
		tp.CardholderName = cardHolderName;
	}
}
