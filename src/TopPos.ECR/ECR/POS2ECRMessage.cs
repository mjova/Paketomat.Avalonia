namespace ECR;

internal class POS2ECRMessage
{
	public static POS2ECRMessage create(char[] received)
	{
		string value = received[1].ToString() + received[2];
		if ("10".Equals(value))
		{
			return POS2ECRMessage_10.create(received);
		}
		if ("20".Equals(value))
		{
			return POS2ECRMessage_20.create(received);
		}
		if ("22".Equals(value))
		{
			return POS2ECRMessage_22.create(received);
		}
		return null;
	}

	public virtual bool isValid()
	{
		return false;
	}

	public virtual bool isHold()
	{
		return false;
	}

	public virtual bool isError()
	{
		return false;
	}
}
