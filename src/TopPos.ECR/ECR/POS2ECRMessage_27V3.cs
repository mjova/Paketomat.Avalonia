using System;
using System.Text;

namespace ECR;

internal class POS2ECRMessage_27V3 : POS2ECRMessageV3
{
	public string identifier;

	public string displayMessage;

	public string track2data;

	public string bfoc;

	private bool valid;

	private char[] receivedChars;

	public new static POS2ECRMessage_27V3 create(char[] received)
	{
		POS2ECRMessage_27V3 pOS2ECRMessage_27V = new POS2ECRMessage_27V3();
		pOS2ECRMessage_27V.fill(received);
		pOS2ECRMessage_27V.receivedChars = received;
		char c = received[received.Length - 1];
		char[] array = new char[received.Length - 1];
		Array.Copy(received, 1, array, 0, received.Length - 1);
		char c2 = LRC.calculate(array);
		pOS2ECRMessage_27V.valid = true;
		return pOS2ECRMessage_27V;
	}

	private void fill(char[] received)
	{
		char[] separator = new char[1] { COMChars.FS };
		string text = new string(received);
		string[] array = text.Split(separator);
		char[] array2 = array[0].ToCharArray();
		int num = 0;
		byte[] array3 = new byte[array2.Length];
		char[] array4 = array2;
		foreach (char value in array4)
		{
			try
			{
				array3[num++] = Convert.ToByte(value);
			}
			catch (OverflowException)
			{
			}
		}
		byte[] bytes = Encoding.Convert(Encoding.GetEncoding(Configuration.posMessageEncoding), Encoding.UTF8, array3);
		string @string = Encoding.UTF8.GetString(bytes);
		identifier = array2[1].ToString() + array2[2];
		displayMessage = @string.Substring(9, @string.Length - 9);
		track2data = array[1];
		bfoc = array[2];
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
		return false;
	}

	public override bool isHoldWithCode()
	{
		return false;
	}

	public override bool isErrorWithCode()
	{
		return false;
	}

	public override bool isAs24()
	{
		return true;
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
