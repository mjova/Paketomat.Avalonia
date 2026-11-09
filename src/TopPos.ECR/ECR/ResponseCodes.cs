namespace ECR;

internal class ResponseCodes
{
	public static readonly int SUCCESS = 0;

	public static readonly int COM_UNAVAILABLE = 1;

	public static readonly int NO_ACK_OR_NACK = 2;

	public static readonly int FUNCTION_TIMEOUT = 3;

	public static readonly int INVALID_INPUT = 4;

	public static readonly int CFG_FILE_MISSING = 5;

	public static readonly int INVALID_PRODUCT_KEY = 6;

	public static readonly int POS_ERROR = 7;

	public static readonly int POS_SOCKET_UNREACHABLE = 8;

	public static readonly int TRANSACTION_CANCELED = 9;
}
