using System;
using System.IO;
using System.Text;

namespace ECR;

// v4: putanja loga ide preko Configuration.logDirectory i Path.Combine (v2 je spajao BaseDirectory + "Log\\..."), a greška
// upisa loga (folder samo za čitanje, prava, pun disk) se ignoriše — log ne sme da prekine transakciju. Šta se i kada
// upisuje ostalo je isto kao u v2. Uklonjen nekorišćeni WriteDebug (petlja `while (e != null)` se nije završavala).
internal static class EcrLog
{
	private const string StandardHeader = "PAYTEN - Standard ECR log: ";

	private const string DetailedHeader = "PAYTEN - Detailed ECR log: ";

	internal static void WriteException(string logMessage, Exception e, string source = null)
	{
		DateTime now = DateTime.Now;
		Append("std_log_", now, StandardHeader, delegate(StreamWriter streamWriter)
		{
			streamWriter.WriteLine(now.ToString("HH:mm:ss.fff") + " " + logMessage + "\n");
			StringBuilder stringBuilder = new StringBuilder();
			while (e != null)
			{
				stringBuilder.AppendLine(e.Message);
				stringBuilder.AppendLine(e.StackTrace);
				e = e.InnerException;
			}
			streamWriter.Write(stringBuilder.ToString());
		});
	}

	internal static void WriteFunction(string logMessage, string source = null)
	{
		DateTime now = DateTime.Now;
		Append("std_log_", now, StandardHeader, delegate(StreamWriter streamWriter)
		{
			streamWriter.WriteLine(now.ToString("HH:mm:ss.fff") + " " + logMessage);
		});
	}

	internal static void Write(string logMessage, bool directionSending, LogMessageType type, string source = null)
	{
		DateTime now = DateTime.Now;
		string text2 = StringToHexString(logMessage);
		string text3 = StringByteToString(logMessage, hexa: false, replaceControlCharWithSymbol: true, "ASCII");
		string text4 = logMessage.Length.ToString();
		text4 = text4.PadLeft(3, '0');
		string text = ((!directionSending) ? ("ECR <= POS | " + source.PadLeft(15, ' ')) : ("ECR => POS | " + source.PadLeft(15, ' ')));
		Append("std_log_", now, StandardHeader, delegate(StreamWriter streamWriter)
		{
			streamWriter.WriteLine(now.ToString("HH:mm:ss.fff") + " | " + text + " | Len: " + text4 + " | Hex   : " + text2);
			streamWriter.WriteLine(now.ToString("HH:mm:ss.fff") + " | " + text + " | Len: " + text4 + " | ASCII : " + text3);
		});
	}

	internal static void Write(string logMessage, bool directionSending, LogMessageType type, int lrc, string source = null)
	{
		DateTime now = DateTime.Now;
		int length = logMessage.Length;
		if (type == LogMessageType.MSG && !directionSending)
		{
			logMessage = logMessage.Insert(0, '\u0002'.ToString());
			logMessage = logMessage.Insert(length + 1, '\u0003'.ToString());
			logMessage = logMessage.Insert(length + 2, ((char)lrc).ToString());
		}
		string text2 = StringToHexString(logMessage);
		string text3 = logMessage.Length.ToString();
		text3 = text3.PadLeft(3, '0');
		string text = ((!directionSending) ? ("ECR <= POS | " + source.PadLeft(15, ' ')) : ("ECR => POS | " + source.PadLeft(15, ' ')));
		Append("std_log_", now, StandardHeader, delegate(StreamWriter streamWriter)
		{
			streamWriter.WriteLine(now.ToString("HH:mm:ss.fff") + " | " + text + " | Len: " + text3 + " | Hex   : " + text2);
			streamWriter.WriteLine(now.ToString("HH:mm:ss.fff") + " | " + text + " | Len: " + text3 + " | ASCII : " + logMessage);
		});
	}

