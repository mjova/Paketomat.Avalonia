using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace ECR;

internal class EcrConnectorV3
{
	public delegate void EcrConnectorMessageHandler(string messageIdentifier, string message);

	public delegate void EcrConnectorExtendedMessageHandler(string messageIdentifier, string code, string message);

	public delegate void EcrConnectorAs24MessageHandler(string messageIdentifier, string track2data, string message, string bfoc);

	public delegate void EcrConnectorFunctionMessageHandler(string messageIdentifier, string code, string message);

	public delegate void EcrConnectorRawdataHandler(EcrPort.DataDirection direction, byte[] rawdata);

	internal static AutoResetEvent progress;

	internal static bool timeout;

	internal static CommunicationPortV3 comPort;

	internal static TcpConnector comPortTcp;

	private int terminalResponseCount = 1;

	internal static int CancelTransactionFlag;

	internal static bool CancelTransactionPerformed;

	internal static string HoldMessageCode;

	public event EcrConnectorMessageHandler EcrConnectorMessageReceived;

	public event EcrConnectorExtendedMessageHandler EcrConnectorExtendedMessageReceived;

	public event EcrConnectorAs24MessageHandler EcrConnectorAs24MessageReceived;

	public event EcrConnectorFunctionMessageHandler EcrConnectorFunctionMessageReceived;

	public event EcrConnectorRawdataHandler EcrConnectorRawdataEvent;

	private static char[] ReturnCancelCommand()
	{
		List<char> list = new List<char>();
		list.Add(COMChars.STX);
		list.Add(COMChars.STX);
		list.Add('2');
		list.Add('3');
		list.Add('0');
		list.Add('0');
		list.Add('0');
		list.Add('0');
		list.Add('0');
		list.Add('0');
		list.Add(COMChars.ETX);
		char item = LRC.calculate(list.GetRange(1, list.Count - 1).ToArray());
		list.Add(item);
		return list.ToArray();
	}

	private static char[] ReturnAllowTransaction()
	{
		List<char> list = new List<char>();
		list.Add(COMChars.STX);
		list.Add(COMChars.STX);
		list.Add('3');
		list.Add('6');
		list.Add('0');
		list.Add('0');
		list.Add('0');
		list.Add('0');
		list.Add('0');
		list.Add('0');
		list.Add('1');
		list.Add('\u001c');
		list.Add('\u001c');
		list.Add('\u001c');
		list.Add(COMChars.ETX);
		char item = LRC.calculate(list.GetRange(1, list.Count - 1).ToArray());
		list.Add(item);
		return list.ToArray();
	}

	private static char[] ReturnDeclineTransaction()
	{
		List<char> list = new List<char>();
		list.Add(COMChars.STX);
		list.Add(COMChars.STX);
		list.Add('3');
		list.Add('6');
		list.Add('0');
		list.Add('0');
		list.Add('0');
		list.Add('0');
		list.Add('0');
		list.Add('0');
		list.Add('0');
		list.Add('\u001c');
		list.Add('\u001c');
		list.Add('\u001c');
		list.Add(COMChars.ETX);
		char item = LRC.calculate(list.GetRange(1, list.Count - 1).ToArray());
		list.Add(item);
		return list.ToArray();
	}

