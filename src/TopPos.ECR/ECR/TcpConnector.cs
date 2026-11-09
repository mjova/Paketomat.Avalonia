using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ECR;

internal class TcpConnector
{
	private const int maxMessageLenth = 1024;

	internal static object readResult;

	internal static bool success;

	private string ipaddress;

	private TcpClient tcpClient = null;

	private NetworkStream tcpClientStream = null;

	public TcpConnector()
	{
		Console.WriteLine(Configuration.ipaddress);
		ipaddress = Configuration.ipaddress;
	}

	public TcpConnector(string ipaddress)
	{
		this.ipaddress = ipaddress;
	}

	internal void writeMessage(object data)
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		ECR2POSMessage_00V3 eCR2POSMessage_00V = (ECR2POSMessage_00V3)data;
		try
		{
			char[] array = eCR2POSMessage_00V.toCharArray();
			byte[] array2 = new byte[array.Length];
			int num = 0;
			char[] array3 = array;
			foreach (char value in array3)
			{
				array2[num++] = Convert.ToByte(value);
			}
			tcpClientStream.Write(array2, 0, array2.Length);
			if (Configuration.enableLogging)
			{
				EcrLog.Write(new string(array), directionSending: true, LogMessageType.MSG, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
			}
			success = true;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			EcrConnectorV3.progress.Set();
		}
		catch (Exception e)
		{
			EcrLog.WriteException("in tcp write message [general] : ", e, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			success = false;
		}
	}

	internal void writeCancelMessage(object data)
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		try
		{
			char[] array = (char[])data;
			byte[] array2 = new byte[array.Length];
			int num = 0;
			char[] array3 = array;
			foreach (char value in array3)
			{
				array2[num++] = Convert.ToByte(value);
			}
			tcpClientStream.Write(array2, 0, array2.Length);
			if (Configuration.enableLogging)
			{
				EcrLog.Write(new string(array), directionSending: true, LogMessageType.MSG, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
			}
			success = true;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			EcrConnectorV3.progress.Set();
		}
		catch (Exception e)
		{
			EcrLog.WriteException("in tcp write cancel message : ", e, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			success = false;
		}
	}

	internal void waitForAck()
	{
		readChar();
	}

	internal void readChar()
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		byte[] array = new byte[1];
		bool flag = true;
		EcrConnectorV3.timeout = false;
		try
		{
			DateTime now = DateTime.Now;
			while (flag)
			{
				tcpClient.ReceiveTimeout = 5000;
				if (tcpClient.Available > 0)
				{
					int num = tcpClientStream.ReadByte();
					if (num != -1)
					{
						readResult = num;
						break;
					}
				}
				DateTime now2 = DateTime.Now;
				long num2 = (long)(now2 - now).TotalMilliseconds;
				if (num2 > Configuration.waitForAckTimeout)
				{
					throw new TimeoutException("The operation has timed out. No reply from device");
				}
			}
			if (Configuration.enableLogging)
			{
				if ((int)readResult == 6)
				{
					EcrLog.WriteAckNack(readResult.ToString(), directionSending: false, LogMessageType.ACK, detailedLogging: false, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
				}
				else
				{
					EcrLog.WriteAckNack(readResult.ToString(), directionSending: false, LogMessageType.NAK, detailedLogging: false, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
				}
			}
			success = true;
			EcrConnectorV3.timeout = false;
			EcrConnectorV3.progress.Set();
		}
		catch (TimeoutException e)
		{
			EcrLog.WriteException("in tcp read char [timeout] : ", e, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
			success = false;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			EcrConnectorV3.timeout = true;
		}
		catch (Exception e2)
		{
			EcrLog.WriteException("in tcp read char [general] : ", e2, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
			success = false;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			EcrConnectorV3.timeout = false;
		}
	}

	internal void waitForMessage()
	{
		readMessage();
	}

	internal void readMessage()
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		bool flag = true;
		bool flag2 = false;
		try
		{
			int num = 0;
			List<char> list = new List<char>();
			tcpClient.ReceiveTimeout = 5000;
			DateTime now = DateTime.Now;
			while (flag)
			{
				if (!flag2)
				{
					if (tcpClient.Available > 0)
					{
						num = tcpClientStream.ReadByte();
						list.Add((char)num);
						if (num == 3)
						{
							flag2 = true;
						}
					}
				}
				else if (tcpClient.Available > 0)
				{
					char item = (char)tcpClientStream.ReadByte();
					list.Add(item);
					flag = false;
				}
				if (EcrConnectorV3.CancelTransactionFlag == 1)
				{
					EcrConnectorV3.CancelTransactionPerformed = true;
					EcrConnectorV3.CancelTransactionFlag = 9;
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
				EcrLog.Write(new string(list.ToArray()), directionSending: false, LogMessageType.MSG, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
			}
			readResult = list.ToArray();
			success = true;
			EcrConnectorV3.timeout = false;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			EcrConnectorV3.progress.Set();
		}
		catch (TimeoutException e)
		{
			EcrLog.WriteException("in tcp read message [timeout] : ", e, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			success = false;
			EcrConnectorV3.timeout = true;
		}
		catch (Exception e2)
		{
			EcrLog.WriteException("in tcp read message [general] : ", e2, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			success = false;
			EcrConnectorV3.timeout = false;
		}
	}

	internal void sendNAK()
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		try
		{
			tcpClientStream.WriteByte(Convert.ToByte(COMChars.NAK));
			if (Configuration.enableLogging)
			{
				EcrLog.WriteAckNack(((int)COMChars.NAK).ToString(), directionSending: true, LogMessageType.NAK, detailedLogging: false, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
			}
			success = true;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			EcrConnectorV3.progress.Set();
		}
		catch (Exception e)
		{
			EcrLog.WriteException("in tcp send NAK :", e, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			success = false;
		}
	}

	internal void sendACK()
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		try
		{
			tcpClientStream.WriteByte(Convert.ToByte(COMChars.ACK));
			if (Configuration.enableLogging)
			{
				EcrLog.WriteAckNack(((int)COMChars.ACK).ToString(), directionSending: true, LogMessageType.ACK, detailedLogging: false, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
			}
			success = true;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			EcrConnectorV3.progress.Set();
		}
		catch (Exception e)
		{
			EcrLog.WriteException("in tcp send ACK : ", e, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			success = false;
		}
	}

	internal void sendAs24()
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		char[] array = new char[17]
		{
			'\u0002', '2', '8', '0', '0', '0', '0', '0', '0', '1',
			'2', '3', '4', '5', '6', '\u0003', '\u000e'
		};
		try
		{
			byte[] array2 = new byte[array.Length];
			int num = 0;
			char[] array3 = array;
			foreach (char value in array3)
			{
				array2[num++] = Convert.ToByte(value);
			}
			tcpClientStream.Write(array2, 0, array2.Length);
			if (Configuration.enableLogging)
			{
				EcrLog.Write(new string(array), directionSending: true, LogMessageType.MSG, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
			}
			success = true;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			EcrConnectorV3.progress.Set();
		}
		catch (Exception e)
		{
			EcrLog.WriteException("in tcp write message As24 : ", e, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			success = false;
		}
	}

	public bool IsConnected()
	{
		return tcpClient.Connected;
	}

	internal int openPort()
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		int result = ResponseCodes.INVALID_INPUT;
		try
		{
			string[] array = ipaddress.Split(new string[1] { ":" }, StringSplitOptions.None);
			if (array.Length == 2 && IPAddress.TryParse(array[0], out var address) && int.TryParse(array[1], out var result2))
			{
				tcpClient = new TcpClient();
				tcpClient.Connect(address, result2);
				tcpClientStream = tcpClient.GetStream();
				EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
				result = ResponseCodes.SUCCESS;
			}
		}
		catch (ArgumentNullException e)
		{
			EcrLog.WriteException("in opening tcp socket [NullException] : ", e, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			result = ResponseCodes.INVALID_INPUT;
		}
		catch (ArgumentOutOfRangeException e2)
		{
			EcrLog.WriteException("in opening tcp socket [RangeException] : ", e2, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			result = ResponseCodes.INVALID_INPUT;
		}
		catch (Exception e3)
		{
			EcrLog.WriteException("in opening tcp socket [general] : ", e3, ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString());
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			result = ResponseCodes.POS_SOCKET_UNREACHABLE;
		}
		return result;
	}

	internal void closePort()
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		try
		{
			if (tcpClientStream != null)
			{
				tcpClientStream.Close();
				EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			}
		}
		catch (Exception)
		{
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		}
		try
		{
			if (tcpClient != null)
			{
				tcpClient.Close();
				EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			}
		}
		catch (Exception)
		{
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		}
	}

	internal void Dispose()
	{
	}
}
