namespace TopPos.LockController.Serial
{
	public static class Constants
	{
		public const int MinDoorNumber = 0x01;
		public const int MaxDoorNumber = 0x7f;
		public const int MinModuleNumber = 0x01;
		public const int MaxModuleNumber = 0x10;
		public const int NumberOfDoorsPerController = 0x08;
		public const int CommandDataLength = 8;
		public const int SingleResponseDataLength = 8;
		public const int MultiResponseDataLength = 24;  // 3 x 8
	}
}
