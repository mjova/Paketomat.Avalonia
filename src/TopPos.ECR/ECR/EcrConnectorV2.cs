using System;
using System.IO.Ports;
using System.Threading;

namespace ECR;

internal class EcrConnectorV2
{
	public delegate void EcrConnectorMessageHandler(string messageIdentifier, string message);

	internal static AutoResetEvent progress;

	internal static bool timeout;

	internal static CommunicationPortV2 comPort;

	public event EcrConnectorMessageHandler EcrConnectorMessageReceived;

	internal int DoTransaction(ref TransParams tp)
	{
		if (!Configuration.isReadFRomFile)
		{
			return ResponseCodes.CFG_FILE_MISSING;
		}
		if (!ECR2POSMessage_00.isValid(tp))
		{
			return ResponseCodes.INVALID_INPUT;
		}
		try
		{
			comPort = new CommunicationPortV2();
		}
		catch (Exception)
		{
			comPort.Dispose();
			comPort = null;
			return ResponseCodes.COM_UNAVAILABLE;
		}
		return process(ref tp, comPort);
	}

	internal int DoTransaction(ref TransParams tp, string portName, int baudRate)
	{
		if (!Configuration.isReadFRomFile)
		{
			return ResponseCodes.CFG_FILE_MISSING;
		}
		if (!ECR2POSMessage_00.isValid(tp))
		{
			return ResponseCodes.INVALID_INPUT;
		}
		try
		{
			comPort = new CommunicationPortV2(portName, baudRate);
		}
		catch (Exception)
		{
			comPort.Dispose();
			comPort = null;
			return ResponseCodes.COM_UNAVAILABLE;
		}
		return process(ref tp, comPort);
	}

	internal int DoTransaction(ref TransParams tp, string portName, int baudRate, int dataBits, Parity parity, StopBits stopBits)
	{
		if (!Configuration.isReadFRomFile)
		{
			return ResponseCodes.CFG_FILE_MISSING;
		}
		if (!ECR2POSMessage_00.isValid(tp))
		{
			return ResponseCodes.INVALID_INPUT;
		}
		try
		{
			comPort = new CommunicationPortV2(portName, baudRate, dataBits, parity, stopBits);
		}
		catch (Exception)
		{
			comPort.Dispose();
			comPort = null;
			return ResponseCodes.COM_UNAVAILABLE;
		}
		return process(ref tp, comPort);
	}

	internal int process(ref TransParams tp, CommunicationPortV2 comPort)
	{
		int num = 0;
		progress = new AutoResetEvent(initialState: false);
		timeout = false;
		ECR2POSMessage_00 data = ECR2POSMessage_00.create(tp);
		int num2 = comPort.openPort();
		if (num2 != ResponseCodes.SUCCESS)
		{
			comPort.closePort();
			comPort.Dispose();
			comPort = null;
			return ResponseCodes.COM_UNAVAILABLE;
		}
		while (true)
		{
			comPort.writeMessage(data);
			if (!CommunicationPortV2.success)
			{
				comPort.closePort();
				comPort = null;
				return ResponseCodes.COM_UNAVAILABLE;
			}
			comPort.waitForAck();
			if (!CommunicationPortV2.success && !timeout)
			{
				comPort.closePort();
				comPort = null;
				return ResponseCodes.COM_UNAVAILABLE;
			}
			if (!CommunicationPortV2.success && timeout)
			{
				comPort.closePort();
				comPort.Dispose();
				comPort = null;
				return ResponseCodes.FUNCTION_TIMEOUT;
			}
			progress.Reset();
			char obj;
			try
			{
				obj = ((CommunicationPortV2.readResult != null) ? ((char)(int)CommunicationPortV2.readResult) : '\0');
			}
			catch (Exception)
			{
				comPort.closePort();
				comPort.Dispose();
				comPort = null;
				return ResponseCodes.POS_ERROR;
			}
			if (!timeout && CommunicationPortV2.readResult != null && COMChars.ACK.Equals(obj))
			{
				break;
			}
			num++;
			if (num != 3)
			{
				continue;
			}
			comPort.closePort();
			comPort = null;
			return ResponseCodes.NO_ACK_OR_NACK;
		}
		while (true)
		{
			comPort.waitForMessage();
			if (!CommunicationPortV2.success && !timeout)
			{
				comPort.closePort();
				comPort = null;
				return ResponseCodes.COM_UNAVAILABLE;
			}
			progress.Reset();
			if (timeout)
			{
				comPort.closePort();
				comPort = null;
				return ResponseCodes.FUNCTION_TIMEOUT;
			}
			char[] received = (char[])CommunicationPortV2.readResult;
			POS2ECRMessage pOS2ECRMessage = POS2ECRMessage.create(received);
			if (!pOS2ECRMessage.isValid())
			{
				comPort.sendNAK();
				if (!CommunicationPortV2.success)
				{
					comPort.closePort();
					comPort = null;
					return ResponseCodes.COM_UNAVAILABLE;
				}
				continue;
			}
			comPort.sendACK();
			if (!CommunicationPortV2.success)
			{
				comPort.closePort();
				comPort = null;
				return ResponseCodes.COM_UNAVAILABLE;
			}
			if (pOS2ECRMessage.isHold())
			{
				if (this.EcrConnectorMessageReceived != null)
				{
					POS2ECRMessage_20 pOS2ECRMessage_ = (POS2ECRMessage_20)pOS2ECRMessage;
					this.EcrConnectorMessageReceived(pOS2ECRMessage_.identifier, pOS2ECRMessage_.displayMessage);
				}
				continue;
			}
			if (pOS2ECRMessage.isError())
			{
				if (this.EcrConnectorMessageReceived != null)
				{
					POS2ECRMessage_22 pOS2ECRMessage_2 = (POS2ECRMessage_22)pOS2ECRMessage;
					this.EcrConnectorMessageReceived(pOS2ECRMessage_2.identifier, pOS2ECRMessage_2.displayMessage);
				}
				comPort.closePort();
				comPort = null;
				return ResponseCodes.POS_ERROR;
			}
			((POS2ECRMessage_10)pOS2ECRMessage).updateTransactionParams(ref tp);
			if (!"1".Equals(((POS2ECRMessage_10)pOS2ECRMessage).moreMessagesFlag))
			{
				break;
			}
		}
		comPort.closePort();
		comPort = null;
		return ResponseCodes.SUCCESS;
	}
}
