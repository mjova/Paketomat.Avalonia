using System;
using System.Collections.Generic;
using System.Text;

namespace ECR;

internal class ECR2POSMessage_00V3
{
	public string identifier;

	public string terminalId;

	public string sourceId;

	public string sequentialNumber;

	public string transactionType;

	public string printerFlag;

	public string cashierId;

	public string transactionNumber;

	public string transactionAmount1;

	public string amountExponent;

	public string amountCurrency;

	public string authorizationCode;

	public string inputLabel;

	public string insurancePolicyNumber;

	public string installmentsNumber;

	public string languageId;

	public char[] printData;

	public string cashierId2;

	public string cashAmount;

	public string payServices;

	public string tacData;

	public string ipReferenceForRefund;

	public string qrCodeData = string.Empty;

	public string specificProcessingFlag = string.Empty;

	public string radcomSpecificData = string.Empty;

	public static ECR2POSMessage_00V3 create(TransParamsV3 tp)
	{
		string s = new string(tp.PrintData);
		byte[] bytes = Encoding.GetEncoding("ISO-8859-2").GetBytes(s);
		string text = string.Empty;
		byte[] array = bytes;
		foreach (byte b in array)
		{
			string text2 = text;
			char c = (char)b;
			text = text2 + c;
		}
		text.ToCharArray();
		ECR2POSMessage_00V3 eCR2POSMessage_00V = new ECR2POSMessage_00V3();
		eCR2POSMessage_00V.identifier = "00";
		eCR2POSMessage_00V.terminalId = tp.TerminalId;
		eCR2POSMessage_00V.sourceId = "00";
		eCR2POSMessage_00V.sequentialNumber = tp.SeqNumber;
		eCR2POSMessage_00V.transactionType = tp.TransactionType;
		eCR2POSMessage_00V.printerFlag = tp.PrinterFlag;
		eCR2POSMessage_00V.cashierId = tp.CashierId;
		eCR2POSMessage_00V.transactionNumber = tp.TransactionNumber;
		eCR2POSMessage_00V.transactionAmount1 = tp.TransactionAmount1.ToString();
		eCR2POSMessage_00V.amountExponent = "+0";
		eCR2POSMessage_00V.amountCurrency = tp.AmountCurrency;
		eCR2POSMessage_00V.authorizationCode = tp.AuthorizationCode;
		eCR2POSMessage_00V.inputLabel = tp.InputLabel;
		eCR2POSMessage_00V.insurancePolicyNumber = tp.InsurancePolicyNumber;
		eCR2POSMessage_00V.installmentsNumber = tp.InstallmentsNumber;
		eCR2POSMessage_00V.languageId = tp.LanguageID;
		eCR2POSMessage_00V.printData = text.ToCharArray();
		eCR2POSMessage_00V.cashierId2 = tp.CashierId2;
		eCR2POSMessage_00V.cashAmount = ((tp.CashAmount.ToString() == "0") ? "" : tp.CashAmount.ToString());
		eCR2POSMessage_00V.payServices = tp.PayServices;
		eCR2POSMessage_00V.tacData = tp.TacData;
		eCR2POSMessage_00V.ipReferenceForRefund = tp.IPReferenceNumber;
		eCR2POSMessage_00V.qrCodeData = tp.qrCodeData;
		eCR2POSMessage_00V.specificProcessingFlag = (string.IsNullOrEmpty(tp.specificProcessingFlag) ? "" : tp.specificProcessingFlag);
		eCR2POSMessage_00V.radcomSpecificData = (string.IsNullOrEmpty(tp.radcomSpecificData) ? "" : tp.radcomSpecificData);
		return eCR2POSMessage_00V;
	}

	public char[] toCharArray()
	{
		List<char> list = new List<char>();
		list.Add(COMChars.STX);
		list.Add(COMChars.STX);
		list.AddRange(identifier.ToCharArray());
		list.AddRange(terminalId.ToCharArray());
		list.AddRange(sourceId.ToCharArray());
		list.AddRange(sequentialNumber.ToCharArray());
		list.AddRange(transactionType.ToCharArray());
		list.AddRange(printerFlag.ToCharArray());
		list.AddRange(cashierId.ToCharArray());
		list.AddRange(transactionNumber.ToCharArray());
		list.Add(COMChars.FS);
		list.AddRange(transactionAmount1.ToCharArray());
		list.Add(COMChars.FS);
		list.Add(COMChars.FS);
		list.AddRange(amountExponent.ToCharArray());
		list.Add(COMChars.FS);
		list.AddRange(amountCurrency.ToCharArray());
		list.Add(COMChars.FS);
		list.Add(COMChars.FS);
		list.Add(COMChars.FS);
		list.Add(COMChars.FS);
		list.AddRange(authorizationCode.ToCharArray());
		list.Add(COMChars.FS);
		list.Add(COMChars.FS);
		list.Add(COMChars.FS);
		list.AddRange(inputLabel.ToCharArray());
		list.Add(COMChars.FS);
		list.AddRange(insurancePolicyNumber.ToCharArray());
		list.Add(COMChars.FS);
		list.AddRange(installmentsNumber.ToCharArray());
		list.Add(COMChars.FS);
		list.Add(COMChars.FS);
		list.AddRange(languageId.ToCharArray());
		list.Add(COMChars.FS);
		list.AddRange(printData);
		list.Add(COMChars.FS);
		list.AddRange(cashierId2.ToCharArray());
		list.Add(COMChars.FS);
		list.AddRange(cashAmount.ToCharArray());
		list.Add(COMChars.FS);
		list.AddRange(payServices.ToCharArray());
		list.Add(COMChars.FS);
		list.AddRange(tacData.ToCharArray());
		list.Add(COMChars.FS);
		list.AddRange(ipReferenceForRefund.ToCharArray());
		list.Add(COMChars.FS);
		list.AddRange(qrCodeData.ToCharArray());
		list.Add(COMChars.FS);
		list.AddRange(specificProcessingFlag.ToCharArray());
		list.Add(COMChars.FS);
		list.AddRange(radcomSpecificData.ToCharArray());
		list.Add(COMChars.FS);
		list.Add(COMChars.ETX);
		char item = LRC.calculate(list.GetRange(1, list.Count - 1).ToArray());
		list.Add(item);
		return list.ToArray();
	}

	public byte[] toByteArray()
	{
		char[] array = toCharArray();
		int num = 0;
		byte[] array2 = new byte[array.Length];
		char[] array3 = array;
		foreach (char value in array3)
		{
			try
			{
				array2[num++] = Convert.ToByte(value);
			}
			catch (OverflowException)
			{
			}
		}
		return array2;
	}

	public static bool isValid(TransParamsV3 tp)
	{
		if (tp.TerminalId == null || "".Equals(tp.TerminalId))
		{
			return false;
		}
		if (tp.SeqNumber == null || "".Equals(tp.SeqNumber))
		{
			return false;
		}
		if (tp.TransactionType == null || "".Equals(tp.TransactionType))
		{
			return false;
		}
		if (tp.PrinterFlag == null || "".Equals(tp.PrinterFlag))
		{
			return false;
		}
		if (tp.CashierId == null || "".Equals(tp.CashierId))
		{
			return false;
		}
		if (TransactionTypes.VOID_TRANSACTION.Equals(tp.TransactionNumber) && (tp.TransactionNumber == null || "".Equals(tp.TransactionNumber)))
		{
			return false;
		}
		if (tp.AmountCurrency == null || "".Equals(tp.AmountCurrency))
		{
			return false;
		}
		return true;
	}
}
