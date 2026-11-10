using Xunit;

namespace TopPos.E2E.Tests
{
	/// <summary>
	/// Svi E2E testovi u jednoj kolekciji: izvršavaju se serijski (isti portovi, single-instance mutex-i, jedan desktop).
	/// </summary>
	[CollectionDefinition(Name, DisableParallelization = true)]
	public sealed class E2ECollection
	{
		public const string Name = "E2E";
	}
}
