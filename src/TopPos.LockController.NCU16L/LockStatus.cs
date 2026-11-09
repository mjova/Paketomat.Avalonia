namespace TopPos.LockController.NCU16L
{
	/// <summary>
	/// Represents the status of locks and infrared sensors on an NCU16L controller
	/// </summary>
	public class LockStatus
    {
        /// <summary>
        /// NCU16L hardware address (0x00-0x0F)
        /// </summary>
        public byte Address { get; set; }

        /// <summary>
        /// Lock states for locks 1-16 (2 bytes)
        /// Data layout:
        /// - LockStates[0] (response DATA1) = locks 1-8 with LSB-first bit ordering (bit 0 = lock 1, bit 7 = lock 8)
        /// - LockStates[1] (response DATA2) = locks 9-16 with LSB-first bit ordering (bit 0 = lock 9, bit 7 = lock 16)
        ///
        /// Lock logic (hardware default):
        /// - Bit=1 means CLOSED (lock hook detected)
        /// - Bit=0 means OPEN (lock hook released)
        ///
        /// This is NC (Normally Closed) switch behavior - the default for NCU16L hardware.
        /// The IsLockClosed() method handles this automatically.
        /// </summary>
        public byte[] LockStates { get; set; } = new byte[2];

        /// <summary>
        /// Infrared detection states for positions 1-16 (2 bytes)
        /// Data layout:
        /// - InfraredStates[0] (response DATA3) = positions 1-8 (bit 0 = pos 1, bit 7 = pos 8)
        /// - InfraredStates[1] (response DATA4) = positions 9-16 (bit 0 = pos 9, bit 7 = pos 16)
        ///
        /// Bit=1 means object detected, Bit=0 means clear
        /// </summary>
        public byte[] InfraredStates { get; set; } = new byte[2];

        /// <summary>
        /// Check if a specific lock is closed
        /// </summary>
        /// <param name="lockNumber">Lock number (1-16)</param>
        /// <returns>True if lock is closed, false if open</returns>
        public bool IsLockClosed(int lockNumber)
        {
            if (lockNumber < 1 || lockNumber > 16)
                throw new ArgumentOutOfRangeException(nameof(lockNumber), "Lock number must be 1-16");

            int byteIndex = (lockNumber - 1) / 8;     // Locks 1-8: byteIndex=0, Locks 9-16: byteIndex=1
            int bitIndex = (lockNumber - 1) % 8;      // Bit position 0-7

            // Data layout: LockStates[0] = locks 1-8, LockStates[1] = locks 9-16
            // LSB-first bit ordering: Lock 1 = bit 0, Lock 2 = bit 1, etc.
            bool bitSet = (LockStates[byteIndex] & (1 << bitIndex)) != 0;

            // NCU16L hardware default: Bit=1 means CLOSED, Bit=0 means OPEN
            // This is NC (Normally Closed) switch behavior - standard for all locks
            return bitSet;
        }

        /// <summary>
        /// Check if infrared sensor detects an object
        /// </summary>
        /// <param name="lockNumber">Lock/position number (1-16)</param>
        /// <returns>True if object detected, false if clear</returns>
        public bool HasInfraredDetection(int lockNumber)
        {
            if (lockNumber < 1 || lockNumber > 16)
                throw new ArgumentOutOfRangeException(nameof(lockNumber), "Lock number must be 1-16");

            int byteIndex = (lockNumber - 1) / 8;     // Positions 1-8: byteIndex=0, Positions 9-16: byteIndex=1
            int bitIndex = (lockNumber - 1) % 8;      // Bit position 0-7

            // Data layout: InfraredStates[0] = positions 1-8, InfraredStates[1] = positions 9-16
            // Bit=1 means object detected, Bit=0 means clear
            return (InfraredStates[byteIndex] & (1 << bitIndex)) != 0;
        }

        /// <summary>
        /// Get all closed locks as a list
        /// </summary>
        public List<int> GetClosedLocks()
        {
            var closedLocks = new List<int>();
            for (int i = 1; i <= 16; i++)
            {
                if (IsLockClosed(i))
                    closedLocks.Add(i);
            }
            return closedLocks;
        }

        /// <summary>
        /// Get all positions with infrared detection
        /// </summary>
        public List<int> GetDetectedPositions()
        {
            var detectedPositions = new List<int>();
            for (int i = 1; i <= 16; i++)
            {
                if (HasInfraredDetection(i))
                    detectedPositions.Add(i);
            }
            return detectedPositions;
        }
    }
}
