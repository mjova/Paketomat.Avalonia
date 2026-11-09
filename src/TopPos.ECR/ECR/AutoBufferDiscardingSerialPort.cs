using System.IO.Ports;

namespace ECR;

internal class AutoBufferDiscardingSerialPort : SerialPort
{
	public new int ReadByte()
	{
		DiscardOutBuffer();
		return base.ReadByte();
	}

	public new void Write(string text)
	{
		DiscardInBuffer();
		base.Write(text);
	}

	public new void Write(char[] buffer, int offset, int count)
	{
		DiscardInBuffer();
		base.Write(buffer, offset, count);
	}

	public new void Write(byte[] buffer, int offset, int count)
	{
		DiscardInBuffer();
		base.Write(buffer, offset, count);
	}

	public new string ReadLine()
	{
		DiscardOutBuffer();
		return base.ReadLine();
	}

	public new string ReadExisting()
	{
		DiscardOutBuffer();
		return base.ReadExisting();
	}
}
