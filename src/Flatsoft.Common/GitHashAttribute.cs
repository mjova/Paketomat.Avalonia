namespace Flatsoft.Common
{
	[AttributeUsage(AttributeTargets.Assembly, Inherited = false, AllowMultiple = false)]
    public sealed class GitHashAttribute : Attribute
    {
        public string Hash { get; }
        public GitHashAttribute(string hsh)
        {
            this.Hash = hsh;
        }
    }
}
