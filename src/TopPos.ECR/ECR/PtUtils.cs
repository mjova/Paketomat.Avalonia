using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Xml;

namespace ECR;

internal class PtUtils
{
	private TlvClass TLV;

	private static byte MULTI_BYTE_TAG_MASK = 31;

	private static byte MULTI_BYTE_LENGTH_MASK = 128;

	private static byte MULTI_BYTE_LENGTH_MASK2 = 129;

	private static byte MULTI_BYTE_LENGTH_MASK3 = 130;

	private static byte MULTI_BYTE_LENGTH_MASK4 = 131;

	private static byte MULTI_BYTE_LENGTH_MASK5 = 132;

	public static string ByteArrayToString(byte[] ba, bool hexa, bool replaceControlCharWithSymbol, string encoding)
	{
		StringBuilder stringBuilder = new StringBuilder(ba.Length * 2);
		foreach (byte b in ba)
		{
			if (!replaceControlCharWithSymbol)
			{
				if (hexa)
				{
					stringBuilder.AppendFormat("{0:x2}", b);
					continue;
				}
				stringBuilder.Append(Encoding.GetEncoding(encoding).GetString(new byte[1] { b }));
				continue;
			}
			switch (b)
			{
			case 2:
				stringBuilder.Append("<STX>");
				continue;
			case 3:
				stringBuilder.Append("<ETX>");
				continue;
			case 6:
				stringBuilder.Append("<ACK>");
				continue;
			case 21:
				stringBuilder.Append("<NAK>");
				continue;
			case 28:
				stringBuilder.Append("<FS>");
				continue;
			case 12:
				stringBuilder.Append("<FF>");
				continue;
			case 13:
				stringBuilder.Append("<CR>");
				continue;
			}
			if (hexa)
			{
				stringBuilder.AppendFormat("{0:x2}", b);
				continue;
			}
			stringBuilder.Append(Encoding.GetEncoding(encoding).GetString(new byte[1] { b }));
		}
		return stringBuilder.ToString().ToUpper();
	}

	private bool TagCheck(byte byteToCheck)
	{
		if ((byteToCheck & MULTI_BYTE_TAG_MASK) == MULTI_BYTE_TAG_MASK)
		{
			return true;
		}
		return false;
	}

	private bool LenghtCheck(byte byteToCheck)
	{
		if ((byteToCheck & MULTI_BYTE_LENGTH_MASK) == MULTI_BYTE_LENGTH_MASK)
		{
			return true;
		}
		return false;
	}

	private int LenghtSize(byte byteToCheck)
	{
		if ((byteToCheck & MULTI_BYTE_LENGTH_MASK2) == MULTI_BYTE_LENGTH_MASK2)
		{
			return 1;
		}
		if ((byteToCheck & MULTI_BYTE_LENGTH_MASK3) == MULTI_BYTE_LENGTH_MASK3)
		{
			return 2;
		}
		if ((byteToCheck & MULTI_BYTE_LENGTH_MASK4) == MULTI_BYTE_LENGTH_MASK4)
		{
			return 3;
		}
		if ((byteToCheck & MULTI_BYTE_LENGTH_MASK5) == MULTI_BYTE_LENGTH_MASK5)
		{
			return 4;
		}
		return 0;
	}

