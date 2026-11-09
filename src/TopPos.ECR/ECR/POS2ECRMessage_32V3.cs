using System;
using System.Text;

namespace ECR;

internal class POS2ECRMessage_32V3 : POS2ECRMessageV3
{
	public string identifier;

	public string code;

	public string displayMessage;

	private bool valid;

	private char[] receivedChars;

	public new static POS2ECRMessage_32V3 create(char[] received)
	{
		POS2ECRMessage_32V3 pOS2ECRMessage_32V = new POS2ECRMessage_32V3();
		pOS2ECRMessage_32V.fill(received);
		pOS2ECRMessage_32V.receivedChars = received;
		char c = received[received.Length - 1];
		char[] array = new char[received.Length - 1];
		Array.Copy(received, 1, array, 0, received.Length - 1);
		char c2 = LRC.calculate(array);
		pOS2ECRMessage_32V.valid = true;
		return pOS2ECRMessage_32V;
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
		code = array2[3].ToString();
		displayMessage = array[1];
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

	public override bool isFunctionMessage()
	{
		return true;
	}

	public void updateTransactionParams(ref FunctionParamV3 fp)
	{
		fp.status = code;
		fp.responseMessage = displayMessage;
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
