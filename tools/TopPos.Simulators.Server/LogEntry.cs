namespace TopPos.ServerSimulator
{
	public class LogEntry
	{
		// Redni broj po završetku obrade (rastući); kontrolni API vraća pozive posle zadatog broja.
		public long Seq { get; set; }
		public DateTime Timestamp { get; set; } = DateTime.Now;
		public string Endpoint { get; set; } = string.Empty;
		public string Method { get; set; } = string.Empty;
		public Dictionary<string, string?> Query { get; set; } = [];
		public string RequestBody { get; set; } = string.Empty;
		public string ResponseBody { get; set; } = string.Empty;
		public int StatusCode { get; set; } = 200;
		public bool IsError { get; set; }

		public string Detail { get; set; } = string.Empty;

		public string Display => string.IsNullOrEmpty(Detail)
			? $"[{Timestamp:HH:mm:ss.fff}] {Method} {Endpoint} -> {StatusCode}"
			: $"[{Timestamp:HH:mm:ss.fff}] {Method} {Endpoint} ({Detail}) -> {StatusCode}";
	}
}
