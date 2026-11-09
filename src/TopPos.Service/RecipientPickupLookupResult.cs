namespace TopPos.Service
{
	/// <summary>
	/// Ishod provere preuzimanja (<see cref="PrimaocPraznjenjePozicije"/>). Razlikuje odbijanje od izostanka odgovora, jer se posle
	/// izostanka odgovora isti zahtev može odmah ponoviti (provera ne rezerviše vrata).
	/// </summary>
	public enum RecipientPickupLookupResult
	{
		/// <summary>Server je odgovorio sa brojem vrata i iznosom za uplatu (vrata 0 uz iznos &gt; 0: treba platiti).</summary>
		Found,

		/// <summary>Server je odbio zahtev (400), uz poruku kad je ima — ponavljanje ne menja odgovor.</summary>
		Rejected,

		/// <summary>Ispravan odgovor nije stigao: veza, isteklo vreme, drugi status ili 200 sa telom koje nije JSON.</summary>
		NoResponse,
	}
}
