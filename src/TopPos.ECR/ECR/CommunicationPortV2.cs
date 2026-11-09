using System;
using System.Collections.Generic;
using System.IO.Ports;

namespace ECR;

internal class CommunicationPortV2 : SerialPort
{
	private const int maxMessageLenth = 1024;

	internal static object readResult;

	internal static bool success;

	public CommunicationPortV2()
	{
		base.PortName = Configuration.portName;
		base.BaudRate = Configuration.baudRate;
		base.DataBits = Configuration.dataBits;
		base.Parity = (Parity)Enum.Parse(typeof(Parity), Configuration.parity.ToString());
		base.StopBits = (StopBits)Enum.Parse(typeof(StopBits), Configuration.stopBits.ToString());
	}

	public CommunicationPortV2(string portName, int baudRate, int dataBits, Parity parity, StopBits stopBits)
	{
		base.PortName = portName;
		base.BaudRate = baudRate;
		base.DataBits = dataBits;
		base.Parity = parity;
		base.StopBits = stopBits;
	}

	public CommunicationPortV2(string portName, int baudRate)
	{
		base.PortName = portName;
		base.BaudRate = baudRate;
		base.DataBits = 8;
		base.Parity = Parity.None;
		base.StopBits = StopBits.One;
	}

	internal void writeMessage(object data)
	{
		ECR2POSMessage_00 eCR2POSMessage_ = (ECR2POSMessage_00)data;
		try
		{
			char[] array = eCR2POSMessage_.toCharArray();
			Write(array, 0, array.Length);
			if (Configuration.enableLogging)
			{
				EcrLog.Write(new string(array), directionSending: true, LogMessageType.MSG);
			}
			success = true;
			EcrConnectorV2.progress.Set();
		}
		catch (Exception e)
		{
			EcrLog.WriteException("in write message", e);
			success = false;
		}
	}

	internal void waitForAck()
	{
		readChar();
	}

	internal void readChar()
	{
		bool flag = true;
		EcrConnectorV2.timeout = false;
		try
		{
			DateTime now = DateTime.Now;
			while (flag)
			{
				if (base.BytesToRead > 0)
				{
					readResult = ReadChar();
					break;
				}
				DateTime now2 = DateTime.Now;
				long num = (long)(now2 - now).TotalMilliseconds;
				if (num > Configuration.waitForAckTimeout)
				{
					throw new TimeoutException("The operation has timed out. No reply from device");
				}
			}
			if (Configuration.enableLogging)
			{
				if ((int)readResult == 6)
				{
					EcrLog.WriteAckNack(readResult.ToString(), directionSending: false, LogMessageType.ACK, detailedLogging: false);
				}
				else
				{
					EcrLog.WriteAckNack(readResult.ToString(), directionSending: false, LogMessageType.NAK, detailedLogging: false);
				}
			}
			success = true;
			EcrConnectorV2.timeout = false;
			EcrConnectorV2.progress.Set();
		}
		catch (TimeoutException e)
		{
			EcrLog.WriteException("in read char", e);
			success = false;
			EcrConnectorV2.timeout = true;
		}
		catch (Exception e2)
		{
			EcrLog.WriteException("in read char", e2);
			success = false;
			EcrConnectorV2.timeout = false;
		}
	}

	internal void waitForMessage()
	{
		readMessage();
	}

	internal void readMessage()
	{
		bool flag = true;
		bool flag2 = false;
		try
		{
			int num = 0;
			List<char> list = new List<char>();
			DateTime now = DateTime.Now;
			while (flag)
			{
				if (!flag2)
				{
					if (base.BytesToRead > 0)
					{
						num = ReadByte();
						if (num != 3)
						{
							list.Add((char)num);
						}
						else
						{
							flag2 = true;
						}
					}
				}
				else if (base.BytesToRead > 0)
				{
					char item = (char)ReadChar();
					list.Add(item);
					flag = false;
				}
				DateTime now2 = DateTime.Now;
				long num2 = (long)(now2 - now).TotalMilliseconds;
				if (num2 > Configuration.functionTimeout)
				{
					throw new TimeoutException("The operation has timed out. No reply from device");
				}
			}
			if (Configuration.enableLogging)
			{
				EcrLog.Write(new string(list.ToArray()), directionSending: false, LogMessageType.MSG);
			}
			readResult = list.ToArray();
			success = true;
			EcrConnectorV2.timeout = false;
			EcrConnectorV2.progress.Set();
		}
		catch (TimeoutException e)
		{
			EcrLog.WriteException("in read message", e);
			success = false;
			EcrConnectorV2.timeout = true;
		}
		catch (Exception e2)
		{
			EcrLog.WriteException("in read message", e2);
			success = false;
			EcrConnectorV2.timeout = false;
		}
	}

	internal void sendNAK()
	{
		try
		{
			char[] array = new char[1] { COMChars.NAK };
			Write(array, 0, array.Length);
			if (Configuration.enableLogging)
			{
				EcrLog.WriteAckNack(((int)COMChars.NAK).ToString(), directionSending: true, LogMessageType.NAK, detailedLogging: false);
			}
			success = true;
			EcrConnectorV2.progress.Set();
		}
		catch (Exception e)
		{
			EcrLog.WriteException("in send NAK", e);
			success = false;
		}
	}

	internal void sendACK()
	{
		try
		{
			char[] array = new char[1] { COMChars.ACK };
			Write(array, 0, array.Length);
			if (Configuration.enableLogging)
			{
				EcrLog.WriteAckNack(((int)COMChars.ACK).ToString(), directionSending: true, LogMessageType.ACK, detailedLogging: false);
			}
			success = true;
			EcrConnectorV2.progress.Set();
		}
		catch (Exception e)
		{
			EcrLog.WriteException("in send ACK", e);
			success = false;
		}
	}

	internal int openPort()
	{
		try
		{
			Open();
			DiscardOutBuffer();
			DiscardInBuffer();
			return ResponseCodes.SUCCESS;
		}
		catch (Exception e)
		{
			EcrLog.WriteException("in opening serial port ", e);
			return ResponseCodes.COM_UNAVAILABLE;
		}
	}

	internal void closePort()
	{
		try
		{
			if (base.IsOpen)
			{
				Close();
			}
		}
		catch (Exception)
		{
		}
	}
}