	internal static void WriteDetailed(string logMessage, bool directionSending, LogMessageType type, DateTime start, int[] sendTimeArray, string source = null)
	{
		string text2 = logMessage.Length.ToString();
		text2 = text2.PadLeft(3, '0');
		string text = ((!directionSending) ? ("ECR <= POS | " + source.PadLeft(15, ' ')) : ("ECR => POS | " + source.PadLeft(15, ' ')));
		Append("det_log_", start, DetailedHeader, delegate(StreamWriter streamWriter)
		{
			streamWriter.Write(start.ToString("HH:mm:ss.fff") + " | " + text + " | Len: " + text2 + " | Hex   : ");
			int num = 0;
			foreach (char c in logMessage)
			{
				streamWriter.Write(CharToHexString(c) + "[+" + sendTimeArray[num].ToString("d3") + "] ");
				streamWriter.Write(CharToHexString(c) + " ");
				num++;
			}
			streamWriter.WriteLine();
		});
	}

	internal static void WriteAckNack(string logMessage, bool directionSending, LogMessageType type, bool detailedLogging, string source = null)
	{
		DateTime now = DateTime.Now;
		string text2 = ((!directionSending) ? ("ECR <= POS | " + source.PadLeft(15, ' ')) : ("ECR => POS | " + source.PadLeft(15, ' ')));
		Append(detailedLogging ? "det_log_" : "std_log_", now, null, delegate(StreamWriter streamWriter)
		{
			streamWriter.WriteLine(now.ToString("HH:mm:ss.fff") + " | " + text2 + " | Len: 001 | Hex   : " + int.Parse(logMessage).ToString().PadLeft(2, '0'));
		});
	}

	internal static void WriteEnd(bool detailedLogging)
	{
		DateTime now = DateTime.Now;
		Append(detailedLogging ? "det_log_" : "std_log_", now, null, delegate(StreamWriter streamWriter)
		{
			streamWriter.WriteLine("--------------------------------");
		});
	}

	internal static void WriteReturn(string returnValue, bool detailedLogging)
	{
		DateTime now = DateTime.Now;
		Append(detailedLogging ? "det_log_" : "std_log_", now, null, delegate(StreamWriter streamWriter)
		{
			streamWriter.WriteLine(now.ToString("HH:mm:ss.fff") + " | " + returnValue);
			streamWriter.WriteLine("--------------------------------");
		});
	}

	/// <summary>
	/// Dopisuje u log fajl `prefix + datum` u Configuration.logDirectory; novom fajlu prvo upiše zaglavlje ako je zadato
	/// (kao v2). Svaka greška, i neispravan folder, se ignoriše: log ne sme da prekine transakciju.
	/// </summary>
	private static void Append(string prefix, DateTime date, string header, Action<StreamWriter> write)
	{
		try
		{
			string path = Path.Combine(Configuration.logDirectory, prefix + date.ToString("yyyy-MM-dd") + ".log");
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			if (header != null && !File.Exists(path))
			{
				using StreamWriter headerWriter = File.CreateText(path);
				headerWriter.WriteLine(header + date.ToString("yyyy-MM-dd"));
				headerWriter.WriteLine("--------------------------------");
				headerWriter.WriteLine();
			}
			using StreamWriter streamWriter = File.AppendText(path);
			write(streamWriter);
		}
		catch (Exception)
		{
		}
	}

	private static string CharToHexString(char c)
	{
		string text = Convert.ToString(c, 16).PadLeft(2, '0');
		return text.ToString().ToUpper();
	}

	private static string StringToHexString(string s)
	{
		StringBuilder stringBuilder = new StringBuilder();
		char[] array = s.ToCharArray();
		char[] array2 = array;
		foreach (char value in array2)
		{
			int num = Convert.ToInt32(value);
			string text = $"{num:X}";
			stringBuilder.Append(text.PadLeft(2, '0')).Append(" ");
		}
		return stringBuilder.ToString().ToUpper();
	}

	private static string StringByteToString(string str, bool hexa, bool replaceControlCharWithSymbol, string encoding)
	{
		if (str != null)
		{
			byte[] bytes = Encoding.ASCII.GetBytes(str);
			StringBuilder stringBuilder = new StringBuilder(bytes.Length * 2);
			byte[] array = bytes;
			foreach (byte b in array)
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
		return null;
	}
}
