using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Xml;

namespace ECR;

public class Configuration
{
	public static string prodKey;

	public static string prodKeyE;

	public static string prodKeyW;

	public static string amountCurrency;

	public static string portName;

	public static string deviceVendor;

	public static string deviceVid;

	public static string devicePid;

	public static int baudRate;

	public static int dataBits;

	public static int stopBits;

	public static string parity;

	public static string ipaddress;

	public static int retryTimeout;

	public static int functionTimeout;

	public static int waitForAckTimeout;

	public static bool enableLogging;

	public static bool detailedLogging;

	public static bool sendRespMessage13;

	public static string posMessageEncoding;

	public static bool isReadFRomFile;

	/// <summary>
	/// Folder za ECR logove (v4). Podrazumevano „Log“ uz program, kao v2; aplikacija ga postavlja kad program nema pravo
	/// upisa u svoj folder (npr. /opt na Linuxu).
	/// </summary>
	public static string logDirectory = Path.Combine(AppContext.BaseDirectory, "Log");

	// v4: AppContext.BaseDirectory i Path.Combine umesto Assembly.Location + "\\..." (prazan u single-file publish-u,
	// obrnuta kosa crta na Linuxu, a ecr.cfg grana je bila bez separatora).
	static Configuration()
	{
		deviceVendor = "Ingenico";
		deviceVid = string.Empty;
		devicePid = string.Empty;
		waitForAckTimeout = 3000;
		if (double.Parse(EcrPort.DLLVersion, CultureInfo.InvariantCulture) >= 2.1)
		{
			isReadFRomFile = readXmlConfiguration(Path.Combine(AppContext.BaseDirectory, "ecrcfg.xml"));
		}
		else
		{
			isReadFRomFile = readConfig(Path.Combine(AppContext.BaseDirectory, "ecr.cfg"));
		}
	}

	public static string GetExecutingDirectoryName()
	{
		return AppContext.BaseDirectory;
	}

	private static bool readConfig(string cfgFileName)
	{
		if (File.Exists(cfgFileName))
		{
			using (StreamReader streamReader = File.OpenText(cfgFileName))
			{
				string text;
				while ((text = streamReader.ReadLine()) != null)
				{
					if (!text.StartsWith(";") && !text.StartsWith("["))
					{
						string value = "";
						processLine(text, "Prod_keyW", ref prodKeyW);
						processLine(text, "Prod_keyE", ref prodKeyE);
						processLine(text, "Prod_keyE", ref prodKey);
						processLine(text, "Amount_currency", ref amountCurrency);
						processLine(text, "Port_name", ref portName);
						if (processLine(text, "Baud_rate", ref value) == 0)
						{
							baudRate = int.Parse(value);
						}
						if (processLine(text, "Data_bits", ref value) == 0)
						{
							dataBits = int.Parse(value);
						}
						if (processLine(text, "Stop_bits", ref value) == 0)
						{
							stopBits = int.Parse(value);
						}
						processLine(text, "Parity", ref parity);
						if (processLine(text, "POS_IP_address", ref value) == 0)
						{
							ipaddress = value;
						}
						if (processLine(text, "Retry_timeout", ref value) == 0)
						{
							retryTimeout = int.Parse(value);
						}
						if (processLine(text, "WaitForAckTimeout", ref value) == 0)
						{
							waitForAckTimeout = int.Parse(value);
						}
						if (processLine(text, "Function_timeout", ref value) == 0)
						{
							functionTimeout = int.Parse(value);
						}
						if (processLine(text, "Enable_logging", ref value) == 0)
						{
							enableLogging = bool.Parse(value);
						}
						if (processLine(text, "Detailed_logging", ref value) == 0)
						{
							detailedLogging = bool.Parse(value);
						}
						if (processLine(text, "Send_RespMessage13", ref value) == 0)
						{
							sendRespMessage13 = bool.Parse(value);
						}
						if (processLine(text, "pos_message_encoding", ref value) == 0)
						{
							posMessageEncoding = value;
						}
					}
				}
			}
			return true;
		}
		return false;
	}

