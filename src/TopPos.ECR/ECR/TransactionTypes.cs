namespace ECR;

internal class TransactionTypes
{
	public static readonly string INITIALISATION = "00";

	public static readonly string SALE = "01";

	public static readonly string PREAUTHORISATION = "02";

	public static readonly string CASH_ADVANCE = "03";

	public static readonly string PREAUTHORISATION_COMPLETION = "04";

	public static readonly string REFUND = "05";

	public static readonly string OFFLINE_SALE = "06";

	public static readonly string VOID_TRANSACTION = "10";

	public static readonly string CANCEL_PREVIOUS_TRANSACTION = "11";

	public static readonly string SALE_WITH_INSTALLMENTS = "20";

	public static readonly string SETTLEMENT = "30";

	public static readonly string BATCH_STATUS = "31";

	public static readonly string CURRENT_TOTALS = "32";

	public static readonly string CURRENT_ISSUER_TOTALS = "33";

	public static readonly string PRINT_DATA = "97";

	public static readonly string CARD_READ = "98";

	public static readonly string INPUT_DATA = "99";
}
