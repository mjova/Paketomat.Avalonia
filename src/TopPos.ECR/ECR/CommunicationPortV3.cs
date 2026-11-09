using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ECR;

internal class CommunicationPortV3 : SerialPort
{
	private const int maxMessageLenth = 4096;

	internal static object readResult;

	internal static bool success;

	internal string GetComPortNameFor(string VID, string PID)
	{
		string pattern = $"^VID_{VID}.PID_{PID}";
		Regex regex = new Regex(pattern, RegexOptions.IgnoreCase);
		List<string> list = new List<string>();
		RegistryKey localMachine = Registry.LocalMachine;
		RegistryKey registryKey = localMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum");
		string[] subKeyNames = registryKey.GetSubKeyNames();
		foreach (string name in subKeyNames)
		{
			RegistryKey registryKey2 = registryKey.OpenSubKey(name);
			string[] subKeyNames2 = registryKey2.GetSubKeyNames();
			foreach (string text in subKeyNames2)
			{
				if (regex.Match(text).Success)
				{
					RegistryKey registryKey3 = registryKey2.OpenSubKey(text);
					string[] subKeyNames3 = registryKey3.GetSubKeyNames();
					foreach (string name2 in subKeyNames3)
					{
						RegistryKey registryKey4 = registryKey3.OpenSubKey(name2);
						RegistryKey registryKey5 = registryKey4.OpenSubKey("Device Parameters");
						list.Add((string)registryKey5.GetValue("PortName"));
					}
				}
			}
		}
		string[] availableSerialPortsForThisComputer = EcrPort.getAvailableSerialPortsForThisComputer();
		List<string> second = new List<string>(availableSerialPortsForThisComputer);
		IEnumerable<string> source = list.Intersect(second);
		if (source.Count() == 1)
		{
			EcrLog.Write("Pronadjen uredjaj [ " + $"VID_{VID}.PID_{PID}" + " ] na portu [ " + source.First() + " ]", directionSending: true, LogMessageType.MSG, source.First());
			return source.First();
		}
		EcrLog.Write("Pronadjeno više uređaja [ " + $"VID_{VID}.PID_{PID}" + " ]", directionSending: true, LogMessageType.MSG, "FALSE");
		return "FALSE";
	}

	public CommunicationPortV3()
	{
		if (Configuration.deviceVendor == "Castles")
		{
			string comPortNameFor = GetComPortNameFor(Configuration.deviceVid, Configuration.devicePid);
			if (comPortNameFor == "FALSE")
			{
				EcrLog.Write("Greska prilikom setovanja komunikacionog porta.", directionSending: true, LogMessageType.MSG, "FALSE");
				base.PortName = "COM_UNAVAILABLE";
			}
			else
			{
				EcrLog.Write("Uspesno setovan port [ " + comPortNameFor + " ].", directionSending: true, LogMessageType.MSG, comPortNameFor);
				base.PortName = comPortNameFor;
			}
		}
		else
		{
			base.PortName = Configuration.portName;
		}
		base.BaudRate = Configuration.baudRate;
		base.DataBits = Configuration.dataBits;
		base.Parity = (Parity)Enum.Parse(typeof(Parity), Configuration.parity.ToString());
		base.StopBits = (StopBits)Enum.Parse(typeof(StopBits), Configuration.stopBits.ToString());
	}

	public CommunicationPortV3(string portName, int baudRate, int dataBits, Parity parity, StopBits stopBits)
	{
		base.PortName = portName;
		base.BaudRate = baudRate;
		base.DataBits = dataBits;
		base.Parity = parity;
		base.StopBits = stopBits;
	}