	public byte[] StringToByteArray(string hex)
	{
		if (hex != null)
		{
			int length = hex.Length;
			byte[] array = new byte[length / 2];
			for (int i = 0; i < length; i += 2)
			{
				array[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
			}
			return array;
		}
		return null;
	}

	public string HEX2ASCII(string hex)
	{
		string text = string.Empty;
		for (int i = 0; i < hex.Length; i += 2)
		{
			string value = hex.Substring(i, 2);
			int num = Convert.ToInt32(value, 16);
			text += (char)num;
		}
		return text;
	}

	public int IntFromHex(string HexID)
	{
		return int.Parse(HexID, NumberStyles.HexNumber);
	}

	public void ParseTlvByte(byte[] tlvdatabyte, ref ArrayList al)
	{
		string text = "";
		string text2 = null;
		TLV = new TlvClass();
		int num = 0;
		if (tlvdatabyte == null)
		{
			return;
		}
		try
		{
			if (!TagCheck(tlvdatabyte[num]))
			{
				TLV.Tag = tlvdatabyte[num].ToString("X2");
				num++;
				if (!LenghtCheck(tlvdatabyte[num]))
				{
					TLV.Lenght = tlvdatabyte[num].ToString("X2");
					int num2 = tlvdatabyte[num];
					num++;
					for (int i = num; i < num + num2; i++)
					{
						text += tlvdatabyte[i].ToString("X2");
					}
					TLV.Value = text;
					al.Add(TLV);
					num += num2;
					for (int j = num; j < tlvdatabyte.Length; j++)
					{
						text2 += tlvdatabyte[j].ToString("X2");
					}
					tlvdatabyte = StringToByteArray(text2);
					ParseTlvByte(tlvdatabyte, ref al);
				}
				else
				{
					switch (LenghtSize(tlvdatabyte[num]))
					{
					case 2:
						break;
					case 3:
						break;
					case 4:
						break;
					case 5:
						break;
					}
				}
				return;
			}
			TLV.Tag = tlvdatabyte[num].ToString("X2") + tlvdatabyte[num + 1].ToString("X2");
			num += 2;
			if (!LenghtCheck(tlvdatabyte[num]))
			{
				TLV.Lenght = tlvdatabyte[num].ToString("X2");
				int num2 = tlvdatabyte[num];
				num++;
				for (int k = num; k < num + num2; k++)
				{
					text += tlvdatabyte[k].ToString("X2");
				}
				TLV.Value = text;
				al.Add(TLV);
				num += num2;
				for (int l = num; l < tlvdatabyte.Length; l++)
				{
					text2 += tlvdatabyte[l].ToString("X2");
				}
				tlvdatabyte = StringToByteArray(text2);
				ParseTlvByte(tlvdatabyte, ref al);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine(ex.Message);
		}
	}

	public void ParseTlv(string tlvdata, ref ArrayList al)
	{
		string text = "";
		string text2 = "";
		string text3 = "";
		if (tlvdata.Length == 0)
		{
			return;
		}
		TLV = new TlvClass();
		try
		{
			text2 = tlvdata.Substring(0, 2);
			TLV.Tag = text2;
			text = tlvdata.Substring(2);
			byte byteToCheck = Convert.ToByte(text.Substring(0, 2), 16);
			TLV.Lenght = text.Substring(0, 2);
			if (LenghtCheck(byteToCheck))
			{
				string text4 = text.Substring(2, LenghtSize(byteToCheck) * 2);
				TLV.Lenght += text4;
				text3 = text.Substring(2 + LenghtSize(byteToCheck) * 2, IntFromHex(text4));
				TLV.Value = text3;
				text = text.Substring(2 + LenghtSize(byteToCheck) * 2 + IntFromHex(text4));
				al.Add(TLV);
				ParseTlv(text, ref al);
			}
			else
			{
				string text4 = text.Substring(0, 2);
				text3 = text.Substring(2, IntFromHex(text4));
				TLV.Value = text3;
				text = text.Substring(2 + IntFromHex(text4));
				al.Add(TLV);
				ParseTlv(text, ref al);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine(ex.Message);
		}
	}

	public string CheckToConvert(string emvTag)
	{
		string result = "0";
		XmlDocument xmlDocument = new XmlDocument();
		xmlDocument.Load(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "/emvxml.xml");
		XmlNodeList xmlNodeList = xmlDocument.SelectNodes("/EmvTags/tag");
		foreach (XmlNode item in xmlNodeList)
		{
			if (item["id"].InnerText == emvTag)
			{
				result = item["convert"].InnerText;
			}
		}
		return result;
	}

	public string ReturnValueFromArrayList(ArrayList al, string searchString)
	{
		string result = null;
		for (int i = 0; i < al.Count; i++)
		{
			if (((TlvClass)al[i]).Tag == searchString)
			{
				result = ((TlvClass)al[i]).Value;
			}
		}
		return result;
	}

	public string ReturnLenght(string lenghtstr)
	{
		string text = "";
		string text2 = "";
		long.TryParse(lenghtstr, out var result);
		if (result < 128)
		{
			text = "";
			text2 = "X2";
		}
		if (result > 127 && result < 256)
		{
			text = "81";
			text2 = "X2";
		}
		if (result > 255 && result < 65536)
		{
			text = "82";
			text2 = "X4";
		}
		if (result > 65535 && result < 16777216)
		{
			text = "83";
			text2 = "X6";
		}
		if (result > 16777215)
		{
			text = "84";
			text2 = "X8";
		}
		return text + result.ToString(text2);
	}
}
