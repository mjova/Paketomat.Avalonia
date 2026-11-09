using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace ECR;

public class TransParamsV3
{
	private ArrayList al = new ArrayList();

	public string TerminalId;

	public string SeqNumber;

	public string TransactionType;

	public string PrinterFlag;

	public string CashierId;

	public string TransactionNumber;

	public long TransactionAmount1;

	public string AmountCurrency;

	public string AuthorizationCode;

	public string InputLabel;

	public string InsurancePolicyNumber;

	public string InstallmentsNumber;

	public string LanguageID;

	public char[] PrintData;

	public string CashierId2;

	public int CashAmount;

	public string PayServices;

	public string BalanceInquiry;

	public string offlineCryptogram;

	public string LoyaltyData;

	public string TacData;

	public string FormattedTextToPrint;

	public string specificProcessingFlag;

	public string TransactionFlag;

	public string BatchNumber;

	public string TransactionDate;

	public string TransactionTime;

	public string CardDataSource;

	public string CardNumber;

	public string TidNumber;

	public string MidNumber;

	public string CompanyName;

	public string EmvData;

	public string SignatureLine;

	public string FullResponseCode;

	public string TransactionStatus;

	public string PINBlock;

	public string CardholderName;

	public string retrievalReferenceNumber;

	public string displayMessage;

	public string PinFlag;

	public string dccStatus;

	public string qrCodeData;

	private List<TotalV3> totals = new List<TotalV3>();

	private List<TransDetV3> transactions = new List<TransDetV3>();

	private List<TotalV3New> totalsNew = new List<TotalV3New>();

	private DCCData dccData = new DCCData();

	public string IPReferenceNumber;

	public string PersonalVehicleCardData;

	public string mifareCardType;

	public string mifareCardUID;

	public string F4Gcode;

	public string radcomSpecificData;

	public string AcquirerID;

	public string Ticket;

	public void addTotalItem(TotalV3 item)
	{
		totals.Add(item);
	}

	public void addTotalNewItem(TotalV3New item)
	{
		totalsNew.Add(item);
	}

	public void addTransItem(TransDetV3 item)
	{
		transactions.Add(item);
	}

	public List<TotalV3> getTotalsForSettlement()
	{
		return totals;
	}

	public List<TotalV3New> getTotalsNew()
	{
		return totalsNew;
	}

	public List<TransDetV3> getAllTransactions()
	{
		return transactions;
	}

	public void clearTotalsForSettlement()
	{
		totals.Clear();
		transactions.Clear();
		totalsNew.Clear();
	}

	public void AddDccData(DCCData inputdata)
	{
		dccData = inputdata;
	}

	public DCCData ReturnDccData()
	{
		return dccData;
	}

	public void returnTicket()
	{
		switch (TransactionType)
		{
		case "01":
			makeTicket();
			break;
		case "02":
			makeTicket();
			break;
		case "03":
			makeTicket();
			break;
		case "04":
			makeTicket();
			break;
		case "05":
			makeTicket();
			break;
		case "06":
			makeTicket();
			break;
		case "07":
			makeTicket();
			break;
		case "10":
			makeTicket();
			break;
		case "12":
			makeTicket();
			break;
		case "20":
			makeTicket();
			break;
		case "30":
			makeSettlementBatchTicket();
			break;
		case "31":
			makeSettlementBatchTicket();
			break;
		case "32":
			makeSettlementBatchTicket();
			break;
		case "33":
			makeIssuerTotalsTicket();
			break;
		default:
			Ticket = "Currently unsupported receipt report.";
			break;
		}
	}

