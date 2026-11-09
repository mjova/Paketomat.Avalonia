using System;
using System.Text;

namespace ECR;

internal class POS2ECRMessage_20V3 : POS2ECRMessageV3
{
	public string identifier;

	public string displayMessage;

	private bool valid;

	private char[] receivedChars;

	public new static POS2ECRMessage_20V3 create(char[] received)
	{
		POS2ECRMessage_20V3 pOS2ECRMessage_20V = new POS2ECRMessage_20V3();
		pOS2ECRMessage_20V.receivedChars = received;
		pOS2ECRMessage_20V.fill(received);
		char c = received[received.Length - 1];
		char[] array = new char[received.Length - 1];
		Array.Copy(received, 1, array, 0, received.Length - 1);
		char c2 = LRC.calculate(array);
		pOS2ECRMessage_20V.valid = true;
		return pOS2ECRMessage_20V;
	}

	private void fill(char[] received)
	{
		int num = 0;
		byte[] array = new byte[received.Length];
		foreach (char value in received)
		{
			try
			{
				array[num++] = Convert.ToByte(value);
			}
			catch (OverflowException)
			{
			}
		}
		byte[] bytes = Encoding.Convert(Encoding.GetEncoding(Configuration.posMessageEncoding), Encoding.UTF8, array);
		string @string = Encoding.UTF8.GetString(bytes);
		identifier = received[1].ToString() + received[2];
		displayMessage = @string.Substring(9, received.Length - 11);
	}

	public override bool isValid()
	{
		return true;
	}

	public override bool isHold()
	{
		return true;
	}

	public override bool isError()
	{
		return false;
	}

	public void updateTransactionParams(ref TransParamsV3 tp)
	{
		tp.displayMessage = displayMessage;
	}

	public byte[] toByteArray()
	{
		int num = 0;
		byte[] array = new byte[receivedChars.Length];
		char[] array2 = receivedChars;
		foreach (char value in array2)
		{
			try
			{
				array[num++] = Convert.ToByte(value);
			}
			catch (OverflowException)
			{
			}
		}
		return array;
	}
}
