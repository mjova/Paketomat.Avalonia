namespace TopPos.LockController
{
	/// <summary>
	/// Ishod jednog slanja komande za otvaranje (<see cref="ILockControllerClient.OpenDoorOnce"/>). <see cref="Sent"/>=false:
	/// komanda sigurno nije poslata (veza ili port nisu otvoreni, broj vrata van opsega, otkazano pre slanja). <see cref="Sent"/>=true
	/// i <see cref="Status"/>=null: komanda je poslata, a stanje vrata se ne zna — vrata su možda otvorena.
	/// </summary>
	public readonly record struct DoorOpenAttempt(bool Sent, DoorStatusCode? Status)
	{
		public static readonly DoorOpenAttempt NotSent = new(false, null);
	}
}
