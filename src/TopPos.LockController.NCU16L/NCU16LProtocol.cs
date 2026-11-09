namespace TopPos.LockController.NCU16L
{
    /// <summary>
    /// NCU16L Protocol constants
    /// </summary>
    public static class NCU16LProtocol
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
        /// Broadcast address for all NCU controllers on bus (0xF0)
        /// </summary>
        public const byte BROADCAST_ADDRESS = 0xF0;

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
		/// Wait time between commands in milliseconds (500ms)
		/// </summary>
		public const double INTER_COMMAND_DELAY_MS= 200;
	}

    /// <summary>
    /// NCU16L command codes sent to the controller
    /// </summary>
    public enum NCU16LCommand : byte
    {
		/// <summary>
		/// Get the status of a single NCU16L (0x30)
		/// </summary>
		GetState = 0x30,

		/// <summary>
		/// Unlock a specific lock (0x31)
		/// </summary>
		Unlock = 0x31,

		/// <summary>
		/// Get the status of all NCU16L on RS485 bus (0x32)
		/// </summary>
		GetAllCUStates = 0x32,

		/// <summary>
		/// Open all locks on a single NCU16L (0x33)
		/// </summary>
		OpenAllLocks = 0x33,

		/// <summary>
		/// Set or query the unlock time (0x37)
		/// </summary>
		SetQueryUnlockTime = 0x37,

		/// <summary>
		/// Set or query the delay unlock time (0x39)
		/// </summary>
		SetQueryDelayUnlockTime = 0x39,

		/// <summary>
		/// Get current detection status (short circuit/open circuit) (0x3A)
		/// </summary>
		CurrentDetectionStatus = 0x3A
	}

    /// <summary>
    /// NCU16L response codes received from the controller
    /// </summary>
    public enum NCU16LResponse : byte
    {
        /// <summary>
        /// Return lock hook and infrared detection data for single NCU16L (0x35)
        /// </summary>
        ReturnSingleData = 0x35,

        /// <summary>
        /// Return lock hook and infrared detection data for all NCU16L on the bus (0x36)
        /// </summary>
        ReturnAllData = 0x36
    }
}