	internal int DoTransaction(ref TransParamsV3 tp)
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		if (!Configuration.isReadFRomFile)
		{
			return ResponseCodes.CFG_FILE_MISSING;
		}
		if (!ECR2POSMessage_00V3.isValid(tp))
		{
			return ResponseCodes.INVALID_INPUT;
		}
		try
		{
			comPort = new CommunicationPortV3();
		}
		catch (Exception e)
		{
			comPort.Dispose();
			comPort = null;
			EcrLog.WriteException("in write message DoTransaction(config) : ", e);
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			return ResponseCodes.COM_UNAVAILABLE;
		}
		EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		return process(ref tp, comPort);
	}

	internal int DoTransaction(ref TransParamsV3 tp, string portName, int baudRate)
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		return DoTransaction(ref tp, portName, baudRate, 8, Parity.None, StopBits.One);
	}

	internal int DoTransaction(ref TransParamsV3 tp, string portName, int baudRate, int dataBits, Parity parity, StopBits stopBits)
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		if (!Configuration.isReadFRomFile)
		{
			return ResponseCodes.CFG_FILE_MISSING;
		}
		if (!ECR2POSMessage_00V3.isValid(tp))
		{
			return ResponseCodes.INVALID_INPUT;
		}
		try
		{
			comPort = new CommunicationPortV3(portName, baudRate, dataBits, parity, stopBits);
		}
		catch (Exception e)
		{
			comPort.Dispose();
			comPort = null;
			EcrLog.WriteException("in write message DoTransaction(full) : ", e);
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			return ResponseCodes.COM_UNAVAILABLE;
		}
		EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		return process(ref tp, comPort);
	}

	internal int DoTransactionTcp(ref TransParamsV3 tp)
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		if (!Configuration.isReadFRomFile)
		{
			return ResponseCodes.CFG_FILE_MISSING;
		}
		if (!ECR2POSMessage_00V3.isValid(tp))
		{
			return ResponseCodes.INVALID_INPUT;
		}
		try
		{
			comPortTcp = new TcpConnector();
		}
		catch (Exception e)
		{
			comPort.Dispose();
			comPort = null;
			EcrLog.WriteException("in write message DoTransactionTcp(config) : ", e);
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			return ResponseCodes.COM_UNAVAILABLE;
		}
		EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		return process(ref tp, comPortTcp);
	}

	internal int DoTransactionTcp(ref TransParamsV3 tp, string ipaddress)
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		if (!Configuration.isReadFRomFile)
		{
			return ResponseCodes.CFG_FILE_MISSING;
		}
		if (!ECR2POSMessage_00V3.isValid(tp))
		{
			return ResponseCodes.INVALID_INPUT;
		}
		try
		{
			comPortTcp = new TcpConnector(ipaddress);
		}
		catch (Exception e)
		{
			comPort.Dispose();
			comPort = null;
			EcrLog.WriteException("in write message DoTransactionTcp(full) : ", e);
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			return ResponseCodes.COM_UNAVAILABLE;
		}
		EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		return process(ref tp, comPortTcp);
	}

	internal int process(ref TransParamsV3 tp, CommunicationPortV3 comPort)
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		int num = 0;
		progress = new AutoResetEvent(initialState: false);
		timeout = false;
		HoldMessageCode = null;
		CancelTransactionFlag = 0;
		CancelTransactionPerformed = false;
		ECR2POSMessage_00V3 eCR2POSMessage_00V = ECR2POSMessage_00V3.create(tp);
		int num2 = comPort.openPort();
		if (num2 != ResponseCodes.SUCCESS)
		{
			comPort.closePort();
			comPort.Dispose();
			comPort = null;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			return ResponseCodes.COM_UNAVAILABLE;
		}
		while (true)
		{
			if (CancelTransactionPerformed)
			{
				comPort.writeCancelMessage(ReturnCancelCommand());
			}
			else if (CancelTransactionFlag != 9)
			{
				comPort.writeMessage(eCR2POSMessage_00V);
			}
			if (!CommunicationPortV3.success)
			{
				comPort.closePort();
				comPort = null;
				EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
				return ResponseCodes.COM_UNAVAILABLE;
			}
			if (EcrPort.EnableRealtimeRawdataDebug)
			{
				if (CancelTransactionPerformed)
				{
					this.EcrConnectorRawdataEvent(EcrPort.DataDirection.ECR2POS, Encoding.GetEncoding("ISO-8859-2").GetBytes(ReturnCancelCommand()));
				}
				else if (CancelTransactionFlag != 9)
				{
					this.EcrConnectorRawdataEvent(EcrPort.DataDirection.ECR2POS, eCR2POSMessage_00V.toByteArray());
				}
			}
			comPort.waitForAck();
			if (!CommunicationPortV3.success && !timeout)
			{
				comPort.closePort();
				comPort = null;
				EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
				return ResponseCodes.COM_UNAVAILABLE;
			}
			if (!CommunicationPortV3.success && timeout)
			{
				num++;
			}
			progress.Reset();
			char c;
			try
			{
				c = ((CommunicationPortV3.readResult != null) ? ((char)(int)CommunicationPortV3.readResult) : '\0');
				if (EcrPort.EnableRealtimeRawdataDebug && CommunicationPortV3.readResult != null)
				{
					this.EcrConnectorRawdataEvent(EcrPort.DataDirection.POS2ECR, new byte[1] { Convert.ToByte(c) });
				}
			}
			catch (Exception)
			{
				comPort.closePort();
				comPort.Dispose();
				comPort = null;
				EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
				return ResponseCodes.POS_ERROR;
			}
			if (timeout || CommunicationPortV3.readResult == null || !COMChars.ACK.Equals(c))
			{
				if (num == 3)
				{
					break;
				}
				continue;
			}
			while (true)
			{
				CancelTransactionPerformed = false;
				comPort.waitForMessage();
				if (CancelTransactionPerformed)
				{
					break;
				}
				if (!CommunicationPortV3.success && !timeout)
				{
					comPort.closePort();
					comPort = null;
					EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
					return ResponseCodes.COM_UNAVAILABLE;
				}
				progress.Reset();
				if (timeout)
				{
					comPort.closePort();
					comPort = null;
					EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
					return ResponseCodes.FUNCTION_TIMEOUT;
				}
				char[] array = (char[])CommunicationPortV3.readResult;
				POS2ECRMessageV3 pOS2ECRMessageV = POS2ECRMessageV3.create(array);
				if (EcrPort.EnableRealtimeRawdataDebug)
				{
					int num3 = 0;
					byte[] array2 = new byte[array.Length];
					char[] array3 = array;
					foreach (char value in array3)
					{
						try
						{
							array2[num3++] = Convert.ToByte(value);
						}
						catch (OverflowException)
						{
						}
					}
					this.EcrConnectorRawdataEvent(EcrPort.DataDirection.POS2ECR, array2);
				}
				if (!pOS2ECRMessageV.isValid())
				{
					comPort.sendNAK();
					if (EcrPort.EnableRealtimeRawdataDebug)
					{
						this.EcrConnectorRawdataEvent(EcrPort.DataDirection.ECR2POS, new byte[1] { Convert.ToByte(COMChars.NAK) });
					}
					if (!CommunicationPortV3.success)
					{
						comPort.closePort();
						comPort = null;
						EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
						return ResponseCodes.COM_UNAVAILABLE;
					}
					continue;
				}
				comPort.sendACK();
				if (!CommunicationPortV3.success)
				{
					comPort.closePort();
					comPort = null;
					EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
					return ResponseCodes.COM_UNAVAILABLE;
				}
				if (EcrPort.EnableRealtimeRawdataDebug)
				{
					this.EcrConnectorRawdataEvent(EcrPort.DataDirection.ECR2POS, new byte[1] { Convert.ToByte(COMChars.ACK) });
				}
				if (pOS2ECRMessageV.isHold())
				{
					if (this.EcrConnectorMessageReceived != null)
					{
						POS2ECRMessage_20V3 pOS2ECRMessage_20V = (POS2ECRMessage_20V3)pOS2ECRMessageV;
						this.EcrConnectorMessageReceived(pOS2ECRMessage_20V.identifier, pOS2ECRMessage_20V.displayMessage);
					}
					continue;
				}
				if (pOS2ECRMessageV.isError())
				{
					if (this.EcrConnectorMessageReceived != null)
					{
						POS2ECRMessage_22V3 pOS2ECRMessage_22V = (POS2ECRMessage_22V3)pOS2ECRMessageV;
						this.EcrConnectorMessageReceived(pOS2ECRMessage_22V.identifier, pOS2ECRMessage_22V.displayMessage);
					}
					comPort.closePort();
					comPort = null;
					EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
					return ResponseCodes.POS_ERROR;
				}
				if (pOS2ECRMessageV.isHoldWithCode())
				{
					if (this.EcrConnectorExtendedMessageReceived != null)
					{
						POS2ECRMessage_25V3 pOS2ECRMessage_25V = (POS2ECRMessage_25V3)pOS2ECRMessageV;
						this.EcrConnectorExtendedMessageReceived(pOS2ECRMessage_25V.identifier, pOS2ECRMessage_25V.code, pOS2ECRMessage_25V.displayMessage);
						if (pOS2ECRMessage_25V.code == "54")
						{
							HoldMessageCode = pOS2ECRMessage_25V.code;
						}
					}
					continue;
				}
				if (pOS2ECRMessageV.isErrorWithCode())
				{
					if (this.EcrConnectorExtendedMessageReceived != null)
					{
						POS2ECRMessage_26V3 pOS2ECRMessage_26V = (POS2ECRMessage_26V3)pOS2ECRMessageV;
						this.EcrConnectorExtendedMessageReceived(pOS2ECRMessage_26V.identifier, pOS2ECRMessage_26V.code, pOS2ECRMessage_26V.displayMessage);
					}
					comPort.closePort();
					comPort = null;
					EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
					return ResponseCodes.POS_ERROR;
				}
				if (pOS2ECRMessageV.isAs24())
				{
					if (this.EcrConnectorAs24MessageReceived != null)
					{
						POS2ECRMessage_27V3 pOS2ECRMessage_27V = (POS2ECRMessage_27V3)pOS2ECRMessageV;
						this.EcrConnectorAs24MessageReceived(pOS2ECRMessage_27V.identifier, pOS2ECRMessage_27V.track2data, pOS2ECRMessage_27V.displayMessage, pOS2ECRMessage_27V.bfoc);
						comPort.sendAs24();
						if (EcrPort.EnableRealtimeRawdataDebug)
						{
							byte[] rawdata = new byte[17]
							{
								2, 50, 56, 48, 48, 48, 48, 48, 48, 49,
								50, 51, 52, 53, 54, 3, 14
							};
							this.EcrConnectorRawdataEvent(EcrPort.DataDirection.ECR2POS, rawdata);
						}
					}
					continue;
				}
				if (pOS2ECRMessageV.isCancel())
				{
					if (this.EcrConnectorMessageReceived != null)
					{
						POS2ECRMessage_24V3 pOS2ECRMessage_24V = (POS2ECRMessage_24V3)pOS2ECRMessageV;
						this.EcrConnectorMessageReceived(pOS2ECRMessage_24V.identifier, pOS2ECRMessage_24V.displayMessage);
					}
					comPort.closePort();
					comPort = null;
					EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
					CancelTransactionFlag = 0;
					return ResponseCodes.TRANSACTION_CANCELED;
				}
				if (pOS2ECRMessageV.isFunctionMessage())
				{
					if (this.EcrConnectorMessageReceived != null)
					{
						POS2ECRMessage_32V3 pOS2ECRMessage_32V = (POS2ECRMessage_32V3)pOS2ECRMessageV;
						this.EcrConnectorFunctionMessageReceived(pOS2ECRMessage_32V.identifier, pOS2ECRMessage_32V.code, pOS2ECRMessage_32V.displayMessage);
					}
					comPort.closePort();
					comPort = null;
					EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
					return ResponseCodes.SUCCESS;
				}
				if (pOS2ECRMessageV.isSpecificMessage())
				{
					if (this.EcrConnectorExtendedMessageReceived != null)
					{
						POS2ECRMessage_35V3 pOS2ECRMessage_35V = (POS2ECRMessage_35V3)pOS2ECRMessageV;
						this.EcrConnectorExtendedMessageReceived(pOS2ECRMessage_35V.identifier, pOS2ECRMessage_35V.pan, pOS2ECRMessage_35V.cardReadType);
						if (pOS2ECRMessage_35V.pan != "")
						{
							if (tp.InsurancePolicyNumber == pOS2ECRMessage_35V.pan.Substring(0, 6))
							{
								string inputLabel = tp.InputLabel;
								string text = inputLabel;
								if (!(text == "1"))
								{
									if (text == "0")
									{
										comPort.writeSpecificMessage(ReturnDeclineTransaction());
										comPort.waitForAck();
										if (EcrPort.EnableRealtimeRawdataDebug)
										{
											byte[] rawdata2 = new byte[16]
											{
												2, 50, 51, 54, 48, 48, 48, 48, 48, 48,
												48, 28, 28, 28, 3, 26
											};
											this.EcrConnectorRawdataEvent(EcrPort.DataDirection.ECR2POS, rawdata2);
										}
										EcrLog.WriteFunction("### Declined transaction, transaction not allowed for this card! ###");
									}
								}
								else
								{
									comPort.writeSpecificMessage(ReturnAllowTransaction());
									comPort.waitForAck();
									if (EcrPort.EnableRealtimeRawdataDebug)
									{
										byte[] rawdata3 = new byte[16]
										{
											2, 50, 51, 54, 48, 48, 48, 48, 48, 48,
											49, 28, 28, 28, 3, 43
										};
										this.EcrConnectorRawdataEvent(EcrPort.DataDirection.ECR2POS, rawdata3);
									}
									EcrLog.WriteFunction("### Allowed transaction, transaction has allowed for this card! ###");
								}
								continue;
							}
							tp.InputLabel = string.Empty;
							if (tp.InsurancePolicyNumber == "")
							{
								comPort.writeSpecificMessage(ReturnAllowTransaction());
								comPort.waitForAck();
								if (EcrPort.EnableRealtimeRawdataDebug)
								{
									byte[] rawdata4 = new byte[16]
									{
										2, 50, 51, 54, 48, 48, 48, 48, 48, 48,
										49, 28, 28, 28, 3, 43
									};
									this.EcrConnectorRawdataEvent(EcrPort.DataDirection.ECR2POS, rawdata4);
								}
								EcrLog.WriteFunction("### Allowed transaction, there is no condition! ###");
							}
							else
							{
								comPort.writeSpecificMessage(ReturnDeclineTransaction());
								comPort.waitForAck();
								if (EcrPort.EnableRealtimeRawdataDebug)
								{
									byte[] rawdata5 = new byte[16]
									{
										2, 50, 51, 54, 48, 48, 48, 48, 48, 48,
										48, 28, 28, 28, 3, 26
									};
									this.EcrConnectorRawdataEvent(EcrPort.DataDirection.ECR2POS, rawdata5);
								}
								EcrLog.WriteFunction("### Declined transaction, sent and read data does not aligned! ###");
							}
						}
						else
						{
							comPort.writeSpecificMessage(ReturnAllowTransaction());
							comPort.waitForAck();
							if (EcrPort.EnableRealtimeRawdataDebug)
							{
								byte[] rawdata6 = new byte[16]
								{
									2, 50, 51, 54, 48, 48, 48, 48, 48, 48,
									49, 28, 28, 28, 3, 43
								};
								this.EcrConnectorRawdataEvent(EcrPort.DataDirection.ECR2POS, rawdata6);
							}
							EcrLog.WriteFunction("### Allowed transaction, there is no condition! ###");
						}
						continue;
					}
					comPort.closePort();
					comPort = null;
					EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
					return ResponseCodes.POS_ERROR;
				}
				POS2ECRMessage_10V3 pOS2ECRMessage_10V = (POS2ECRMessage_10V3)pOS2ECRMessageV;
				pOS2ECRMessage_10V.updateTransactionParams(ref tp);
				if ("1".Equals(pOS2ECRMessage_10V.moreMessagesFlag))
				{
					if (Convert.ToInt16(pOS2ECRMessage_10V.transactionType) >= 30 && Convert.ToInt16(pOS2ECRMessage_10V.transactionType) < 35)
					{
						tp.addTotalItem(pOS2ECRMessage_10V.getCurrentTotal());
						tp.addTotalNewItem(pOS2ECRMessage_10V.getCurrentTotalNew());
					}
					else if (Convert.ToInt16(pOS2ECRMessage_10V.transactionType) > 0 && Convert.ToInt16(pOS2ECRMessage_10V.transactionType) < 30)
					{
						tp.addTransItem(pOS2ECRMessage_10V.getCurrentTrans());
					}
					terminalResponseCount++;
					continue;
				}
				if ("0".Equals(pOS2ECRMessage_10V.moreMessagesFlag) && terminalResponseCount > 1)
				{
					if (Convert.ToInt16(pOS2ECRMessage_10V.transactionType) >= 30 && Convert.ToInt16(pOS2ECRMessage_10V.transactionType) < 35)
					{
						tp.addTotalItem(pOS2ECRMessage_10V.getCurrentTotal());
						tp.addTotalNewItem(pOS2ECRMessage_10V.getCurrentTotalNew());
					}
					else if (Convert.ToInt16(pOS2ECRMessage_10V.transactionType) > 0 && Convert.ToInt16(pOS2ECRMessage_10V.transactionType) < 30)
					{
						tp.addTransItem(pOS2ECRMessage_10V.getCurrentTrans());
					}
					tp.returnTicket();
					comPort.closePort();
					comPort = null;
					EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
					return ResponseCodes.SUCCESS;
				}
				tp.returnTicket();
				comPort.closePort();
				comPort = null;
				EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
				return ResponseCodes.SUCCESS;
			}
		}
		comPort.closePort();
		comPort = null;
		EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		return ResponseCodes.NO_ACK_OR_NACK;
	}

	internal int process(ref TransParamsV3 tp, TcpConnector comPort)
	{
		EcrLog.WriteFunction("### Entering " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		int num = 0;
		progress = new AutoResetEvent(initialState: false);
		timeout = false;
		HoldMessageCode = null;
		CancelTransactionFlag = 0;
		CancelTransactionPerformed = false;
		HoldMessageCode = "0";
		ECR2POSMessage_00V3 eCR2POSMessage_00V = ECR2POSMessage_00V3.create(tp);
		int num2 = comPort.openPort();
		if (num2 != ResponseCodes.SUCCESS)
		{
			comPort.closePort();
			comPort.Dispose();
			comPort = null;
			EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
			return num2;
		}
		while (true)
		{
			if (CancelTransactionPerformed)
			{
				comPort.writeCancelMessage(ReturnCancelCommand());
			}
			else if (CancelTransactionFlag != 9)
			{
				comPort.writeMessage(eCR2POSMessage_00V);
			}
			if (!TcpConnector.success)
			{
				comPort.closePort();
				comPort = null;
				EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
				return ResponseCodes.POS_SOCKET_UNREACHABLE;
			}
			if (EcrPort.EnableRealtimeRawdataDebug)
			{
				if (CancelTransactionPerformed)
				{
					this.EcrConnectorRawdataEvent(EcrPort.DataDirection.ECR2POS, Encoding.GetEncoding("ISO-8859-2").GetBytes(ReturnCancelCommand()));
				}
				else if (CancelTransactionFlag != 9)
				{
					this.EcrConnectorRawdataEvent(EcrPort.DataDirection.ECR2POS, eCR2POSMessage_00V.toByteArray());
				}
			}
			comPort.waitForAck();
			if (!TcpConnector.success && !timeout)
			{
				comPort.closePort();
				comPort = null;
				EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
				return ResponseCodes.POS_SOCKET_UNREACHABLE;
			}
			if (!TcpConnector.success && timeout)
			{
				comPort.closePort();
				comPort.Dispose();
				comPort = null;
				EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
				return ResponseCodes.NO_ACK_OR_NACK;
			}
			progress.Reset();
			char c;
			try
			{
				c = ((TcpConnector.readResult != null) ? ((char)(int)TcpConnector.readResult) : '\0');
				if (EcrPort.EnableRealtimeRawdataDebug && TcpConnector.readResult != null)
				{
					this.EcrConnectorRawdataEvent(EcrPort.DataDirection.POS2ECR, new byte[1] { Convert.ToByte(c) });
				}
			}
			catch (Exception e)
			{
				comPort.closePort();
				comPort.Dispose();
				comPort = null;
				EcrLog.WriteException("in closing TCP client : ", e);
				EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
				return ResponseCodes.POS_ERROR;
			}
			if (timeout || TcpConnector.readResult == null || !COMChars.ACK.Equals(c))
			{
				num++;
				if (num == 3)
				{
					break;
				}
				continue;
			}
			while (true)
			{
				CancelTransactionPerformed = false;
				comPort.waitForMessage();
				if (CancelTransactionPerformed)
				{
					break;
				}
				if (!TcpConnector.success && !timeout)
				{
					comPort.closePort();
					comPort = null;
					EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
					return ResponseCodes.POS_SOCKET_UNREACHABLE;
				}
				progress.Reset();
				if (timeout)
				{
					comPort.closePort();
					comPort = null;
					EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
					return ResponseCodes.FUNCTION_TIMEOUT;
				}
				char[] array = (char[])TcpConnector.readResult;
				POS2ECRMessageV3 pOS2ECRMessageV = POS2ECRMessageV3.create(array);
				if (EcrPort.EnableRealtimeRawdataDebug)
				{
					int num3 = 0;
					byte[] array2 = new byte[array.Length];
					char[] array3 = array;
					foreach (char value in array3)
					{
						try
						{
							array2[num3++] = Convert.ToByte(value);
						}
						catch (OverflowException)
						{
						}
					}
					this.EcrConnectorRawdataEvent(EcrPort.DataDirection.POS2ECR, array2);
				}
				if (!pOS2ECRMessageV.isValid())
				{
					comPort.sendNAK();
					if (EcrPort.EnableRealtimeRawdataDebug)
					{
						this.EcrConnectorRawdataEvent(EcrPort.DataDirection.ECR2POS, new byte[1] { Convert.ToByte(COMChars.NAK) });
					}
					if (!TcpConnector.success)
					{
						comPort.closePort();
						comPort = null;
						EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
						return ResponseCodes.POS_SOCKET_UNREACHABLE;
					}
					continue;
				}
				comPort.sendACK();
				if (EcrPort.EnableRealtimeRawdataDebug)
				{
					this.EcrConnectorRawdataEvent(EcrPort.DataDirection.ECR2POS, new byte[1] { Convert.ToByte(COMChars.ACK) });
				}
				if (!TcpConnector.success)
				{
					comPort.closePort();
					comPort = null;
					EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
					return ResponseCodes.POS_SOCKET_UNREACHABLE;
				}
				if (pOS2ECRMessageV.isHold())
				{
					if (this.EcrConnectorMessageReceived != null)
					{
						POS2ECRMessage_20V3 pOS2ECRMessage_20V = (POS2ECRMessage_20V3)pOS2ECRMessageV;
						this.EcrConnectorMessageReceived(pOS2ECRMessage_20V.identifier, pOS2ECRMessage_20V.displayMessage);
					}
					continue;
				}
				if (pOS2ECRMessageV.isError())
				{
					if (this.EcrConnectorMessageReceived != null)
					{
						POS2ECRMessage_22V3 pOS2ECRMessage_22V = (POS2ECRMessage_22V3)pOS2ECRMessageV;
						this.EcrConnectorMessageReceived(pOS2ECRMessage_22V.identifier, pOS2ECRMessage_22V.displayMessage);
					}
					comPort.closePort();
					comPort = null;
					EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
					return ResponseCodes.POS_ERROR;
				}
				if (pOS2ECRMessageV.isHoldWithCode())
				{
					if (this.EcrConnectorExtendedMessageReceived != null)
					{
						POS2ECRMessage_25V3 pOS2ECRMessage_25V = (POS2ECRMessage_25V3)pOS2ECRMessageV;
						this.EcrConnectorExtendedMessageReceived(pOS2ECRMessage_25V.identifier, pOS2ECRMessage_25V.code, pOS2ECRMessage_25V.displayMessage);
						if (pOS2ECRMessage_25V.code == "54")
						{
							HoldMessageCode = pOS2ECRMessage_25V.code;
						}
					}
					continue;
				}
				if (pOS2ECRMessageV.isErrorWithCode())
				{
					if (this.EcrConnectorExtendedMessageReceived != null)
					{
						POS2ECRMessage_26V3 pOS2ECRMessage_26V = (POS2ECRMessage_26V3)pOS2ECRMessageV;
						this.EcrConnectorExtendedMessageReceived(pOS2ECRMessage_26V.identifier, pOS2ECRMessage_26V.code, pOS2ECRMessage_26V.displayMessage);
					}
					comPort.closePort();
					comPort = null;
					EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
					return ResponseCodes.POS_ERROR;
				}
				if (pOS2ECRMessageV.isAs24())
				{
					if (this.EcrConnectorAs24MessageReceived != null)
					{
						POS2ECRMessage_27V3 pOS2ECRMessage_27V = (POS2ECRMessage_27V3)pOS2ECRMessageV;
						this.EcrConnectorAs24MessageReceived(pOS2ECRMessage_27V.identifier, pOS2ECRMessage_27V.track2data, pOS2ECRMessage_27V.displayMessage, pOS2ECRMessage_27V.bfoc);
						comPort.sendAs24();
						if (EcrPort.EnableRealtimeRawdataDebug)
						{
							byte[] rawdata = new byte[17]
							{
								2, 50, 56, 48, 48, 48, 48, 48, 48, 49,
								50, 51, 52, 53, 54, 3, 14
							};
							this.EcrConnectorRawdataEvent(EcrPort.DataDirection.ECR2POS, rawdata);
						}
					}
					continue;
				}
				if (pOS2ECRMessageV.isCancel())
				{
					if (this.EcrConnectorMessageReceived != null)
					{
						POS2ECRMessage_24V3 pOS2ECRMessage_24V = (POS2ECRMessage_24V3)pOS2ECRMessageV;
						this.EcrConnectorMessageReceived(pOS2ECRMessage_24V.identifier, pOS2ECRMessage_24V.displayMessage);
					}
					comPort.closePort();
					comPort = null;
					EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
					return ResponseCodes.TRANSACTION_CANCELED;
				}
				POS2ECRMessage_10V3 pOS2ECRMessage_10V = (POS2ECRMessage_10V3)pOS2ECRMessageV;
				pOS2ECRMessage_10V.updateTransactionParams(ref tp);
				if ("1".Equals(pOS2ECRMessage_10V.moreMessagesFlag))
				{
					if (Convert.ToInt16(pOS2ECRMessage_10V.transactionType) >= 30 && Convert.ToInt16(pOS2ECRMessage_10V.transactionType) < 35)
					{
						tp.addTotalItem(pOS2ECRMessage_10V.getCurrentTotal());
						tp.addTotalNewItem(pOS2ECRMessage_10V.getCurrentTotalNew());
					}
					else if (Convert.ToInt16(pOS2ECRMessage_10V.transactionType) > 0 && Convert.ToInt16(pOS2ECRMessage_10V.transactionType) < 30)
					{
						tp.addTransItem(pOS2ECRMessage_10V.getCurrentTrans());
					}
					terminalResponseCount++;
					continue;
				}
				if ("0".Equals(pOS2ECRMessage_10V.moreMessagesFlag) && terminalResponseCount > 1)
				{
					if (Convert.ToInt16(pOS2ECRMessage_10V.transactionType) > 30 && Convert.ToInt16(pOS2ECRMessage_10V.transactionType) < 35)
					{
						tp.addTotalItem(pOS2ECRMessage_10V.getCurrentTotal());
						tp.addTotalNewItem(pOS2ECRMessage_10V.getCurrentTotalNew());
					}
					else if (Convert.ToInt16(pOS2ECRMessage_10V.transactionType) > 0 && Convert.ToInt16(pOS2ECRMessage_10V.transactionType) < 30)
					{
						tp.addTransItem(pOS2ECRMessage_10V.getCurrentTrans());
					}
					tp.returnTicket();
					comPort.closePort();
					comPort = null;
					EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
					return ResponseCodes.SUCCESS;
				}
				tp.returnTicket();
				comPort.closePort();
				comPort = null;
				EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
				return ResponseCodes.SUCCESS;
			}
		}
		comPort.closePort();
		comPort = null;
		EcrLog.WriteFunction("### Exit from " + new StackTrace().GetFrame(0).GetMethod().Name + " method. ###");
		return ResponseCodes.NO_ACK_OR_NACK;
	}
}
