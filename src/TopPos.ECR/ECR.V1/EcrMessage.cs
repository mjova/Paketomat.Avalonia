using System;
using System.Collections;

namespace ECR.V1;

internal class EcrMessage
{
	private TransactionType fldTransactionType = TransactionType.None;

	private MessageType fldMessageType = MessageType.None;

	private string fldRespCode = "";

	private int fldEcrNumber = -1;

	private int fldEcrReceiptNumber = -1;

	private int fldTransactionAmount1 = -1;

	private int fldTransactionAmount2 = -1;

	private string fldPan = "";

	private string fldExpDate = "";

	private string fldProductCodes = "";

	private string fldInvoiceNumber = "";

	private string fldApprovalCode = "";

	private string fldDate = "";

	private string fldTime = "";

	private string fldIssuerName = "";

	private int fldCashierId = 0;

	private string fldDocumentId = "";

	private string fldInputLabel = "";

	private int fldInstallments = -1;

	private string fldInsurancePolicy = "";

	private string fldTerminalNo = "";

	private string fldBatchNumber = "";

	private string fldTransactionCount = "";

	private string fldTransactionTotal = "";

	private string fldRefundCount = "";

	private string fldRefundTotal = "";

	private string activationCode = "";

	internal TransactionType FldTransactionType
	{
		get
		{
			return fldTransactionType;
		}
		set
		{
			fldTransactionType = value;
		}
	}

	internal MessageType FldMessageType
	{
		get
		{
			return fldMessageType;
		}
		set
		{
			fldMessageType = value;
		}
	}

	internal string FldRespCode
	{
		get
		{
			return fldRespCode;
		}
		set
		{
			fldRespCode = value;
		}
	}

	internal int FldEcrNumber
	{
		get
		{
			return fldEcrNumber;
		}
		set
		{
			fldEcrNumber = value;
		}
	}

	internal int FldEcrReceiptNumber
	{
		get
		{
			return fldEcrReceiptNumber;
		}
		set
		{
			fldEcrReceiptNumber = value;
		}
	}

	internal int FldTransactionAmount1
	{
		get
		{
			return fldTransactionAmount1;
		}
		set
		{
			if (value > 0)
			{
				fldTransactionAmount1 = value;
			}
			else if (FldTransactionType == TransactionType.PUR)
			{
				EcrMessage.InvalidInputData();
			}
		}
	}

	internal int FldTransactionAmount2
	{
		get
		{
			return fldTransactionAmount2;
		}
		set
		{
			fldTransactionAmount2 = value;
		}
	}

	internal string FldPan
	{
		get
		{
			return fldPan;
		}
		set
		{
			fldPan = value;
		}
	}

	internal string FldExpDate
	{
		get
		{
			return fldExpDate;
		}
		set
		{
			fldExpDate = value;
		}
	}

	internal string FldProductCodes
	{
		get
		{
			return fldProductCodes;
		}
		set
		{
			fldProductCodes = value;
		}
	}

	internal string FldInvoiceNumber
	{
		get
		{
			return fldInvoiceNumber;
		}
		set
		{
			fldInvoiceNumber = value;
		}
	}

	internal string FldApprovalCode
	{
		get
		{
			return fldApprovalCode;
		}
		set
		{
			fldApprovalCode = value;
		}
	}

	internal string FldDate
	{
		get
		{
			return fldDate;
		}
		set
		{
			fldDate = value;
		}
	}

	internal string FldTime
	{
		get
		{
			return fldTime;
		}
		set
		{
			fldTime = value;
		}
	}

	internal string FldIssuerName
	{
		get
		{
			return fldIssuerName;
		}
		set
		{
			fldIssuerName = value;
		}
	}

	internal int FldCashierId
	{
		get
		{
			return fldCashierId;
		}
		set
		{
			fldCashierId = value;
		}
	}

	internal string FldDocumentId
	{
		get
		{
			return fldDocumentId;
		}
		set
		{
			fldDocumentId = value;
		}
	}

