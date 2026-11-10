namespace TopPos.LockController
{
	public class LockBox
	{
		public LockBox(int number, bool isLocked = true) 
		{ 
			this.Number = number;
			this.IsLocked = isLocked;
		}

        public int Number { get; }

		public bool IsLocked { get; set; }	
    }

	public class LockModule
	{
		private readonly LockBox[] _boxes;
        public LockModule(IEnumerable<LockBox> boxes)
        {
            _boxes = boxes.ToArray();
        }

		public IReadOnlyList<LockBox> Boxes => _boxes;

		public bool IsOperational { get; set; } = true;
    }

	public class Locker
	{
		private readonly LockModule[] _modules;

        public Locker(IEnumerable<LockModule> modules)
        {
            _modules = modules.ToArray();
        }

        public IReadOnlyList<LockModule> Modules => _modules;

		public static Locker MakeLocker(int numModules, int numBoxesPerModule, bool isBoxInitiallyLocked = true)
		{
			var modules = new List<LockModule>();
			for (int m = 0; m < numModules; m++) 
			{
				var boxes = Enumerable.Range(0, numBoxesPerModule)
									.Select(idx => new LockBox((m * numBoxesPerModule) + idx + 1, isLocked: isBoxInitiallyLocked)).ToArray();
				modules.Add(new LockModule(boxes));
			}
			return new Locker(modules);
		}
    }
}
