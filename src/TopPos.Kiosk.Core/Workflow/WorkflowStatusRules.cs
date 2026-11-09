using TopPos.Agent.Contracts;

namespace TopPos.Client.WPF.Workflow
{
	/// <summary>
	/// Pravila statusa izvedena iz workflow stanja — jedno mesto za „slobodan/zauzet“, ulogu i spremnost za OpenDoor
	/// (koriste ih <see cref="KioskStatusSource"/> i <see cref="StatusSemaphore"/>).
	/// </summary>
	public static class WorkflowStatusRules
	{
		/// <summary>Uloga dok kiosk ima vlasnika koji nije lokalna korisnička sesija.</summary>
		public const string ServiceRole = "Service";
		public const string AgentRole = "Agent";

		/// <summary>Slobodan je samo Start i Idle (pre prvog Start-a); svako drugo stanje, i virtuelno, znači zauzet.</summary>
		public static bool IsFree(WorkflowState state) => state is WorkflowState.Start or WorkflowState.Idle;

		/// <summary>
		/// Uloga po prefiksu stanja: Courier* = courier, Service* = service (tehničar), servisna sesija Agenta = agent; sve
		/// ostalo (recipient, sender) = client.
		/// </summary>
		public static string ResolveRole(WorkflowState state)
		{
			if (state == WorkflowState.RemoteService)
				return AgentRole;
			var name = state.ToString();
			if (name.StartsWith("Courier", StringComparison.Ordinal))
				return "Courier";
			if (name.StartsWith("Service", StringComparison.Ordinal))
				return ServiceRole;
			return "Client";
		}

		/// <summary>
		/// Da li kiosk trenutno može da primi OpenDoor i, ako ne može, stabilan razlog (<see cref="CannotAcceptReasons"/>).
		/// Redosled razloga: gašenje, oporavak, inicijalizacija, operativnost, zauzetost (Idle = još nije spreman), pa podrška
		/// komande. Ovo je samo prikaz spremnosti — komanda pri prijemu sama ponovo proverava i rezerviše kiosk.
		/// <paramref name="hasOwner"/>: kiosk ima vlasnika u <see cref="KioskOperationCoordinator"/> (i na Start-u).
		/// </summary>
		public static (bool CanAccept, string? Reason) EvaluateOpenDoor(string lifecycle, bool? operational, WorkflowState state, bool openDoorSupported,
																		bool hasOwner = false)
		{
			if (lifecycle == KioskLifecycles.ShuttingDown)
				return (false, CannotAcceptReasons.ShuttingDown);
			if (lifecycle == KioskLifecycles.RecoveryRequired)
				return (false, CannotAcceptReasons.RecoveryRequired);
			if (lifecycle != KioskLifecycles.Ready)
				return (false, CannotAcceptReasons.NotReady);
			if (operational != true)
				return (false, CannotAcceptReasons.NotOperational);
			if (state == WorkflowState.Idle)
				return (false, CannotAcceptReasons.NotReady);
			if (state != WorkflowState.Start || hasOwner)
				return (false, CannotAcceptReasons.Busy);
			if (!openDoorSupported)
				return (false, CannotAcceptReasons.NotSupported);
			return (true, null);
		}
	}
}
