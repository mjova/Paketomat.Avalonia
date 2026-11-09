namespace TopPos.LockController.NCU24L
{
	/// <summary>
	/// NCU24L Protocol constants
	/// </summary>
	public static class NCU24LProtocol
	{
		/// <summary>
		/// Start of transmission byte (0x02)
		/// </summary>
		public const byte STX = 0x02;

		/// <summary>
		/// End of transmission byte (0x03)
		/// </summary>
		public const byte ETX = 0x03;

		/// <summary>
		/// Broadcast address for all NCU24L on bus (0x64)
		/// Also used as LOCKNUM=0x64 to target all locks
		/// </summary>
		public const byte BROADCAST_ADDRESS = 0x64;

		/// <summary>
		/// Lock number value meaning "all locks" (0x64)
		/// </summary>
		public const byte ALL_LOCKS = 0x64;

		/// <summary>
		/// Default ASK byte value for commands (0x00)
		/// </summary>
		public const byte ASK_DEFAULT = 0x00;

		/// <summary>
		/// Number of locks per NCU24L unit
		/// </summary>
		public const int LOCKS_PER_UNIT = 24;

		/// <summary>
		/// Maximum number of NCU24L units on RS485 bus
		/// </summary>
		public const int MAX_UNITS_ON_BUS = 10;

		/// <summary>
		/// Default TCP port for BU module
		/// </summary>
		public const int DEFAULT_PORT = 4001;

		/// <summary>
		/// Default unlock time in milliseconds (550ms)
		/// </summary>
		public const int DEFAULT_UNLOCK_TIME_MS = 550;

		/// <summary>
		/// Maximum unlock time in milliseconds (60s)
		/// </summary>
		public const int MAX_UNLOCK_TIME_MS = 60000;

		/// <summary>
		/// Default baud rate for RS485 communication
		/// </summary>
		public const int DEFAULT_BAUD_RATE = 19200;

		/// <summary>
		/// Minimum packet length: STX+ADDR+LOCKNUM+CMD+ASK+DATALEN+ETX+SUM = 8 bytes
		/// </summary>
		public const int MIN_PACKET_LENGTH = 8;

		/// <summary>
		/// Wait time between commands in milliseconds
		/// </summary>
		public const double INTER_COMMAND_DELAY_MS = 200;
	}

	/// <summary>
	/// NCU24L command codes
	/// </summary>
	public enum NCU24LCommand : byte
	{
		/// <summary>
		/// Get lock hook status (0x80)
		/// </summary>
		GetState = 0x80,

		/// <summary>
		/// Unlock a specific lock or all locks (0x81)
		/// Use LOCKNUM=0x64 for all locks, ADDR=0x64 for all NCUs
		/// </summary>
		Unlock = 0x81,

		/// <summary>
		/// Set or query unlock time (0x82)
		/// </summary>
		SetQueryUnlockTime = 0x82,

		/// <summary>
		/// Set or query baud rate (0x83)
		/// </summary>
		SetQueryBaudRate = 0x83,

		/// <summary>
		/// Set or query delay unlock time (0x84)
		/// </summary>
		SetQueryDelayUnlockTime = 0x84,

		/// <summary>
		/// Get short circuit overcurrent status (0x87)
		/// </summary>
		GetShortCircuitStatus = 0x87,

		/// <summary>
		/// Get open circuit no-current status (0x88)
		/// </summary>
		GetOpenCircuitStatus = 0x88,

		/// <summary>
		/// Initialize to factory defaults (0x8E)
		/// </summary>
		Initialize = 0x8E,

		/// <summary>
		/// Query software and hardware version (0x8F)
		/// </summary>
		QueryVersion = 0x8F
	}

	/// <summary>
	/// NCU24L response ASK codes
	/// </summary>
	public enum NCU24LResponseCode : byte
	{
		/// <summary>
		/// Default value when sending (0x00)
		/// </summary>
		Default = 0x00,

		/// <summary>
		/// Command executed successfully (0x10)
		/// </summary>
		Success = 0x10,

		/// <summary>
		/// Command execution failed (0x11)
		/// </summary>
		Failed = 0x11,

		/// <summary>
		/// Command timed out (0x12)
		/// </summary>
		Timeout = 0x12,

		/// <summary>
		/// Unknown command (0x13)
		/// </summary>
		UnknownCommand = 0x13,

		/// <summary>
		/// Data verification failure (0x14)
		/// </summary>
		DataVerificationFailure = 0x14
	}
}
