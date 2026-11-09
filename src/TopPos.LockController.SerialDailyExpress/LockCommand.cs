namespace TopPos.LockController.SerialDailyExpress
{
	public enum CommandId : byte
	{
		OpenLock = 0xAA,
		CheckLockStatus = 0xBB,
		GetAllLocksStatus = 0xDD,
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
			_data[1] = (byte) (this.ControllerAddress & 0xff);
			_data[2] = (byte) this.Id;
			int idx = 3;
			int end = Math.Min(Constants.CommandDataLength - 1, commandParameters.Length);
			for (int i = 0; i < end; i++) {
				_data[idx++] = commandParameters[i];
			}
			_data[Constants.CommandDataLength-1] = TextEnd;
        }

		public CommandId Id { get; }
		public int Number { get; }	// Door number (1-based) or module number (1-based)

		/// <summary>
		/// Controller address (0-based): derived from Number.
		/// Single door commands: (doorNumber - 1) / NumberOfDoorsPerController
		/// Multiple door commands: moduleNumber - 1
		/// </summary>
		protected abstract int ControllerAddress { get; }

		public ReadOnlyMemory<byte> Data => _data;

		public bool MatchResponse(LockResponse? response)
		{
			if (response == null)
				return false;
			if (response.CommandId == this.Id && response.ControllerAddress == this.ControllerAddress)
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

		/// <summary>
		/// Lock address within a controller (1-based): (doorNumber - 1) % NumberOfDoorsPerController + 1
		/// </summary>
		protected static byte GetLockAddress(int doorNumber)
		{
			int position = (doorNumber - 1) % Constants.NumberOfDoorsPerController;
			return (byte)(1 << position);
		}

		/// <summary>
		/// Controller address (0-based) for a given door number
		/// </summary>
		protected static int GetControllerAddressForDoor(int doorNumber)
			=> (doorNumber - 1) / Constants.NumberOfDoorsPerController;

		public static int GetCommandResponseLength(CommandId commandId)
			=> commandId switch {
				CommandId.CheckLockStatus => Constants.SingleResponseDataLength,
				CommandId.OpenLock => Constants.SingleResponseDataLength,
				CommandId.GetAllLocksStatus => Constants.BulkResponseDataLength,
				_ => throw new ArgumentOutOfRangeException(nameof(commandId), $"Unknown lock command ID: {commandId}")
			};
	}

	public class OpenDoorSingleCommand : LockCommand
	{
        public OpenDoorSingleCommand(int doorNumber)
			: base(CommandId.OpenLock, doorNumber, GetLockAddress(doorNumber))
        {
			EnsureValidDoorNumber(doorNumber);
		}

		protected override int ControllerAddress => GetControllerAddressForDoor(this.Number);
	}

	public class CheckDoorStatusSingleCommand : LockCommand
	{
        public CheckDoorStatusSingleCommand(int doorNumber)
			: base(CommandId.CheckLockStatus, doorNumber, GetLockAddress(doorNumber))
        {
			EnsureValidDoorNumber(doorNumber);
		}

		protected override int ControllerAddress => GetControllerAddressForDoor(this.Number);
	}

	public class CheckDoorStatusMultipleCommand : LockCommand
	{
        public CheckDoorStatusMultipleCommand(int moduleNumber)
			: base(CommandId.GetAllLocksStatus, moduleNumber, 0xFF)
		{ }

		protected override int ControllerAddress => this.Number - 1;
	}
}
