namespace ECR.V1;

public enum MessageType
{
	None = 0,
	InitiateTransaction = 10,
	InitiateTransactionResp = 11,
	AuthorizationResult = 12,
	AuthorizationResultResp = 13
}
