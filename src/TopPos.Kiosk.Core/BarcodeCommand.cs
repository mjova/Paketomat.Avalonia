using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TopPos.Client.WPF
{


	// Query: ~<SOH>0000@GRDDUR*;<ETX>
	// Bytes: 0x7E, 0x01, 0x30, 0x30, 0x30, 0x30, 0x40, 0x47, 0x52, 0x44, 0x44, 0x55, 0x52, 0x2A, 0x3B, 0x03
	// Len: 16
	// byte[] _testQuery = { 0x7E, 0x01, 0x30, 0x30, 0x30, 0x30, 0x40, 0x47, 0x52, 0x44, 0x44, 0x55, 0x52, 0x2A, 0x3B, 0x03 };

	// Response: <STX><SOH>0000@GRDDUR5000<ACK>;<ETX>
	// Bytes: 0x02, 0x01, 0x30, 0x30, 0x30, 0x30, 0x40, 0x47, 0x52, 0x44, 0x44, 0x55, 0x52, 0x35, 0x30, 0x30, 0x30, 0x06, 0x3B, 0x03
	// Len: 20
	// byte[] _testResponse = new byte[30];


	/*
	public class BarcodeCommands
	{
		public Dictionary<string, string> _commands = new Dictionary<string, string>();

		public BarcodeCommands() {
			_commands.Add("EnterSetup", "SETUPE1");
			_commands.Add("ExitSetup", "SETUPE0");
			_commands.Add("TransmitProgrammingBarcodeData", "SETUPT1");
			_commands.Add("DoNotTransmitProgrammingBarcodeData", "SETUPT0");
			_commands.Add("DisableGoodReadDelay", "GRDENA1");
			_commands.Add("EnableGoodReadDelay", "GRDENA0");
			_commands.Add("0", "DIGIT0");
			_commands.Add("1", "DIGIT1");
			_commands.Add("2", "DIGIT2");
			_commands.Add("3", "DIGIT3");
			_commands.Add("4", "DIGIT4");
			_commands.Add("5", "DIGIT5");
			_commands.Add("6", "DIGIT6");
			_commands.Add("7", "DIGIT7");
			_commands.Add("8", "DIGIT8");
			_commands.Add("9", "DIGIT9");
			_commands.Add("GoodReadDelay", "GRDDUR");
			_commands.Add("DigitSave", "DIGSAV");
		}
	}
	*/


	public class BarcodeCommand
	{
		public enum StorageType
		{
			Permanent = 0x40,   // @
			Temporary = 23      // #
		};

		public enum QueryType
		{
			CurrentValue = 0x2A,                // *
			FactoryDefaultValue = 0x26,     // &
			RangeOfPossibleValues = 0x5E        // ^
		}

		public const byte CommandStart = 0x7E;      // ~
		public const byte CommandSufix = 0x3B;      // ;
		public const byte StartOfHeading = 0x01;
		public const byte TextStart = 0x02;
		public const byte TextEnd = 0x03;
		private readonly byte[] _data;

		public BarcodeCommand(string tag, string subTag,
				StorageType storageType = StorageType.Permanent,
				QueryType queryType = QueryType.CurrentValue)
		{

			string command = tag+subTag;

			// za sve komande koje sam gledao, 16 bajtova ovo je dovoljno.
			// ako će ikada ova fn da dobija druge komande, proveriti
			// da li je za njih dovoljno 16 bajtova.

			// Query: ~<SOH>0000@GRDDUR*;<ETX>
			// Bytes: 0x7E, 0x01, 0x30, 0x30, 0x30, 0x30, 0x40, 0x47, 0x52, 0x44, 0x44, 0x55, 0x52, 0x2A, 0x3B, 0x03
			ArgumentException.ThrowIfNullOrEmpty(command);
			if (command.Length < 6)
				throw new ArgumentException(
					$"Command '{command}' must be at least 6 characters.", nameof(command));

			int bufferLen = 10 + command.Length;
			int index = 0;
			_data = new byte[bufferLen];
			_data[index++] = CommandStart;
			_data[index++] = StartOfHeading;
			string prefix = "0000";
			Array.Copy(Encoding.ASCII.GetBytes(prefix), 0, _data, index, prefix.Length);
			index+= prefix.Length;
			_data[index++]= (byte) storageType;
			Array.Copy(Encoding.ASCII.GetBytes(command.ToUpper()), 0, _data, index, command.Length);
			index+= command.Length;
			_data[index++]= (byte) queryType;
			_data[index++]= CommandSufix;
			_data[index++]= TextEnd;
		}

		public ReadOnlyMemory<byte> Data => _data;


	}

}