	public CommunicationPortV3(string portName, int baudRate)
	{
		base.PortName = portName;
		base.BaudRate = baudRate;
		base.DataBits = 8;
		base.Parity = Parity.None;
		base.StopBits = StopBits.One;
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
			Write(array2, 0, array2.Length);
			if (Configuration.enableLogging)
			{
				EcrLog.Write(new string(array), directionSending: true, LogMessageType.MSG, base.PortName);
			}
			success = true;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			EcrConnectorV3.progress.Set();
		}
		catch (Exception e)
		{
			EcrLog.WriteException("in write message [general] : ", e, base.PortName);
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			closePort();
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
			Write(array2, 0, array2.Length);
			if (Configuration.enableLogging)
			{
				EcrLog.Write(new string(array), directionSending: true, LogMessageType.MSG, base.PortName);
			}
			success = true;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			EcrConnectorV3.progress.Set();
		}
		catch (Exception e)
		{
			EcrLog.WriteException("in write cancel message : ", e, base.PortName);
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			closePort();
			success = false;
		}
	}

	internal void writeSpecificMessage(object data)
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
			Write(array2, 0, array2.Length);
			if (Configuration.enableLogging)
			{
				EcrLog.Write(new string(array), directionSending: true, LogMessageType.MSG, base.PortName);
			}
			success = true;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			EcrConnectorV3.progress.Set();
		}
		catch (Exception e)
		{
			EcrLog.WriteException("in write cancel message : ", e, base.PortName);
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			closePort();
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
		bool flag = true;
		EcrConnectorV3.timeout = false;
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
					EcrLog.WriteAckNack(readResult.ToString(), directionSending: false, LogMessageType.ACK, detailedLogging: false, base.PortName);
				}
				else
				{
					EcrLog.WriteAckNack(readResult.ToString(), directionSending: false, LogMessageType.NAK, detailedLogging: false, base.PortName);
				}
			}
			success = true;
			EcrConnectorV3.timeout = false;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			EcrConnectorV3.progress.Set();
		}
		catch (TimeoutException e)
		{
			EcrLog.WriteException("in read char [timeout] :", e, base.PortName);
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			success = false;
			closePort();
			EcrConnectorV3.timeout = true;
		}
		catch (Exception e2)
		{
			EcrLog.WriteException("in read char [general] : ", e2, base.PortName);
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			success = false;
			closePort();
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
			DateTime now = DateTime.Now;
			while (flag)
			{
				if (!flag2)
				{
					if (base.BytesToRead > 0)
					{
						num = ReadByte();
						list.Add((char)num);
						if (num == 3)
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
				EcrLog.Write(new string(list.ToArray()), directionSending: false, LogMessageType.MSG, base.PortName);
			}
			readResult = list.ToArray();
			success = true;
			EcrConnectorV3.timeout = false;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			EcrConnectorV3.progress.Set();
		}
		catch (TimeoutException e)
		{
			EcrLog.WriteException("in read message [timeout] : ", e, base.PortName);
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			success = false;
			closePort();
			EcrConnectorV3.timeout = true;
		}
		catch (Exception e2)
		{
			EcrLog.WriteException("in read message [general] : ", e2, base.PortName);
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			success = false;
			closePort();
			EcrConnectorV3.timeout = false;
		}
	}

	internal void sendNAK()
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		try
		{
			char[] array = new char[1] { COMChars.NAK };
			Write(array, 0, array.Length);
			if (Configuration.enableLogging)
			{
				EcrLog.WriteAckNack(((int)COMChars.NAK).ToString(), directionSending: true, LogMessageType.NAK, detailedLogging: false, base.PortName);
			}
			success = true;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			EcrConnectorV3.progress.Set();
		}
		catch (Exception e)
		{
			EcrLog.WriteException("in send NAK : ", e, base.PortName);
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			closePort();
			success = false;
		}
	}

	internal void sendACK()
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		try
		{
			char[] array = new char[1] { COMChars.ACK };
			Write(array, 0, array.Length);
			if (Configuration.enableLogging)
			{
				EcrLog.WriteAckNack(((int)COMChars.ACK).ToString(), directionSending: true, LogMessageType.ACK, detailedLogging: false, base.PortName);
			}
			success = true;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			EcrConnectorV3.progress.Set();
		}
		catch (Exception e)
		{
			EcrLog.WriteException("in send ACK : ", e, base.PortName);
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			closePort();
			success = false;
		}
	}

	internal void sendAs24()
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		try
		{
			char[] array = new char[17]
			{
				'\u0002', '2', '8', '0', '0', '0', '0', '0', '0', '1',
				'2', '3', '4', '5', '6', '\u0003', '\u000e'
			};
			Write(array, 0, array.Length);
			if (Configuration.enableLogging)
			{
				EcrLog.Write(new string(array), directionSending: true, LogMessageType.MSG, base.PortName);
			}
			success = true;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			EcrConnectorV3.progress.Set();
		}
		catch (Exception e)
		{
			EcrLog.WriteException("in send As24 [general] : ", e, base.PortName);
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			closePort();
			success = false;
		}
	}

	internal int openPort()
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		try
		{
			Open();
			DiscardOutBuffer();
			DiscardInBuffer();
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			return ResponseCodes.SUCCESS;
		}
		catch (Exception e)
		{
			EcrLog.WriteException("in opening serial port : ", e, base.PortName);
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			return ResponseCodes.COM_UNAVAILABLE;
		}
	}

	internal void closePort()
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		try
		{
			if (base.IsOpen)
			{
				Close();
			}
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		}
		catch (Exception e)
		{
			EcrLog.WriteException("in closing serial port : ", e, base.PortName);
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		}
	}
}
