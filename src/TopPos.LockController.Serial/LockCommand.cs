namespace TopPos.LockController.Serial
{
	public enum CommandId : byte
	{
		OpenDoorSingle = 0xaa,
		Power = 0xab,							// ? Open Computer/Service Door
		CheckDoorStatusSingle = 0xbb,
		OpenDoorMultiple = 0xbe,
		Beep = 0xcc,
		CheckDoorStatusMultiple = 0xdd,
		SetTime = 0xde,
		LedLight = 0xee,
		Restart = 0xff
	}

	public abstract class LockCommand
	{
		public const byte TextStart = 0x02;
		public const byte TextEnd = 0x03;
		private readonly byte[] _data;

        protected LockCommand(CommandId commandId, int number, params byte[] commandParameters)
        {
            this.Id = commandId;
			
			this.Number = number;
			_data = new byte[Constants.CommandDataLength];
			_data[0] = TextStart;
			_data[1] = (byte) (this.SerializedNumber & 0xff);
			_data[2] = (byte) this.Id;
			int idx = 3;
			int end = Math.Min(Constants.CommandDataLength - 1, commandParameters.Length);
			for (int i = 0; i < end; i++) {
				_data[idx++] = commandParameters[i];
			}
			_data[Constants.CommandDataLength-1] = TextEnd;
        }

		public CommandId Id { get; }
		public int Number { get; }	// Depending on the command, a door or a module number

		protected virtual int SerializedNumber => this.Number;	// Some commands send a door number within the module number's range, instead of the module number itself
		public ReadOnlyMemory<byte> Data => _data;

		public bool MatchResponse(LockResponse? response)
		{
			if (response == null)
				return false;
			if (response.CommandId == this.Id && (response.Number == this.Number || response.Number == SerializedNumber))
				return true;
			return false;
		}

		public override string ToString() => $"{this.Id} [{this.Number}]";

		protected static bool IsTextStart(byte b) => b == TextStart;
		protected static bool IsTextEnd(byte b) => b == TextEnd;

		protected static bool HasValidCommandMarkers(ReadOnlySpan<byte> data)
			=> data.Length >= Constants.CommandDataLength &&
				IsTextStart(data[0]) &&
				IsTextEnd(data[Constants.CommandDataLength-1]);

		protected static void EnsureValidDoorNumber(int number)
		{
			if (number < Constants.MinDoorNumber || number > Constants.MaxDoorNumber) {
				throw new ArgumentOutOfRangeException(nameof(number), $"Door number must be in the range {Constants.MinDoorNumber} and {Constants.MaxDoorNumber}");
			}
		}

		public static int GetCommandResponseLength(CommandId commandId)
			=> commandId switch {
				CommandId.CheckDoorStatusSingle => Constants.SingleResponseDataLength,
				CommandId.OpenDoorSingle => Constants.SingleResponseDataLength,
				CommandId.Restart => Constants.SingleResponseDataLength,
				CommandId.LedLight => Constants.SingleResponseDataLength,
				CommandId.Power => Constants.SingleResponseDataLength,
				CommandId.SetTime => Constants.SingleResponseDataLength,
				CommandId.Beep => Constants.SingleResponseDataLength,
				CommandId.CheckDoorStatusMultiple => Constants.MultiResponseDataLength,
				CommandId.OpenDoorMultiple => Constants.MultiResponseDataLength,
				_ => throw new ArgumentOutOfRangeException(nameof(commandId), $"Unknown lock command ID: {commandId}")
			};
	}

	public class OpenDoorSingleCommand : LockCommand
	{
        public OpenDoorSingleCommand(int doorNumber)
			: base(CommandId.OpenDoorSingle, doorNumber)
        {
			EnsureValidDoorNumber(doorNumber);
		}
	}

	public class OpenDoorMultipleCommand : LockCommand
	{
        public OpenDoorMultipleCommand(int moduleNumber)
			: base(CommandId.OpenDoorMultiple, moduleNumber)	// moduleNumber is 0-based, door numbers are 1-based
        {}
	}

	public class PowerCommand : LockCommand
	{
        public PowerCommand(int moduleNumber, bool on)
			: base(CommandId.Power, moduleNumber, (byte) (on? 0x01 : 0x00))
        {
			this.On = on;
        }

		public bool On { get; }
	}

	public class CheckDoorStatusSingleCommand : LockCommand
	{
        public CheckDoorStatusSingleCommand(int doorNumber)
			: base(CommandId.CheckDoorStatusSingle, doorNumber)
        {
			EnsureValidDoorNumber(doorNumber);
		}
	}

	public class CheckDoorStatusMultipleCommand : LockCommand
	{
        public CheckDoorStatusMultipleCommand(int moduleNumber)
			: base(CommandId.CheckDoorStatusMultiple, moduleNumber) 
		{ }

		protected override int SerializedNumber => (this.Number - 1) * Constants.NumberOfDoorsPerController + 1;
	}

	public class BeepCommand : LockCommand
	{
        public BeepCommand(int moduleNumber)
			: base(CommandId.Beep, moduleNumber)
        {}
	}

	public class LedLightCommand : LockCommand
	{
        public LedLightCommand(int moduleNumber, bool on)
			: base(CommandId.LedLight, moduleNumber, (byte) (on? 0x01 : 0x00))
        {
			this.On = on;
        }

		public bool On { get; }
	}

	public class RestartModuleCommand : LockCommand
	{
        public RestartModuleCommand(int moduleNumber)
			: base(CommandId.Restart, moduleNumber)
        {}

		protected override int SerializedNumber => (this.Number-1) * Constants.NumberOfDoorsPerController + 1;
	}
}