	private static bool readXmlConfiguration(string cfgFileName)
	{
		if (File.Exists(cfgFileName))
		{
			prodKey = readDataFromXML(cfgFileName, "/Configuration/ProductKey/Prod_keyE");
			prodKeyE = readDataFromXML(cfgFileName, "/Configuration/ProductKey/Prod_keyE");
			prodKeyW = readDataFromXML(cfgFileName, "/Configuration/ProductKey/Prod_keyW");
			amountCurrency = readDataFromXML(cfgFileName, "/Configuration/InputV2/Amount_currency");
			try
			{
				deviceVendor = readDataFromXML(cfgFileName, "/Configuration/COMport/Vendor");
				deviceVid = readDataFromXML(cfgFileName, "/Configuration/COMport/VID");
				devicePid = readDataFromXML(cfgFileName, "/Configuration/COMport/PID");
			}
			catch (Exception)
			{
			}
			portName = readDataFromXML(cfgFileName, "/Configuration/COMport/Port_name");
			baudRate = int.Parse(readDataFromXML(cfgFileName, "/Configuration/COMport/Baud_rate"));
			dataBits = int.Parse(readDataFromXML(cfgFileName, "/Configuration/COMport/Data_bits"));
			stopBits = int.Parse(readDataFromXML(cfgFileName, "/Configuration/COMport/Stop_bits"));
			parity = readDataFromXML(cfgFileName, "/Configuration/COMport/Parity");
			ipaddress = readDataFromXML(cfgFileName, "/Configuration/LAN/POS_IP_address");
			retryTimeout = int.Parse(readDataFromXML(cfgFileName, "/Configuration/TimeOut/Retry_timeout"));
			waitForAckTimeout = int.Parse(readDataFromXML(cfgFileName, "/Configuration/TimeOut/WaitForAckTimeout"));
			functionTimeout = int.Parse(readDataFromXML(cfgFileName, "/Configuration/TimeOut/Function_timeout"));
			enableLogging = bool.Parse(readDataFromXML(cfgFileName, "/Configuration/Log/Enable_logging"));
			detailedLogging = bool.Parse(readDataFromXML(cfgFileName, "/Configuration/Log/Detailed_logging"));
			sendRespMessage13 = bool.Parse(readDataFromXML(cfgFileName, "/Configuration/ProtocolV1/Send_RespMessage13"));
			posMessageEncoding = readDataFromXML(cfgFileName, "/Configuration/Encoding/Pos_Message_Encoding");
			return true;
		}
		return false;
	}

	private static int processLine(string line, string propertyName, ref string value)
	{
		try
		{
			int length = propertyName.Length;
			if (line.StartsWith(propertyName))
			{
				line = line.Remove(0, length);
				line = line.Replace('=', ' ');
				int num = line.IndexOf(';');
				length = line.Length;
				if (num != -1)
				{
					line = line.Remove(num, length - num);
				}
				line = line.Trim();
				line = line.Replace("\\n", "\n");
				value = line;
				return 0;
			}
			return 1;
		}
		catch (Exception)
		{
			return 1;
		}
	}

	private static string readDataFromXML(string filename, string singleNode)
	{
		try
		{
			XmlDocument xmlDocument = new XmlDocument();
			xmlDocument.Load(filename);
			XmlNode xmlNode = xmlDocument.DocumentElement.SelectSingleNode(singleNode);
			return xmlNode.InnerText;
		}
		catch (Exception)
		{
			return "";
		}
	}

	private static void changeDataInXML(string filename, string singleNode, string nodeData)
	{
		XmlDocument xmlDocument = new XmlDocument();
		xmlDocument.Load(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "/" + filename);
		XmlNode xmlNode = xmlDocument.DocumentElement.SelectSingleNode(singleNode);
		xmlNode.InnerText = nodeData;
		xmlDocument.Save(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "/" + filename);
	}

	private static void makeSetupXMLfile()
	{
		XmlWriterSettings xmlWriterSettings = new XmlWriterSettings();
		xmlWriterSettings.Indent = true;
		XmlWriter xmlWriter = XmlWriter.Create(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "/ecrcfg.xml", xmlWriterSettings);
		xmlWriter.WriteStartDocument();
		xmlWriter.WriteComment("This file is generated by the Asseco Ecr Dll");
		xmlWriter.WriteStartElement("Settings");
		xmlWriter.WriteElementString("Hostname", "localhost");
		xmlWriter.WriteElementString("Username", "root");
		xmlWriter.WriteElementString("Password", "root");
		xmlWriter.WriteEndElement();
		xmlWriter.WriteEndDocument();
		xmlWriter.Flush();
		xmlWriter.Close();
	}
}