	internal string FldInputLabel
	{
		get
		{
			return fldInputLabel;
		}
		set
		{
			fldInputLabel = value;
		}
	}

	internal int FldInstallments
	{
		get
		{
			return fldInstallments;
		}
		set
		{
			if (FldTransactionType == TransactionType.INS)
			{
				if (value > 0)
				{
					fldInstallments = value;
				}
				else
				{
					EcrMessage.InvalidInputData();
				}
			}
			else if (value > 0)
			{
				EcrMessage.InvalidInputData();
			}
			else
			{
				fldInstallments = value;
			}
		}
	}

	internal string FldInsurancePolicy
	{
		get
		{
			return fldInsurancePolicy;
		}
		set
		{
			fldInsurancePolicy = value;
		}
	}

	internal string FldTerminalNo
	{
		get
		{
			return fldTerminalNo;
		}
		set
		{
			fldTerminalNo = value;
		}
	}

	internal string FldBatchNumber
	{
		get
		{
			return fldBatchNumber;
		}
		set
		{
			fldBatchNumber = value;
		}
	}

	internal string FldTransactionCount
	{
		get
		{
			return fldTransactionCount;
		}
		set
		{
			fldTransactionCount = value;
		}
	}

	internal string FldTransactionTotal
	{
		get
		{
			return fldTransactionTotal;
		}
		set
		{
			fldTransactionTotal = value;
		}
	}

	internal string FldRefundCount
	{
		get
		{
			return fldRefundCount;
		}
		set
		{
			fldRefundCount = value;
		}
	}

	internal string FldRefundTotal
	{
		get
		{
			return fldRefundTotal;
		}
		set
		{
			fldRefundTotal = value;
		}
	}

	internal string FldActivationCode
	{
		get
		{
			return activationCode;
		}
		set
		{
			activationCode = value;
		}
	}

	internal static event VoidFuncDelegate InvalidInputData;

	internal EcrMessage()
	{
	}

	internal EcrMessage(TransactionType Ttype, MessageType Mtype)
	{
		fldTransactionType = Ttype;
		fldMessageType = Mtype;
	}

