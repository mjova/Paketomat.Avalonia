using System;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace ECR;

internal static class hwid
{
	public static string getActCode()
	{
		string text = "";
		string text2 = "";
		string text3 = "PAYTEN SEE 2019 CBU";
		text = text3 + GetMacAddressPhysicalNetworkInterface() + getHddSerial() + Environment.ProcessorCount + Environment.MachineName;
		return GetMD5(text).Substring(11, 12).ToUpper();
	}

	public static string GetMacAddressPhysicalNetworkInterface()
	{
		string text = "";
		ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("Select MACAddress FROM Win32_NetworkAdapter WHERE MACAddress IS NOT NULL AND PhysicalAdapter = true AND PNPDeviceID LIKE 'PCI%'");
		ManagementObjectCollection managementObjectCollection = managementObjectSearcher.Get();
		foreach (ManagementBaseObject item in managementObjectCollection)
		{
			text += Convert.ToString(item["MACAddress"]);
		}
		return text;
	}

	private static string getMacAddress()
	{
		string text = "";
		NetworkInterface[] allNetworkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
		foreach (NetworkInterface networkInterface in allNetworkInterfaces)
		{
			text = GetMacAddressPhysicalNetworkInterface();
			text = "";
			switch (networkInterface.NetworkInterfaceType)
			{
			case NetworkInterfaceType.Ethernet:
				text += networkInterface.GetPhysicalAddress().ToString();
				break;
			case NetworkInterfaceType.Ethernet3Megabit:
				text += networkInterface.GetPhysicalAddress().ToString();
				break;
			case NetworkInterfaceType.FastEthernetFx:
				text += networkInterface.GetPhysicalAddress().ToString();
				break;
			case NetworkInterfaceType.FastEthernetT:
				text += networkInterface.GetPhysicalAddress().ToString();
				break;
			case NetworkInterfaceType.GigabitEthernet:
				text += networkInterface.GetPhysicalAddress().ToString();
				break;
			case NetworkInterfaceType.Wireless80211:
				text += networkInterface.GetPhysicalAddress().ToString();
				break;
			}
		}
		return text;
	}

	private static string getHddSerial()
	{
		string text = "";
		ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("select * from Win32_PhysicalMedia where Tag like '%PHYSICALDRIVE0%'");
		ManagementObjectCollection managementObjectCollection = managementObjectSearcher.Get();
		foreach (ManagementBaseObject item in managementObjectCollection)
		{
			text += Convert.ToString(item["SerialNumber"]);
		}
		return text;
	}

	private static string getCpuId()
	{
		string text = "";
		ManagementClass managementClass = new ManagementClass("win32_processor");
		ManagementObjectCollection instances = managementClass.GetInstances();
		foreach (ManagementObject item in instances)
		{
			if (text == "")
			{
				text = item.Properties["processorID"].Value.ToString();
				break;
			}
		}
		return text;
	}

	public static string GetMD5(string text)
	{
		UnicodeEncoding unicodeEncoding = new UnicodeEncoding();
		byte[] bytes = unicodeEncoding.GetBytes(text);
		MD5 mD = new MD5CryptoServiceProvider();
		string text2 = "";
		byte[] array = mD.ComputeHash(bytes);
		byte[] array2 = array;
		foreach (byte b in array2)
		{
			text2 += $"{b:x2}";
		}
		return text2;
	}

	internal static string calculateProdKey()
	{
		string text = "PAYTEN POS-ECR DLL";
		string text2 = "";
		string text3 = "";
		text2 = text + getActCode();
		return GetMD5(text2).Substring(11, 12).ToUpper();
	}
}