	private void makeTicket()
	{
		_Ticket ticket = new _Ticket();
		string empty = string.Empty;
		string empty2 = string.Empty;
		double num = Convert.ToDouble(TransactionAmount1) / 100.0;
		PtUtils ptUtils = new PtUtils();
		if (CardDataSource != "M")
		{
			ptUtils.ParseTlvByte(ptUtils.StringToByteArray(EmvData), ref al);
			ticket.AID = ptUtils.ReturnValueFromArrayList(al, "84");
			ticket.CardApplicationName = ptUtils.HEX2ASCII(ptUtils.ReturnValueFromArrayList(al, "9F12"));
			empty2 = ptUtils.ReturnValueFromArrayList(al, "9F26");
			empty = ptUtils.ReturnValueFromArrayList(al, "9F27");
			if (empty == "80")
			{
				ticket.EMVData = "ARQC (" + empty + ") " + empty2;
			}
			else
			{
				ticket.EMVData = "AAC (" + empty + ") " + empty2;
			}
		}
		else
		{
			ticket.AID = "";
			ticket.CardApplicationName = "";
			ticket.EMVData = "";
		}
		ticket.Amount = num.ToString("0.00");
		ticket.ApprovalCode = AuthorizationCode;
		ticket.CardIssuer = CompanyName.TrimEnd(' ');
		ticket.CardNumber = CardNumber;
		ticket.InstallmentsNumber = InstallmentsNumber;
		ticket.InvoiceNumber = TransactionNumber;
		ticket.ResponseCode = FullResponseCode;
		ticket.RRN = retrievalReferenceNumber;
		ticket.TerminalID = TidNumber;
		ticket.TransactionDate = TransactionDate;
		ticket.TransactionStatus = displayMessage;
		ticket.TransactionTime = TransactionTime;
		ticket.TransactionType = TransactionType;
		ticket.SignatureLine = SignatureLine;
		Ticket = JsonConvert.SerializeObject((object)ticket, (Formatting)1);
	}

	private void makeSettlementBatchTicket()
	{
		Settlement settlement = new Settlement();
		settlement.AcquirersTotals = new AcqTotals();
		settlement.AcquirersTotals.Acquirers = new List<Scheme>();
		foreach (TotalV3New item in totalsNew)
		{
			Scheme scheme = new Scheme();
			scheme.AcquirerID = item.AcquirerID;
			scheme.BatchNumber = item.BatchNumber;
			scheme.SchemeName = item.AcquirerIssuerName.TrimEnd(' ');
			scheme.DebitTransactionAmount = item.DebitTransactionAmount;
			scheme.DebitTransactionCount = item.DebitTransactionCount;
			scheme.RefundTransactionAmount = item.RefundTransactionAmount;
			scheme.RefundTransactionCount = item.RefundTransactionCount;
			scheme.SpdhHostTotals = item.SpdhHostTotals;
			scheme.SpdhTerminalTotals = item.SpdhTerminalTotals;
			scheme.TermNum = item.HostID;
			settlement.AcquirersTotals.Acquirers.Add(scheme);
		}
		Ticket = JsonConvert.SerializeObject((object)settlement, (Formatting)1);
	}

