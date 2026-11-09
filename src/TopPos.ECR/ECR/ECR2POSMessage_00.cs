using System.Collections.Generic;

namespace ECR;

internal class ECR2POSMessage_00
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

	public static ECR2POSMessage_00 create(TransParams tp)
	{
		ECR2POSMessage_00 eCR2POSMessage_ = new ECR2POSMessage_00();
		eCR2POSMessage_.identifier = "00";
		eCR2POSMessage_.terminalId = tp.TerminalId;
		eCR2POSMessage_.sourceId = "00";
		eCR2POSMessage_.sequentialNumber = tp.SeqNumber;
		eCR2POSMessage_.transactionType = tp.TransactionType;
		eCR2POSMessage_.printerFlag = tp.PrinterFlag;
		eCR2POSMessage_.cashierId = tp.CashierId;
		eCR2POSMessage_.transactionNumber = ((tp.TransactionNumber == null) ? "" : tp.TransactionNumber);
		eCR2POSMessage_.transactionAmount1 = ((tp.TransactionAmount1 == 0) ? "" : tp.TransactionAmount1.ToString());
		eCR2POSMessage_.amountExponent = "+0";
		eCR2POSMessage_.amountCurrency = tp.AmountCurrency;
		eCR2POSMessage_.authorizationCode = ((tp.AuthorizationCode == null) ? "" : tp.AuthorizationCode);
		eCR2POSMessage_.inputLabel = ((tp.InputLabel == null) ? "" : tp.InputLabel);
		eCR2POSMessage_.insurancePolicyNumber = ((tp.InsurancePolicyNumber == null) ? "" : tp.InsurancePolicyNumber);
		eCR2POSMessage_.installmentsNumber = ((tp.InstallmentsNumber == null) ? "" : tp.InstallmentsNumber);
		return eCR2POSMessage_;
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
		list.Add(COMChars.ETX);
		char item = LRC.calculate(list.GetRange(1, list.Count - 1).ToArray());
		list.Add(item);
		return list.ToArray();
	}

	public static bool isValid(TransParams tp)
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
