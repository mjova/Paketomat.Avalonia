using System.Diagnostics;
using System.Drawing;
using System.Runtime.Versioning;
using System.Text;
using Tmds.DBus.Protocol;
using Xunit;

namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>
	/// v4: Linux implementacija <see cref="KioskUi"/> preko AT-SPI (D-Bus, Tmds.DBus.Protocol kao Avalonia). Avalonia izlaže
	/// AutomationId kao <c>AccessibleId</c> (ručno provereno na Debian VM-u sa pyatspi, 9.11.2026). Pritisak je
	/// <c>Action.DoAction(0)</c> (kao UIA Invoke — ne prolazi kroz miš); omogućen = stanje SENSITIVE, prikazan = VISIBLE ili
	/// SHOWING. Pravi miš (Home, dug pritisak) nema prenosiv put na Wayland-u — takvi testovi se na Linuxu preskaču.
	/// </summary>
	[SupportedOSPlatform("linux")]
	public sealed class AtSpiKioskUi : KioskUi
	{
		private const string AccessibleInterface = "org.a11y.atspi.Accessible";
		private const string RootPath = "/org/a11y/atspi/accessible/root";
		private const string MouseSkipReason = "Pravi miš (Home, dug pritisak) na Linuxu nije podržan u E2E (AT-SPI nema klik mišem, Wayland ne dozvoljava ubacivanje).";

		// AtspiStateType (at-spi2-core atspi-constants.h)
		private const int StateEnabled = 8;
		private const int StateSensitive = 24;
		private const int StateShowing = 25;
		private const int StateVisible = 30;

		private static readonly TimeSpan __callTimeout = TimeSpan.FromSeconds(10);

		private readonly DBusConnection _bus;
		private readonly string _application;
		private readonly Action? _guard;
		private readonly Action<string> _trace;

		private AtSpiKioskUi(DBusConnection bus, string application, Action? guard, Action<string> trace)
		{
			_bus = bus;
			_application = application;
			_guard = guard;
			_trace = trace;
		}

		private sealed record Node(string Bus, string Path);

		/// <summary>
		/// Uključuje pristupačnost u sesiji (<c>org.a11y.Status.IsEnabled</c>) pre starta kioska — bez toga aplikacije ne
		/// moraju da se registruju na AT-SPI magistrali. Podešavanje sesije ostaje uključeno posle testova.
		/// </summary>
		public static async Task EnableAccessibilityAsync(Action<string> trace)
		{
			using var session = new DBusConnection(DBusAddress.Session ?? throw new E2EException("Nema D-Bus sesije (DBUS_SESSION_BUS_ADDRESS)."));
			await session.ConnectAsync().ConfigureAwait(false);
			bool enabled = await CallAsync(session, "org.a11y.Bus", "/org/a11y/bus", "org.freedesktop.DBus.Properties", "Get", "ss",
											(ref MessageWriter w) => { w.WriteString("org.a11y.Status"); w.WriteString("IsEnabled"); },
											(m, _) => m.GetBodyReader().ReadVariantValue().GetBool()).ConfigureAwait(false);
			if (!enabled) {
				await CallAsync<bool>(session, "org.a11y.Bus", "/org/a11y/bus", "org.freedesktop.DBus.Properties", "Set", "ssv",
										(ref MessageWriter w) => { w.WriteString("org.a11y.Status"); w.WriteString("IsEnabled"); w.WriteVariantBool(true); },
										(_, _) => true).ConfigureAwait(false);
				trace("AT-SPI: pristupačnost uključena u sesiji (org.a11y.Status.IsEnabled)");
			}
		}

		/// <summary>Čeka da se kiosk (po PID-u) pojavi u AT-SPI registru i da ima prozor.</summary>
		public static new async Task<KioskUi> AttachAsync(Process process, TimeSpan timeout, Action? guard, Action<string> trace)
		{
			var address = await GetAccessibilityBusAddressAsync().ConfigureAwait(false);
			var bus = new DBusConnection(address);
			try {
				await bus.ConnectAsync().ConfigureAwait(false);
				var application = await Poll.UntilAsync(() => FindApplicationAsync(bus, process.Id), timeout, "kiosk u AT-SPI registru", guard).ConfigureAwait(false);
				var ui = new AtSpiKioskUi(bus, application, guard, trace);
				var window = await Poll.UntilAsync(async () => (await ui.ChildrenAsync(new Node(application, RootPath)).ConfigureAwait(false)).FirstOrDefault(),
													timeout, "prozor kioska u AT-SPI stablu", guard).ConfigureAwait(false);
				trace($"UI: AT-SPI prozor \"{await ui.NameAsync(window).ConfigureAwait(false)}\" ({application})");
				return ui;
			}
			catch {
				bus.Dispose();
				throw;
			}
		}

		private static async Task<string> GetAccessibilityBusAddressAsync()
		{
			using var session = new DBusConnection(DBusAddress.Session ?? throw new E2EException("Nema D-Bus sesije (DBUS_SESSION_BUS_ADDRESS)."));
			await session.ConnectAsync().ConfigureAwait(false);
			return await CallAsync(session, "org.a11y.Bus", "/org/a11y/bus", "org.a11y.Bus", "GetAddress", null, null,
									(m, _) => m.GetBodyReader().ReadString()).ConfigureAwait(false);
		}

		/// <summary>Jedinstveno ime kioska na AT-SPI magistrali, ili null dok se ne registruje.</summary>
		private static async Task<string?> FindApplicationAsync(DBusConnection bus, int pid)
		{
			var applications = await CallAsync(bus, "org.a11y.atspi.Registry", RootPath, AccessibleInterface, "GetChildren", null, null, ReadNodes).ConfigureAwait(false);
			foreach (var application in applications) {
				try {
					uint owner = await CallAsync(bus, "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "GetConnectionUnixProcessID", "s",
												(ref MessageWriter w) => w.WriteString(application.Bus), (m, _) => m.GetBodyReader().ReadUInt32()).ConfigureAwait(false);
					if (owner == pid) {
						return application.Bus;
					}
				}
				catch (DBusErrorReplyException) {
					// aplikacija je u međuvremenu izašla
				}
			}
			return null;
		}

		// ─── Elementi ───

		/// <summary>Prvi element sa zadatim AccessibleId-jem (pretraga u dubinu od korena aplikacije), ili null.</summary>
		private async Task<Node?> FindAsync(string automationId)
		{
			var stack = new Stack<Node>();
			stack.Push(new Node(_application, RootPath));
			while (stack.Count > 0) {
				var node = stack.Pop();
				if (node.Path != RootPath && await AccessibleIdAsync(node).ConfigureAwait(false) == automationId) {
					return node;
				}
				var children = await ChildrenAsync(node).ConfigureAwait(false);
				for (int i = children.Count - 1; i >= 0; i--) {
					stack.Push(children[i]);
				}
			}
			return null;
		}

		private async Task<List<Node>> AllAsync()
		{
			var all = new List<Node>();
			var stack = new Stack<Node>();
			stack.Push(new Node(_application, RootPath));
			while (stack.Count > 0) {
				var node = stack.Pop();
				all.Add(node);
				var children = await ChildrenAsync(node).ConfigureAwait(false);
				for (int i = children.Count - 1; i >= 0; i--) {
					stack.Push(children[i]);
				}
			}
			return all;
		}

		private Task<List<Node>> ChildrenAsync(Node node)
			=> Guarded(() => CallAsync(_bus, node.Bus, node.Path, AccessibleInterface, "GetChildren", null, null, ReadNodes), $"GetChildren {node.Path}");

		private Task<string> AccessibleIdAsync(Node node)
			=> Guarded(() => GetStringPropertyAsync(node, "AccessibleId"), $"AccessibleId {node.Path}");

		private Task<string> NameAsync(Node node)
			=> Guarded(() => GetStringPropertyAsync(node, "Name"), $"Name {node.Path}");

		private Task<string> GetStringPropertyAsync(Node node, string property)
			=> CallAsync(_bus, node.Bus, node.Path, "org.freedesktop.DBus.Properties", "Get", "ss",
							(ref MessageWriter w) => { w.WriteString(AccessibleInterface); w.WriteString(property); },
							(m, _) => m.GetBodyReader().ReadVariantValue().GetString());

		private async Task<(bool Enabled, bool Shown)> StateAsync(Node node)
		{
			var words = await Guarded(() => CallAsync(_bus, node.Bus, node.Path, AccessibleInterface, "GetState", null, null,
														(m, _) => m.GetBodyReader().ReadArrayOfUInt32()), $"GetState {node.Path}").ConfigureAwait(false);
			bool Has(int state) => state / 32 < words.Length && (words[state / 32] & (1u << (state % 32))) != 0;
			return (Has(StateSensitive), Has(StateVisible) || Has(StateShowing));
		}

		// ─── KioskUi ───

		public override async Task WaitForAsync(string automationId, TimeSpan? timeout = null)
			=> await WaitForNodeAsync(automationId, timeout).ConfigureAwait(false);

		private Task<Node> WaitForNodeAsync(string automationId, TimeSpan? timeout = null)
			=> Poll.UntilAsync(async () => await FindAsync(automationId).ConfigureAwait(false) is { } node
												&& await StateAsync(node).ConfigureAwait(false) is { Enabled: true, Shown: true }
											? node : null,
								timeout ?? DefaultTimeout, $"element \"{automationId}\" (vidljiv i omogućen)", _guard);

		public override Task WaitForHiddenAsync(string automationId, TimeSpan? timeout = null)
			=> Poll.UntilAsync(async () => await FindAsync(automationId).ConfigureAwait(false) is not { } node
												|| !(await StateAsync(node).ConfigureAwait(false)).Shown,
								timeout ?? DefaultTimeout, $"element \"{automationId}\" sakriven", _guard);

		public override async Task WaitForPageAsync(string pageName, TimeSpan? timeout = null)
		{
			await Poll.UntilAsync(() => FindAsync($"Page.{pageName}"), timeout ?? DefaultTimeout, $"stranica {pageName}", _guard).ConfigureAwait(false);
			_trace($"UI: stranica {pageName}");
		}

		public override async Task TapAsync(string automationId, TimeSpan? timeout = null)
		{
			var node = await WaitForNodeAsync(automationId, timeout).ConfigureAwait(false);
			bool done = await Guarded(() => CallAsync(_bus, node.Bus, node.Path, "org.a11y.atspi.Action", "DoAction", "i",
														(ref MessageWriter w) => w.WriteInt32(0), (m, _) => m.GetBodyReader().ReadBool()), $"DoAction {automationId}").ConfigureAwait(false);
			if (!done) {
				throw new E2EException($"AT-SPI DoAction(0) za \"{automationId}\" nije izvršen.");
			}
			_trace($"UI: tap {automationId}");
		}

		public override Task PressWithMouseAsync(string automationId, TimeSpan? hold = null, bool requireEnabled = true)
			=> throw new SkipException(MouseSkipReason);

		public override Task<Point> GetClickablePointAsync(string automationId)
			=> throw new SkipException(MouseSkipReason);

		public override Task ClickAtAsync(Point point, string what, TimeSpan? hold = null)
			=> throw new SkipException(MouseSkipReason);

		public override async Task<string> WaitForTextAsync(string text, TimeSpan? timeout = null)
		{
			var found = await Poll.UntilAsync(async () => {
									foreach (var node in await AllAsync().ConfigureAwait(false)) {
										var name = await NameAsync(node).ConfigureAwait(false);
										if (name.Contains(text, StringComparison.Ordinal)) {
											return name;
										}
									}
									return null;
								},
								timeout ?? DefaultTimeout, $"tekst \"{text}\"", _guard).ConfigureAwait(false);
			_trace($"UI: tekst \"{found.ReplaceLineEndings(" ")}\"");
			return found;
		}

		public override async Task<(Rectangle Element, Rectangle Window)> GetBoundsAsync(string automationId)
		{
			var node = await Poll.UntilAsync(() => FindAsync(automationId), DefaultTimeout, $"element \"{automationId}\"", _guard).ConfigureAwait(false);
			var window = (await ChildrenAsync(new Node(_application, RootPath)).ConfigureAwait(false)).First();
			return (await ExtentsAsync(node).ConfigureAwait(false), await ExtentsAsync(window).ConfigureAwait(false));
		}

		/// <summary>Component.GetExtents u koordinatama prozora (1) — na Wayland-u ekranske koordinate nisu poznate.</summary>
		private Task<Rectangle> ExtentsAsync(Node node)
			=> Guarded(() => CallAsync(_bus, node.Bus, node.Path, "org.a11y.atspi.Component", "GetExtents", "u", (ref MessageWriter w) => w.WriteUInt32(1),
										(m, _) => {
											var reader = m.GetBodyReader();
											reader.AlignStruct();
											return new Rectangle(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
										}), $"GetExtents {node.Path}");

		public override async Task<string?> GetTextAsync(string automationId)
			=> await FindAsync(automationId).ConfigureAwait(false) is { } node ? await NameAsync(node).ConfigureAwait(false) : null;

		public override async Task<bool> IsEnabledAsync(string automationId)
			=> await FindAsync(automationId).ConfigureAwait(false) is { } node && (await StateAsync(node).ConfigureAwait(false)).Enabled;

		/// <summary>
		/// Linux: umesto snimka ekrana (Wayland ga ne daje bez portala) upisuje AT-SPI stablo — AccessibleId, ime i stanje
		/// svakog elementa — u fajl pored <paramref name="path"/> (.txt).
		/// </summary>
		public override string? TryCaptureScreenshot(string path)
		{
			try {
				var target = Path.ChangeExtension(path, ".atspi.txt");
				var dump = Task.Run(async () => {
								var text = new StringBuilder();
								foreach (var node in await AllAsync().ConfigureAwait(false)) {
									try {
										var id = node.Path == RootPath ? "(aplikacija)" : await AccessibleIdAsync(node).ConfigureAwait(false);
										var (enabled, shown) = await StateAsync(node).ConfigureAwait(false);
										text.AppendLine($"{node.Path}  id=\"{id}\"  name=\"{(await NameAsync(node).ConfigureAwait(false)).ReplaceLineEndings(" ")}\"  enabled={enabled} shown={shown}");
									}
									catch (DBusErrorReplyException ex) {
										text.AppendLine($"{node.Path}  (nije dostupan: {ex.Message})");
									}
								}
								return text.ToString();
							}).WaitAsync(__callTimeout).GetAwaiter().GetResult();
				File.WriteAllText(target, dump);
				return target;
			}
			catch (Exception) {
				return null;
			}
		}

		public override void Dispose() => _bus.Dispose();

		// ─── D-Bus ───

		/// <summary>AT-SPI poziv sa rokom: poziv koji visi ne sme da zaustavi test (Poll ga tretira kao neuspelu probu).</summary>
		private async Task<T> Guarded<T>(Func<Task<T>> call, string what)
		{
			try {
				return await call().WaitAsync(__callTimeout).ConfigureAwait(false);
			}
			catch (TimeoutException) {
				_trace($"UI: {what} nije završen za {__callTimeout.TotalSeconds:0} s");
				throw new TimeoutException($"AT-SPI poziv \"{what}\" nije završen za {__callTimeout.TotalSeconds:0} s");
			}
		}

		/// <summary>
		/// Argumenti poziva. <see cref="MessageWriter"/> je ref struct: kroz <c>Action&lt;MessageWriter&gt;</c> lambda bi pisala u kopiju,
		/// pa bi poruka otišla sa potpisom a bez tela — dbus-daemon tada zatvara vezu (Debian VM, 10.11.2026).
		/// </summary>
		private delegate void ArgumentWriter(ref MessageWriter writer);

		private static Task<T> CallAsync<T>(DBusConnection connection, string destination, string path, string @interface, string member,
											string? signature, ArgumentWriter? arguments, MessageValueReader<T> reader)
		{
			var writer = connection.GetMessageWriter();	// ne `using var`: takva promenljiva ne sme kao ref argument
			try {
				writer.WriteMethodCallHeader(destination, path, @interface, member, signature, MessageFlags.None);
				arguments?.Invoke(ref writer);
				return connection.CallMethodAsync(writer.CreateMessage(), reader);
			}
			finally {
				writer.Dispose();
			}
		}

		/// <summary>a(so): reference na elemente (ime na magistrali, putanja objekta).</summary>
		private static List<Node> ReadNodes(Message message, object? state)
		{
			var reader = message.GetBodyReader();
			var nodes = new List<Node>();
			var end = reader.ReadArrayStart(DBusType.Struct);
			while (reader.HasNext(end)) {
				reader.AlignStruct();
				nodes.Add(new Node(reader.ReadString(), reader.ReadObjectPathAsString()));
			}
			return nodes;
		}
	}
}
