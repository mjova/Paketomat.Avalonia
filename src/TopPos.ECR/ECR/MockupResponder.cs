using System;
using System.IO;

namespace ECR;

internal class MockupResponder
{
	internal static int DoTransactionV1(ref string transactionType, ref int transactionAmount, ref int installments, ref string respCode, ref string pan, ref string invoiceNumber, ref string approvalCode, ref string date, ref string time, ref string issuerName, ref string terminalNo, ref string batchNumber, ref string transactionCount, ref string transactionTotal, ref string refundCount, ref string refundTotal, ref string activationCode)
	{
		string value = "";
		using (StreamReader streamReader = new StreamReader("dummy_response.cfg"))
		{
			string text;
			while ((text = streamReader.ReadLine()) != null)
			{
				if (!text.StartsWith(";"))
				{
					processLine(text, "V1_respCode", ref respCode);
					processLine(text, "V1_pan", ref pan);
					processLine(text, "V1_approvalCode", ref approvalCode);
					processLine(text, "V1_date", ref date);
					processLine(text, "V1_time", ref time);
					processLine(text, "V1_issuerName", ref issuerName);
					processLine(text, "V1_terminalNo", ref terminalNo);
					processLine(text, "V1_batchNumber", ref batchNumber);
					processLine(text, "V1_transactionCount", ref transactionCount);
					processLine(text, "V1_transactionTotal", ref transactionTotal);
					processLine(text, "V1_refundCount", ref refundCount);
					processLine(text, "V1_refundTotal", ref refundTotal);
					processLine(text, "V1_activationCode", ref activationCode);
					processLine(text, "return", ref value);
				}
			}
		}
		return Convert.ToInt32(value);
	}

	internal static int DoTransactionV2(ref TransParams tp)
	{
		string value = "";
		using (StreamReader streamReader = new StreamReader("dummy_response.cfg"))
		{
			string text;
			while ((text = streamReader.ReadLine()) != null)
			{
				if (!text.StartsWith(";"))
				{
					processLine(text, "V2_TransactionNumber", ref tp.TransactionNumber);
					string value2 = tp.TransactionAmount1.ToString();
					processLine(text, "V2_TransactionAmount1", ref value2);
					tp.TransactionAmount1 = Convert.ToInt32(value2);
					processLine(text, "V2_AuthorizationCode", ref tp.AuthorizationCode);
					processLine(text, "V2_Installments_Number", ref tp.InstallmentsNumber);
					processLine(text, "V2_TransactionFlag", ref tp.TransactionFlag);
					processLine(text, "V2_BatchNumber", ref tp.BatchNumber);
					processLine(text, "V2_TransactionDate", ref tp.TransactionDate);
					processLine(text, "V2_TransactionTime", ref tp.TransactionTime);
					processLine(text, "V2_CardDataSource", ref tp.CardDataSource);
					processLine(text, "V2_CardNumber", ref tp.CardNumber);
					processLine(text, "V2_TidNumber", ref tp.TidNumber);
					processLine(text, "V2_MidNumber", ref tp.MidNumber);
					processLine(text, "V2_CompanyName", ref tp.CompanyName);
					processLine(text, "V2_EmvData", ref tp.EmvData);
					processLine(text, "V2_SignatureLine", ref tp.SignatureLine);
					processLine(text, "V2_AcquirerName", ref tp.AcquirerName);
					processLine(text, "V2_DebitTransactionCount", ref tp.DebitTransactionCount);
					processLine(text, "V2_DebitTransactionAmount", ref tp.DebitTransactionAmount);
					processLine(text, "V2_RefundTransactionCount", ref tp.RefundTransactionCount);
					processLine(text, "V2_RefundTransactionAmount", ref tp.RefundTransactionAmount);
					processLine(text, "V2_MoreMessages", ref tp.MoreMessages);
					processLine(text, "V2_FullResponseCode", ref tp.FullResponseCode);
					processLine(text, "V2_TransactionStatus", ref tp.TransactionStatus);
					processLine(text, "V2_SpdhTerminalTotals", ref tp.SpdhTerminalTotals);
					processLine(text, "V2_SpdhHostTotals", ref tp.SpdhHostTotals);
					processLine(text, "V2_PINBlock", ref tp.PINBlock);
					processLine(text, "V2_CardholderName", ref tp.CardholderName);
					processLine(text, "return", ref value);
				}
			}
		}
		return Convert.ToInt32(value);
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
}
