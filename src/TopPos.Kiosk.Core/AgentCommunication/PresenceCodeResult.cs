namespace TopPos.Kiosk.AgentCommunication
{
	// v4: enum iz v2 AgentCommunication/RemoteServiceSession.cs (Agent kanal dolazi u F2; tada se ovaj fajl briše).
	/// <summary>Ishod unosa koda prisustva.</summary>
	public enum PresenceCodeResult
	{
		Accepted,
		/// <summary>Pogrešan kod; ostalo je još pokušaja.</summary>
		Rejected,
		/// <summary>Pogrešan kod iz poslednjeg pokušaja: sesija je završena.</summary>
		Failed,
		/// <summary>Sesija ne čeka prijavu (serviser je već prijavljen, ili je sesija završena).</summary>
		NotAwaiting,
	}
}