	internal static EcrMessage Parse(string s)
	{
		string[] array = s.Split(Convert.ToChar(28));
		if (array[array.Length - 1] == "")
		{
			ArrayList arrayList = new ArrayList(array);
			arrayList.RemoveAt(arrayList.Count - 1);
			array = (string[])arrayList.ToArray(typeof(string));
		}
		EcrMessage ecrMessage = new EcrMessage();
		switch (array[0].Substring(0, 3))
		{
		case "PUR":
			ecrMessage.FldTransactionType = TransactionType.PUR;
			break;
		case "VOD":
			ecrMessage.FldTransactionType = TransactionType.VOD;
			break;
		case "SET":
			ecrMessage.FldTransactionType = TransactionType.SET;
			break;
		case "REF":
			ecrMessage.FldTransactionType = TransactionType.REF;
			break;
		case "CAS":
			ecrMessage.FldTransactionType = TransactionType.CAS;
			break;
		case "INS":
			ecrMessage.FldTransactionType = TransactionType.INS;
			break;
		}
		switch (array[0].Substring(3, 2))
		{
		case "10":
			ecrMessage.FldMessageType = MessageType.InitiateTransaction;
			break;
		case "11":
			ecrMessage.FldMessageType = MessageType.InitiateTransactionResp;
			break;
		case "12":
			ecrMessage.FldMessageType = MessageType.AuthorizationResult;
			break;
		case "13":
			ecrMessage.FldMessageType = MessageType.AuthorizationResultResp;
			break;
		default:
			ecrMessage.FldMessageType = MessageType.None;
			break;
		}
		int result = 0;
		if (ecrMessage.FldMessageType == MessageType.AuthorizationResult)
		{
			string s2 = array[3];
			int.TryParse(s2, out result);
			ecrMessage.FldTransactionAmount1 = result;
		}
		if (ecrMessage.FldMessageType == MessageType.AuthorizationResult)
		{
			string text = array[0].Substring(6, 2);
			ecrMessage.FldRespCode = text;
		}
		if (ecrMessage.FldMessageType == MessageType.AuthorizationResult)
		{
			string text2 = array[5];
			ecrMessage.FldPan = text2;
		}
		string text3 = "";
		if (ecrMessage.FldMessageType == MessageType.AuthorizationResult)
		{
			try
			{
				text3 = array[6].Substring(0, 4);
			}
			catch
			{
			}
			ecrMessage.FldExpDate = text3;
		}
		string text4 = "";
		if (ecrMessage.FldMessageType == MessageType.AuthorizationResult)
		{
			try
			{
				text4 = array[8].Substring(0, 6);
			}
			catch
			{
			}
			ecrMessage.FldInvoiceNumber = text4;
		}
		string text5 = "";
		if (ecrMessage.FldMessageType == MessageType.AuthorizationResult)
		{
			try
			{
				text5 = array[8].Substring(6, 6);
			}
			catch
			{
			}
			ecrMessage.FldApprovalCode = text5;
		}
		string text6 = "";
		if (ecrMessage.FldMessageType == MessageType.AuthorizationResult)
		{
			try
			{
				text6 = array[8].Substring(12, 4);
			}
			catch
			{
			}
			ecrMessage.fldDate = text6;
		}
		string text7 = "";
		if (ecrMessage.FldMessageType == MessageType.AuthorizationResult)
		{
			try
			{
				text7 = array[8].Substring(16, 4);
			}
			catch
			{
			}
			ecrMessage.fldTime = text7;
		}
		string text8 = "";
		if (ecrMessage.FldMessageType == MessageType.AuthorizationResult)
		{
			try
			{
				text8 = array[8].Substring(20, 10);
			}
			catch
			{
			}
			ecrMessage.FldIssuerName = text8;
		}
		string text9 = "";
		if (ecrMessage.FldMessageType == MessageType.AuthorizationResult)
		{
			try
			{
				text9 = array[10];
			}
			catch
			{
			}
			ecrMessage.FldTerminalNo = text9;
		}
		string text10 = "";
		if (ecrMessage.FldMessageType == MessageType.AuthorizationResult)
		{
			try
			{
				text10 = array[12].Substring(0, 6);
			}
			catch
			{
			}
			ecrMessage.FldBatchNumber = text10;
		}
		string text11 = "";
		if (ecrMessage.FldMessageType == MessageType.AuthorizationResult)
		{
			try
			{
				text11 = array[12].Substring(6, 3);
			}
			catch
			{
			}
			ecrMessage.FldTransactionCount = text11;
		}
		string text12 = "";
		if (ecrMessage.FldMessageType == MessageType.AuthorizationResult)
		{
			try
			{
				text12 = array[12].Substring(9, 12);
			}
			catch
			{
			}
			ecrMessage.FldTransactionTotal = text12;
		}
		string text13 = "";
		if (ecrMessage.FldMessageType == MessageType.AuthorizationResult)
		{
			try
			{
				text13 = array[13].Substring(0, 3);
			}
			catch
			{
			}
			ecrMessage.FldRefundCount = text13;
		}
		string text14 = "";
		if (ecrMessage.FldMessageType == MessageType.AuthorizationResult)
		{
			try
			{
				text14 = array[13].Substring(3, 12);
			}
			catch
			{
			}
			ecrMessage.FldRefundTotal = text14;
		}
		int result2 = 0;
		if (ecrMessage.FldMessageType == MessageType.AuthorizationResult && array.Length > 13)
		{
			string s3 = array[14];
			int.TryParse(s3, out result2);
			ecrMessage.FldInstallments = result2;
		}
		return ecrMessage;
	}
}
