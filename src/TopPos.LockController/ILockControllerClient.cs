namespace TopPos.LockController
{
	public interface ILockControllerClient
	{

		/// <summary>
		/// Number of doors per controller module - depends on controller type
		/// Serial controller: 8 doors per module
		/// NCU16L controller: 16 doors per module
		/// </summary>
		int NumberOfDoorsPerController { get; }

		// OpenDoorSingle - 3 overloads
		ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, CancellationToken cancellationToken);
		ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken);
		ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken);

		/// <summary>
		/// Otvaranje jednih vrata sa najviše jednim slanjem komande: bez ponavljanja i kad odgovor ne stigne (za razliku od
		/// <see cref="OpenDoorSingle(int, CancellationToken)"/>, gde serijski kontroleri ponavljaju slanje po <c>RetryCount</c>).
		/// Rezultat kaže i da li je komanda poslata. Za komande Agenta, gde se otvaranje ne sme ponoviti.
		/// </summary>
		ValueTask<DoorOpenAttempt> OpenDoorOnce(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken);

		// CheckDoorSingle - 3 overloads
		ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, CancellationToken cancellationToken);
		ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken);
		ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken);

		// CheckDoorMultiple - 3 overloads
		ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, CancellationToken cancellationToken);
		ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken);
		ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken);

		/*
		// OpenDoorMultiple - 3 overloads
		ValueTask<DoorStatusCode[]?> OpenDoorMultiple(int moduleNumber, CancellationToken cancellationToken);
		ValueTask<DoorStatusCode[]?> OpenDoorMultiple(int moduleNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken);
		ValueTask<DoorStatusCode[]?> OpenDoorMultiple(int moduleNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken);
		*/
	}
}
