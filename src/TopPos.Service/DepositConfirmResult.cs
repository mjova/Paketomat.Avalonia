namespace TopPos.Service
{
	/// <summary>
	/// Ishod potvrde ostavljanja (<see cref="KurirPunjenjePozicijePotvrda"/>). Server poziciju zauzima tek potvrdom, pa od
	/// ishoda zavisi šta kurir/pošiljalac sme da uradi sa paketom koji je već u zatvorenim vratima.
	/// </summary>
	public enum DepositConfirmResult
	{
		/// <summary>Server je potvrdio ostavljanje u ista vrata.</summary>
		Confirmed,

		/// <summary>Server sigurno nije upisao potvrdu (nema veze, ili odgovor koji znači da zahtev nije obrađen) — može se ponoviti.</summary>
		NotDelivered,

		/// <summary>Server je odgovorio i odbio potvrdu (400, ili 200 sa drugim vratima) — ponavljanje ne menja odgovor.</summary>
		Rejected,

		/// <summary>Ne zna se da li je server upisao potvrdu (odgovor nije stigao, ili je mogao doći posle obrade).</summary>
		Unknown,
	}
}
