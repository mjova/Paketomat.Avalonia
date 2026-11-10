namespace TopPos.ServerSimulator
{
	/// <summary>
	/// Zadata greška za jedan endpoint (kontrolni API, POST /_sim/faults): kašnjenje pre obrade (DelayMs) i/ili
	/// odgovor umesto obrade — 400 sa porukom (Error), ili zadati status (Status, npr. 503 za prolaznu grešku servera; zahtev
	/// se ne obrađuje, pa npr. paket ostaje neplaćen). Count = broj zahteva na koje deluje; 0 = dok se ne obriše.
	/// AfterProcessing = zahtev se obradi, pa se tek onda vrati Status — izgubljen odgovor (npr. 502 proxy-ja posle obrade).
	/// </summary>
	public sealed record ServerFault(string Endpoint, int DelayMs = 0, string? Error = null, int Count = 1, int? Status = null, bool AfterProcessing = false);

	/// <summary>Odgovor sa zadatim statusom umesto obrade (<see cref="ServerFault.Status"/>).</summary>
	public sealed class SimulatedStatusException(int statusCode, string message) : Exception(message)
	{
		public int StatusCode { get; } = statusCode;
	}

	public sealed class ServerFaults
	{
		private readonly List<ServerFault> _armed = [];
		private readonly Lock _lock = new();

		public void Arm(ServerFault fault)
		{
			if (string.IsNullOrWhiteSpace(fault.Endpoint))
				throw new ArgumentException("Endpoint je obavezan");
			if (fault.DelayMs < 0 || fault.Count < 0)
				throw new ArgumentException("DelayMs i Count ne smeju biti negativni");
			if (fault.DelayMs == 0 && fault.Error == null && fault.Status == null)
				throw new ArgumentException("Zadati DelayMs, Error i/ili Status");
			if (fault.Status is < 400 or > 599)
				throw new ArgumentException("Status mora biti 4xx ili 5xx");
			if (fault.AfterProcessing && fault.Status == null)
				throw new ArgumentException("AfterProcessing zahteva Status");
			lock (_lock)
			{
				_armed.RemoveAll(f => string.Equals(f.Endpoint, fault.Endpoint, StringComparison.OrdinalIgnoreCase));
				_armed.Add(fault);
			}
		}

		public void Clear()
		{
			lock (_lock)
			{
				_armed.Clear();
			}
		}

		public List<ServerFault> Snapshot()
		{
			lock (_lock)
			{
				return [.. _armed];
			}
		}

		/// <summary>Greška za ovaj zahtev (troši jedan od Count), ili null.</summary>
		public ServerFault? Take(string endpoint)
		{
			lock (_lock)
			{
				var index = _armed.FindIndex(f => string.Equals(f.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
				if (index < 0)
					return null;
				var fault = _armed[index];
				if (fault.Count == 1)
					_armed.RemoveAt(index);
				else if (fault.Count > 1)
					_armed[index] = fault with { Count = fault.Count - 1 };
				return fault;
			}
		}
	}
}