	private void makeIssuerTotalsTicket()
	{
		List<TotalV3New> list = (from o in totalsNew.Where((TotalV3New item) => !item.AcquirerID.Equals("")).ToList()
			orderby o.AcquirerID
			select o).ToList();
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int num4 = 0;
		int num5 = 0;
		int num6 = 0;
		int num7 = 0;
		int num8 = 0;
		string empty = string.Empty;
		CurrentTotals currentTotals = new CurrentTotals();
		currentTotals.TransactionSummary = new TransactionSummary();
		currentTotals.TransactionSummary.Acquirers = new List<Acquirer>();
		List<Scheme> list2 = new List<Scheme>();
		empty = list[0].AcquirerID;
		Acquirer acquirer;
		Total total;
		for (int i = 0; i < list.Count; i++)
		{
			if (!string.IsNullOrEmpty(list[i].AcquirerID))
			{
				Scheme scheme = new Scheme();
				scheme.AcquirerID = list[i].AcquirerID;
				scheme.BatchNumber = list[i].BatchNumber;
				scheme.SchemeName = list[i].AcquirerIssuerName.TrimEnd(' ');
				scheme.DebitTransactionAmount = (Convert.ToDouble(list[i].DebitTransactionAmount) / 100.0).ToString("0.00");
				scheme.DebitTransactionCount = Convert.ToInt16(list[i].DebitTransactionCount).ToString();
				scheme.RefundTransactionAmount = (Convert.ToDouble(list[i].RefundTransactionAmount) / 100.0).ToString("0.00");
				scheme.RefundTransactionCount = Convert.ToInt16(list[i].RefundTransactionCount).ToString();
				scheme.SpdhHostTotals = list[i].SpdhHostTotals;
				scheme.SpdhTerminalTotals = list[i].SpdhTerminalTotals;
				scheme.TermNum = list[i].HostID;
				num5 += Convert.ToInt32(list[i].DebitTransactionCount);
				num6 += Convert.ToInt32(list[i].DebitTransactionAmount);
				num7 += Convert.ToInt32(list[i].RefundTransactionCount);
				num8 += Convert.ToInt32(list[i].RefundTransactionAmount);
				if (list[i].AcquirerID == empty)
				{
					list2.Add(scheme);
					num += Convert.ToInt32(list[i].DebitTransactionCount);
					num2 += Convert.ToInt32(list[i].DebitTransactionAmount);
					num3 += Convert.ToInt32(list[i].RefundTransactionCount);
					num4 += Convert.ToInt32(list[i].RefundTransactionAmount);
					continue;
				}
				acquirer = new Acquirer();
				acquirer.Schemes = new List<Scheme>();
				List<Scheme> schemes = new List<Scheme>(list2);
				acquirer.Schemes = schemes;
				acquirer.Total = new Total();
				total = new Total();
				total.AcquirerID = empty;
				total.BatchNumber = "0000";
				total.DebitTransactionAmount = (Convert.ToDouble(num2) / 100.0).ToString("0.00");
				total.DebitTransactionCount = num.ToString();
				total.RefundTransactionAmount = (Convert.ToDouble(num4) / 100.0).ToString("0.00");
				total.RefundTransactionCount = num3.ToString();
				total.SchemeName = "TOTAL";
				total.SpdhHostTotals = "";
				total.SpdhTerminalTotals = "";
				total.TermNum = "";
				acquirer.Total = total;
				list2.Clear();
				list2.Add(scheme);
				empty = list[i].AcquirerID;
				num = 0;
				num2 = 0;
				num3 = 0;
				num4 = 0;
				num += Convert.ToInt32(list[i].DebitTransactionCount);
				num2 += Convert.ToInt32(list[i].DebitTransactionAmount);
				num3 += Convert.ToInt32(list[i].RefundTransactionCount);
				num4 += Convert.ToInt32(list[i].RefundTransactionAmount);
				currentTotals.TransactionSummary.Acquirers.Add(acquirer);
			}
		}
		acquirer = new Acquirer();
		acquirer.Schemes = new List<Scheme>();
		acquirer.Total = new Total();
		total = new Total();
		total.AcquirerID = empty;
		total.BatchNumber = "0000";
		total.DebitTransactionAmount = (Convert.ToDouble(num2) / 100.0).ToString("0.00");
		total.DebitTransactionCount = num.ToString();
		total.RefundTransactionAmount = (Convert.ToDouble(num4) / 100.0).ToString("0.00");
		total.RefundTransactionCount = num3.ToString();
		total.SchemeName = "TOTAL";
		total.SpdhHostTotals = "";
		total.SpdhTerminalTotals = "";
		total.TermNum = "";
		acquirer.Total = total;
		List<Scheme> schemes2 = new List<Scheme>(list2);
		acquirer.Schemes = schemes2;
		currentTotals.TransactionSummary.Acquirers.Add(acquirer);
		GrandTotals grandTotals = new GrandTotals();
		grandTotals.AcquirerID = "";
		grandTotals.BatchNumber = "0000";
		grandTotals.DebitTransactionAmount = (Convert.ToDouble(num6) / 100.0).ToString("0.00");
		grandTotals.DebitTransactionCount = num5.ToString();
		grandTotals.RefundTransactionAmount = (Convert.ToDouble(num8) / 100.0).ToString("0.00");
		grandTotals.RefundTransactionCount = num7.ToString();
		grandTotals.SchemeName = "TOTAL";
		grandTotals.SpdhHostTotals = "";
		grandTotals.SpdhTerminalTotals = "";
		grandTotals.TermNum = "";
		currentTotals.TransactionSummary.GrandTotals = grandTotals;
		Ticket = (Ticket = JsonConvert.SerializeObject((object)currentTotals, (Formatting)1));
	}
}
