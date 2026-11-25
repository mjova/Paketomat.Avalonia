namespace TopPos.Kiosk.AgentCommunication
{
	/// <summary>
	/// v4: tekst koji šalje klijent (Agent) ide u log samo skraćen i bez kontrolnih znakova. U v2 je ovo
	/// <c>AgentPipeConnection.Sanitize</c>; izdvojeno jer transport (named pipe / Unix socket) dolazi u F2, a obrada komandi
	/// ga koristi i bez njega.
	/// </summary>
	internal static class AgentLogText
	{
		internal static string Sanitize(string? value, int maxLength = 80)
		{
			if (string.IsNullOrEmpty(value)) {
				return "";
			}
			var trimmed = value.Length > maxLength ? value[..maxLength] : value;
			return new string(trimmed.Select(c => char.IsControl(c) ? '?' : c).ToArray());
		}
	}
}