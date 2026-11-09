namespace TopPos.Platform
{
	/// <summary>Samo jedna instanca kioska na uređaju.</summary>
	public interface ISingleInstanceGuard
	{
		/// <summary>
		/// Zauzima mesto jedine instance; <c>null</c> = druga instanca već radi. Mesto se drži dok se vraćeni objekat ne oslobodi
		/// ili proces ne završi (i pri padu).
		/// </summary>
		IDisposable? TryAcquire();
	}
}