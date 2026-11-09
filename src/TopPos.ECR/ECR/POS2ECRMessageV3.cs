using System;

namespace ECR;

internal class POS2ECRMessageV3
{
	public static POS2ECRMessageV3 create(char[] received)
	{
		string text = "";
		if (received[0] == '\u0006' && received.Length > 1)
		{
			text = received[2].ToString() + received[3];
			char[] array = new char[received.Length - 1];
			Array.Copy(received, 1, array, 0, received.Length - 1);
			received = array;
		}
		else
		{
			text = received[1].ToString() + received[2];
		}
		if ("10".Equals(text))
		{
			return POS2ECRMessage_10V3.create(received);
		}
		if ("20".Equals(text))
		{
			return POS2ECRMessage_20V3.create(received);
		}
		if ("22".Equals(text))
		{
			return POS2ECRMessage_22V3.create(received);
		}
		if ("24".Equals(text))
		{
			return POS2ECRMessage_24V3.create(received);
		}
		if ("25".Equals(text))
		{
			return POS2ECRMessage_25V3.create(received);
		}
		if ("26".Equals(text))
		{
			return POS2ECRMessage_26V3.create(received);
		}
		if ("27".Equals(text))
		{
			return POS2ECRMessage_27V3.create(received);
		}
		if ("32".Equals(text))
		{
			return POS2ECRMessage_32V3.create(received);
		}
		if ("35".Equals(text))
		{
			return POS2ECRMessage_35V3.create(received);
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

	public virtual bool isHoldWithCode()
	{
		return false;
	}

	public virtual bool isErrorWithCode()
	{
		return false;
	}

	public virtual bool isCancel()
	{
		return false;
	}

	public virtual bool isAs24()
	{
		return false;
	}

	public virtual bool isFunctionMessage()
	{
		return false;
	}

	public virtual bool isSpecificMessage()
	{
		return false;
	}
}
