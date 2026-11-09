namespace TopPos.LockController.Serial
{
	public abstract class LockResponse {
		public const byte Acknowledge = 0x06;
		public const byte TextEnd = 0x03;

		protected LockResponse(CommandId commandId)
        {
            this.CommandId = commandId;
		}

		public CommandId CommandId { get; }
		public abstract int Number { get; }	// Depending on the command, a door or a module number
		public abstract ReadOnlyMemory<byte> Data { get; }

		public override string ToString() => $"{this.CommandId} [{this.Number}]";

		protected static bool IsAcknowledge(byte b) => b == Acknowledge;
		protected static bool IsTextEnd(byte b) => b == TextEnd;
		protected static bool IsValidDoorNumber(byte b) => b >= Constants.MinDoorNumber && b <= Constants.MaxDoorNumber;
		protected static bool IsValidModuleNumber(byte b) => b >= Constants.MinModuleNumber && b <= Constants.MaxModuleNumber;
		protected static bool IsValidCommandId(byte b) => Enum.IsDefined((CommandId)b);
		protected static bool IsValidDoorStatusCode(byte b) => true;    // 00=closed, 1-FF open, so all values are valid
        protected static bool IsValidLedStatus(byte b) => b == 0x00 || b == 0x01;

		protected static bool HasValidSingleResponseMarkers(ReadOnlySpan<byte> data)
			=> data.Length >= Constants.SingleResponseDataLength &&
				IsAcknowledge(data[0]) &&
				IsTextEnd(data[Constants.SingleResponseDataLength-1]);
		protected static bool HasValidMultiResponseMarkers(ReadOnlySpan<byte> data)
			=> data.Length >= Constants.MultiResponseDataLength &&
				IsAcknowledge(data[0]) &&
				IsTextEnd(data[7]) &&
				IsAcknowledge(data[16]) &&
				IsTextEnd(data[Constants.MultiResponseDataLength-1]);
	}

	public abstract class SingleDoorResponse : LockResponse {

		private readonly byte[] _data;
		protected SingleDoorResponse(CommandId commandId, int doorNumber, DoorStatusCode doorStatusCode)
			: base(commandId) 
		{
			this.DoorNumber = doorNumber;
			this.DoorStatusCode = doorStatusCode;
			_data = new byte[Constants.SingleResponseDataLength];
			_data[0] = LockResponse.Acknowledge;
			_data[1] = (byte) this.DoorNumber;
			_data[2] = (byte) commandId;
			_data[3] = (byte) this.DoorStatusCode;
			_data[Constants.SingleResponseDataLength-1] = LockResponse.TextEnd;
		}

		public int DoorNumber { get; }
		public override int Number => this.DoorNumber;
		public DoorStatusCode DoorStatusCode { get; }
		public override ReadOnlyMemory<byte> Data => _data;

		protected static bool TryParse(ReadOnlySpan<byte> data, out int? doorNumber, out DoorStatusCode? doorStatusCode) {
			doorNumber = null;
			doorStatusCode = null;

			if(!HasValidSingleResponseMarkers(data)) {
				return false;
			}
			if(!IsValidDoorNumber(data[1])) {
				return false;
			}
			if (!IsValidCommandId(data[2])) {
				return false;
			}
			if (!IsValidDoorStatusCode(data[3])) {
				return false;
			}
			doorNumber = data[1];
			doorStatusCode = (DoorStatusCode)data[3];
			return true;
		}
	}

	public class OpenDoorSingleResponse:SingleDoorResponse {
		public OpenDoorSingleResponse(int doorNumber, DoorStatusCode doorStatusCode)
			: base(CommandId.OpenDoorSingle, doorNumber, doorStatusCode) { }

		public static OpenDoorSingleResponse? TryParse(ReadOnlySpan<byte> data)
			=> TryParse(data, out var doorNumber, out var doorStatusCode) ?
				new OpenDoorSingleResponse(doorNumber!.Value, doorStatusCode!.Value) : null;
	}

	public abstract class MultipleDoorResponse:LockResponse {

		private readonly byte[] _data;

		protected MultipleDoorResponse(CommandId commandId, int module, DoorStatusCode[] doorStatuses)
			: base(commandId) 
		{ 
			if (doorStatuses.Length != 8) {
				throw new ArgumentException($"{nameof(MultipleDoorResponse)} expects 8 door statuses, got {doorStatuses.Length}");
			}
			this.ModuleNumber = module;
			_data = new byte[Constants.MultiResponseDataLength];
			_data[0] = Acknowledge;
			_data[1] = (byte) module;
			_data[2] = (byte)commandId;
			_data[7] = TextEnd;
			for (int i = 0; i < doorStatuses.Length; i++) {
				_data[8 + i] = (byte) doorStatuses[i];
			}
			_data[16] = Acknowledge;
			_data[17] = (byte) module;
			_data[18] = (byte) commandId;
			_data[Constants.MultiResponseDataLength-1] = TextEnd;
		}

		public int ModuleNumber { get; }
		public override int Number => this.ModuleNumber;
		public override ReadOnlyMemory<byte> Data => _data;

		private static DoorStatusCode[]? ToDoorStatusCodes(ReadOnlySpan<byte> data) {
			if(data.IsEmpty) {
				return Array.Empty<DoorStatusCode>();
			}
			DoorStatusCode[] doorStatusCodes = new DoorStatusCode[data.Length];
			for(int i = 0; i < data.Length; i++) {
				if(IsValidDoorStatusCode(data[i])) {
					doorStatusCodes[i] = (DoorStatusCode)data[i];
				} else {
					return null;
				}
			}
			return doorStatusCodes;
		}

		public static bool TryParse(ReadOnlySpan<byte> data, out int? module, out DoorStatusCode[]? doorStatusCodes) {
			module = null;
			doorStatusCodes = null;
			if(!HasValidMultiResponseMarkers(data)) {
				return false;
			}
			module = data[1];
			// Neki kontroleri vraćaju broj vrata iz upita, a ne broj modula,
			// pa validacija broja modula može pasti iako je odgovor validan.
			// Zbog toga ne validiramo vrednost modula (data[1],
			// ali proveravamo da je isti u oba dela odgovora.

			if (!IsValidCommandId(data[2])) {	
				return false;
			}

			if(data[1] != data[17]) {
				return false;   // module number must be the same in both parts of the response
			}

			if((data[2] != data[18])) {
				return false;   // command id must be the same in both parts of the response
			}

			doorStatusCodes = ToDoorStatusCodes(data.Slice(8, 8));
			return doorStatusCodes != null;
		}
	}

	public class OpenDoorMultipleResponse:MultipleDoorResponse {
		public OpenDoorMultipleResponse(int module, DoorStatusCode[] doorStatusCodes) 
			: base(CommandId.OpenDoorMultiple, module, doorStatusCodes) {

			this.DoorStatusCodes = doorStatusCodes;
		}

		public IEnumerable<DoorStatusCode>? DoorStatusCodes { get; }

		public static OpenDoorMultipleResponse? TryParse(ReadOnlySpan<byte> data)
	=> TryParse(data, out var module, out var doorStatusCodes) ?
		new OpenDoorMultipleResponse(module!.Value, doorStatusCodes!) : null;
	}
	public class PowerResponse:LockResponse {
		public PowerResponse()
	: base(CommandId.Power) { }

		public override int Number => throw new NotImplementedException();
		public override ReadOnlyMemory<byte> Data => throw new NotImplementedException();	

		public static PowerResponse? TryParse(ReadOnlySpan<byte> data) => null; // TODO
	}

	public class CheckDoorStatusSingleResponse:SingleDoorResponse {
		public CheckDoorStatusSingleResponse(int doorNumber, DoorStatusCode doorStatusCode)
	: base(CommandId.CheckDoorStatusSingle, doorNumber, doorStatusCode) { }

		public static CheckDoorStatusSingleResponse? TryParse(ReadOnlySpan<byte> data)
			=> TryParse(data, out var doorNumber, out var doorStatusCode) ?
				new CheckDoorStatusSingleResponse(doorNumber!.Value, doorStatusCode!.Value) : null;
	}

	public class CheckDoorStatusMultipleResponse:MultipleDoorResponse {
		public CheckDoorStatusMultipleResponse(int module, DoorStatusCode[] doorStatusCodes) 
			: base(CommandId.CheckDoorStatusMultiple, module, doorStatusCodes) {
      this.DoorStatusCodes = doorStatusCodes;   // nije isto kod SingleDoorResponse, tamo je prazan ctor
		}

		public IEnumerable<DoorStatusCode>? DoorStatusCodes { get; }

		public static CheckDoorStatusMultipleResponse? TryParse(ReadOnlySpan<byte> data)
	=> TryParse(data, out var module, out var doorStatusCodes) ?
		new CheckDoorStatusMultipleResponse(module!.Value, doorStatusCodes!) : null;
	}

	public class SetTimeResponse:LockResponse {

		public SetTimeResponse()
			: base(CommandId.SetTime) { }

		public override int Number => throw new NotImplementedException();
		public override ReadOnlyMemory<byte> Data => throw new NotImplementedException();	
		public static SetTimeResponse? TryParse(ReadOnlySpan<byte> data) => null; // TODO
	}

	public class BeepResponse:LockResponse {

		public BeepResponse()
			: base(CommandId.Beep) {}
		
		public override int Number => throw new NotImplementedException();
		public override ReadOnlyMemory<byte> Data => throw new NotImplementedException();	
		public static BeepResponse? TryParse(ReadOnlySpan<byte> data) => null;  // TODO
	}

	public class LedLightModuleResponse : LockResponse {

		public LedLightModuleResponse(int moduleNumber, int ledStatus)
			: base(CommandId.LedLight)
		{
			ModuleNumber = moduleNumber;
			LedStatus=ledStatus;
		}

		public int ModuleNumber { get; }

		public override int Number => this.ModuleNumber;
		public int LedStatus { get; }
		public override ReadOnlyMemory<byte> Data => throw new NotImplementedException();
		
		public static LedLightModuleResponse? TryParse(ReadOnlySpan<byte> data)
			=> TryParse(data, out var moduleNumber, out var ledStatus) ?
				new LedLightModuleResponse(moduleNumber!.Value, ledStatus!.Value) : null;

		public static bool TryParse(ReadOnlySpan<byte> data, out int? module, out int? ledStatus)
		{
			module = null;
			ledStatus = null;

			if(!HasValidSingleResponseMarkers(data)) {
				return false;
			}
			module = data[1];
			if (!IsValidModuleNumber(data[1])) {	
				return false;
			}
			if (((CommandId)data[2]) != CommandId.LedLight) {
				return false;
			}
			ledStatus = data[3];
			if(!IsValidLedStatus(data[3])) {
				return false;
			}

			return true;
		}
	}

	public class RestartModuleResponse : LockResponse
	{
		public RestartModuleResponse(int moduleNumber)
			: base(CommandId.Restart)
		{
			ModuleNumber = moduleNumber;
		}
		public int ModuleNumber { get; }
		public override int Number => this.ModuleNumber;

		public override ReadOnlyMemory<byte> Data => throw new NotImplementedException();
		
		public static RestartModuleResponse? TryParse(ReadOnlySpan<byte> data)
			=> TryParse(data, out var moduleNumber) ?
				new RestartModuleResponse(moduleNumber!.Value) : null;

		public static bool TryParse(ReadOnlySpan<byte> data, out int? module)
		{
			module = null;
			if(!HasValidSingleResponseMarkers(data)) {
				return false;
			}
			module = data[1];
			if(!IsValidModuleNumber(data[1])) {
				return false;
			}
			return true;
		}

	}
}