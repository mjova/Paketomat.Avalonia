using System.Collections.Generic;

namespace ECR;

public class TransactionSummary
{
	public List<Acquirer> Acquirers { get; set; }

	public GrandTotals GrandTotals { get; set; }
}
