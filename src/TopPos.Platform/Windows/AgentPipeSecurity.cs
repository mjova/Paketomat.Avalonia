using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace TopPos.Platform.Windows
{
	/// <summary>
	/// Zaštita pipe-a (plan §4, ODL-050): eksplicitna DACL i provera identiteta klijenta preko Windows-a, bez sertifikata.
	/// v4: iz v2 <c>TopPos.Client.WPF/AgentCommunication</c> bez izmena koda; koristi je <see cref="WindowsAgentPipeTransport"/>.
	/// </summary>
	[SupportedOSPlatform("windows")]
	internal static class AgentPipeSecurity
	{
		/// <summary>
		/// DACL: dozvoljeni klijent čita i piše; nalog procesa (kiosk) pravi nove instance — Windows za svaku sledeću instancu
		/// proverava i čitanje/pisanje, pa ih nalog procesa mora imati. Zato se drugi procesi istog naloga mogu povezati na
		/// nivou DACL-a, a odbija ih provera identiteta (<see cref="GetClientSid"/>); takav proces je ionako van granice
		/// zaštite (plan §4). Mreža (<c>S-1-5-2</c>) je eksplicitno odbijena. Nema Everyone, Authenticated Users ni Users.
		/// <c>PipeOptions.CurrentUserOnly</c> se ne koristi jer bi isključio LocalSystem.
		/// </summary>
		public static PipeSecurity Create(SecurityIdentifier allowedClientSid)
		{
			using var current = WindowsIdentity.GetCurrent();
			var processUserSid = current.User ?? throw new InvalidOperationException("Nalog procesa nema SID.");

			var security = new PipeSecurity();
			security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
			security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
														PipeAccessRights.FullControl, AccessControlType.Deny));
			security.AddAccessRule(new PipeAccessRule(allowedClientSid,
														PipeAccessRights.ReadWrite | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize,
														AccessControlType.Allow));
			security.AddAccessRule(new PipeAccessRule(processUserSid,
														PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.ReadPermissions
														| PipeAccessRights.Synchronize,
														AccessControlType.Allow));
			return security;
		}

		/// <summary>
		/// SID naloga klijenta, preko impersonacije. Windows je dozvoljava tek kad je server pročitao bar jednu poruku sa
		/// pipe-a, pa se zove posle <c>ClientHello</c>. Proces bez <c>SeImpersonatePrivilege</c> (kiosk nalog) dobija token
		/// nivoa identifikacije — dovoljan za čitanje SID-a, a ne za rad u ime klijenta.
		/// </summary>
		public static SecurityIdentifier GetClientSid(NamedPipeServerStream pipe)
		{
			SecurityIdentifier? sid = null;
			Exception? error = null;
			pipe.RunAsClient(() => {
				try {
					sid = ReadThreadTokenUser();
				}
				catch (Exception ex) {
					error = ex;
				}
			});
			if (error != null) {
				throw new InvalidOperationException($"Identitet klijenta nije moguće pročitati: {error.Message}", error);
			}
			return sid!;
		}

		/// <summary>PID klijenta (za log); <c>null</c> ako ga Windows ne vrati.</summary>
		public static int? GetClientProcessId(NamedPipeServerStream pipe)
			=> GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint processId) ? (int) processId : null;

		// Token impersonirane niti; OpenAsSelf=true jer se token nivoa identifikacije ne može otvoriti u kontekstu klijenta.
		private static SecurityIdentifier ReadThreadTokenUser()
		{
			if (!OpenThreadToken(GetCurrentThread(), TokenQuery, openAsSelf: true, out var token)) {
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
			using (token) {
				GetTokenInformation(token, TokenInformationClassUser, IntPtr.Zero, 0, out int length);
				if (length <= 0) {
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}
				var buffer = Marshal.AllocHGlobal(length);
				try {
					if (!GetTokenInformation(token, TokenInformationClassUser, buffer, length, out _)) {
						throw new Win32Exception(Marshal.GetLastWin32Error());
					}
					// TOKEN_USER počinje sa SID_AND_ATTRIBUTES, čiji je prvi član pokazivač na SID.
					var sidPointer = Marshal.ReadIntPtr(buffer);
					return new SecurityIdentifier(sidPointer);
				}
				finally {
					Marshal.FreeHGlobal(buffer);
				}
			}
		}

		private const uint TokenQuery = 0x0008;
		private const int TokenInformationClassUser = 1;

		[DllImport("kernel32.dll")]
		private static extern IntPtr GetCurrentThread();

		[DllImport("advapi32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool OpenThreadToken(IntPtr threadHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out SafeAccessTokenHandle tokenHandle);

		[DllImport("advapi32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GetTokenInformation(SafeAccessTokenHandle tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
	}
}
