namespace TopPos.LockController.SerialDailyExpress
{
	public abstract class LockResponse {
		public const byte Acknowledge = 0x06;
		public const byte TextEnd = 0x03;

		protected LockResponse(CommandId commandId)
        {
            this.CommandId = commandId;
		}

		public CommandId CommandId { get; }
		public abstract int ControllerAddress { get; }
		public abstract ReadOnlyMemory<byte> Data { get; }

		public override string ToString() => $"{this.CommandId} [ctrl={this.ControllerAddress}]";

		protected static bool IsAcknowledge(byte b) => b == Acknowledge;
		protected static bool IsTextEnd(byte b) => b == TextEnd;
		protected static bool IsValidCommandId(byte b) => Enum.IsDefined((CommandId)b);
		protected static bool IsValidDoorStatusCode(byte b) => true;    // 00=closed, 1-FF open, so all values are valid

		protected static bool HasValidSingleResponseMarkers(ReadOnlySpan<byte> data)
			=> data.Length >= Constants.SingleResponseDataLength &&
				IsAcknowledge(data[0]) &&
				IsTextEnd(data[Constants.SingleResponseDataLength-1]);

		/// <summary>
		/// Bulk response: first 8 bytes are a standard frame [ACK, addr, cmd, param, 0, 0, 0, ETX],
		/// followed by NumberOfDoorsPerController status bytes.
		/// ETX is at position [7], not at the end.
		/// </summary>
		protected static bool HasValidBulkResponseMarkers(ReadOnlySpan<byte> data)
			=> data.Length >= Constants.BulkResponseDataLength &&
				IsAcknowledge(data[0]) &&
				IsTextEnd(data[7]);
	}

	/// <summary>
	/// Single lock response format:
	/// [ACK=0x06, ControllerAddr, CommandCode, 0x00, Status, 0x00, 0x00, ETX=0x03]
	/// </summary>
	public abstract class SingleLockResponse : LockResponse {

		private readonly byte[] _data;
		protected SingleLockResponse(CommandId commandId, int controllerAddress, DoorStatusCode doorStatusCode)
			: base(commandId)
		{
			_controllerAddress = controllerAddress;
			this.DoorStatusCode = doorStatusCode;
			_data = new byte[Constants.SingleResponseDataLength];
			_data[0] = LockResponse.Acknowledge;
			_data[1] = (byte) controllerAddress;
			_data[2] = (byte) commandId;
			_data[3] = 0x00;
			_data[4] = (byte) this.DoorStatusCode;
			_data[5] = 0x00;
			_data[6] = 0x00;
			_data[Constants.SingleResponseDataLength-1] = LockResponse.TextEnd;
		}

		private readonly int _controllerAddress;
		public override int ControllerAddress => _controllerAddress;
		public DoorStatusCode DoorStatusCode { get; }
		public override ReadOnlyMemory<byte> Data => _data;

		protected static bool TryParse(ReadOnlySpan<byte> data, out int? controllerAddress, out DoorStatusCode? doorStatusCode) {
			controllerAddress = null;
			doorStatusCode = null;

			if(!HasValidSingleResponseMarkers(data)) {
				return false;
			}
			if (!IsValidCommandId(data[2])) {
				return false;
			}
			// data[3] should be 0x00 for single response
			if (!IsValidDoorStatusCode(data[4])) {
				return false;
			}
			controllerAddress = data[1];
			doorStatusCode = (DoorStatusCode)data[4];
			return true;
		}
	}

	public class OpenLockResponse : SingleLockResponse {
		public OpenLockResponse(int controllerAddress, DoorStatusCode doorStatusCode)
			: base(CommandId.OpenLock, controllerAddress, doorStatusCode) { }

		public static OpenLockResponse? TryParse(ReadOnlySpan<byte> data)
			=> TryParse(data, out var controllerAddress, out var doorStatusCode) ?
				new OpenLockResponse(controllerAddress!.Value, doorStatusCode!.Value) : null;
	}

	public class CheckLockStatusResponse : SingleLockResponse {
		public CheckLockStatusResponse(int controllerAddress, DoorStatusCode doorStatusCode)
			: base(CommandId.CheckLockStatus, controllerAddress, doorStatusCode) { }

		public static CheckLockStatusResponse? TryParse(ReadOnlySpan<byte> data)
			=> TryParse(data, out var controllerAddress, out var doorStatusCode) ?
				new CheckLockStatusResponse(controllerAddress!.Value, doorStatusCode!.Value) : null;
	}

	/// <summary>
	/// Bulk lock status response format:
	/// Bytes [0-7]:  standard frame [ACK, addr, cmd, param_echo, 0, 0, 0, ETX]
	/// Bytes [8-15]: 8 status bytes (one per door)
	/// Total length = 8 + NumberOfDoorsPerController = 16
	/// </summary>
	public class GetAllLocksStatusResponse : LockResponse {

		private readonly byte[] _data;

		public GetAllLocksStatusResponse(int controllerAddress, DoorStatusCode[] doorStatuses)
			: base(CommandId.GetAllLocksStatus)
		{
			if (doorStatuses.Length != Constants.NumberOfDoorsPerController) {
				throw new ArgumentException($"{nameof(GetAllLocksStatusResponse)} expects {Constants.NumberOfDoorsPerController} door statuses, got {doorStatuses.Length}");
			}
			_controllerAddress = controllerAddress;
			this.DoorStatusCodes = doorStatuses;
			_data = new byte[Constants.BulkResponseDataLength];
			_data[0] = Acknowledge;
			_data[1] = (byte) controllerAddress;
			_data[2] = (byte) CommandId.GetAllLocksStatus;
			_data[3] = 0xFF;  // parameter echo
			_data[7] = TextEnd;
			for (int i = 0; i < doorStatuses.Length; i++) {
				_data[8 + i] = (byte) doorStatuses[i];
			}
		}

		private readonly int _controllerAddress;
		public override int ControllerAddress => _controllerAddress;
		public DoorStatusCode[] DoorStatusCodes { get; }
		public override ReadOnlyMemory<byte> Data => _data;

		public static GetAllLocksStatusResponse? TryParse(ReadOnlySpan<byte> data)
		{
			if (!HasValidBulkResponseMarkers(data)) {
				return null;
			}
			if (!IsValidCommandId(data[2])) {
				return null;
			}

			// Status bytes are at positions [8] to [8 + NumberOfDoorsPerController - 1]
			int doorCount = Constants.NumberOfDoorsPerController;
			var doorStatuses = new DoorStatusCode[doorCount];
			for (int i = 0; i < doorCount; i++) {
				if (!IsValidDoorStatusCode(data[8 + i])) {
					return null;
				}
				doorStatuses[i] = (DoorStatusCode) data[8 + i];
			}

			return new GetAllLocksStatusResponse(data[1], doorStatuses);
		}
	}
}
