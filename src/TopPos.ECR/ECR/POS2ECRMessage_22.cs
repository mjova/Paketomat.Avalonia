using System;
using System.Text;

namespace ECR;

internal class POS2ECRMessage_22 : POS2ECRMessage
{
	public string identifier;

	public string displayMessage;

	private bool valid;

	private char[] receivedChars;

	public new static POS2ECRMessage_22 create(char[] received)
	{
		POS2ECRMessage_22 pOS2ECRMessage_ = new POS2ECRMessage_22();
		pOS2ECRMessage_.fill(received);
		pOS2ECRMessage_.receivedChars = received;
		char c = received[received.Length - 1];
		char[] array = new char[received.Length - 1];
		Array.Copy(received, 1, array, 0, received.Length - 1);
		char c2 = LRC.calculate(array);
		pOS2ECRMessage_.valid = true;
		return pOS2ECRMessage_;
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
		displayMessage = @string.Substring(9, received.Length - 10);
		displayMessage.Replace(COMChars.EOT.ToString(), string.Empty);
	}

	public override bool isValid()
	{
		return true;
	}

	public override bool isHold()
	{
		return false;
	}

	public override bool isError()
	{
		return true;
	}

	public void updateTransactionParams(ref TransParams tp)
	{
	}
}
