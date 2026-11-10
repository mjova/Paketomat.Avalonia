using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TopPos.Kiosk.Workflow;

namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>
	/// Prati Serilog fajl klijenta (Logs\log-*.txt u sandbox-u) i iz njega čita prelaze mašine stanja
	/// ("Workflow: {From} --[{Trigger}]--> {To}").
	///
	/// Prelaz se loguje iz OnTransitioned, PRE OnEntry handlera — linija prelaza zato ne znači da je klijent spreman
	/// za sledeću radnju; posle nje se čeka poseban uslov spremnosti (vidi doc/plan-e2e-ui-testing.md, sekcija 3.5).
	/// Čekanje uvek ide od kursora (<see cref="Mark"/>) uzetog PRE radnje, da ponovljeno stanje ne „prođe" na
	/// osnovu ranijeg događaja.
	/// </summary>
	public sealed partial class ClientLog(string sandboxDirectory)
	{
		// Serilog vrednosti piše pod navodnicima: Workflow: "Idle" --["Reset"]--> "Start"
		[GeneratedRegex(@"Workflow: ""?(?<from>\w+)""? --\[""?(?<trigger>\w+)""?\]--> ""?(?<to>\w+)""?")]
		private static partial Regex TransitionRegex();

		// Workflow: ignored trigger "SenderBarcodeAccepted" from state "Start" (nije dozvoljen iz tog stanja).
		[GeneratedRegex(@"Workflow: ignored trigger ""?(?<trigger>\w+)""? from state ""?(?<state>\w+)""?")]
		private static partial Regex IgnoredTriggerRegex();

		[GeneratedRegex(@"^\[(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) ")]
		private static partial Regex TimestampRegex();

		private readonly string _logDirectory = Path.Combine(sandboxDirectory, "Logs");
		private readonly List<string> _lines = [];
		private readonly Lock _lock = new();
		private string? _file;
		private long _position;

		/// <summary>Kursor: broj do sada pročitanih linija. Čekanja gledaju samo linije posle njega.</summary>
		public int Mark()
		{
			lock (_lock) {
				Refresh();
				return _lines.Count;
			}
		}

		public IReadOnlyList<Transition> TransitionsAfter(int mark)
		{
			lock (_lock) {
				Refresh();
				var transitions = new List<Transition>();
				for (int i = mark; i < _lines.Count; i++) {
					var match = TransitionRegex().Match(_lines[i]);
					if (match.Success
						&& Enum.TryParse<WorkflowState>(match.Groups["from"].Value, out var from)
						&& Enum.TryParse<WorkflowState>(match.Groups["to"].Value, out var to)) {
						transitions.Add(new Transition(i, from, match.Groups["trigger"].Value, to));
					}
				}
				return transitions;
			}
		}

		/// <summary>
		/// Trigeri koje mašina nije prihvatila u zatečenom stanju (Warning iz OnUnhandledTrigger). Posle Home-a dok se čeka
		/// server ovo je dokaz da je zakasneli ishod odbačen; harness ih ne proverava automatski — test zadaje tačan spisak.
		/// </summary>
		public IReadOnlyList<IgnoredTrigger> IgnoredTriggersAfter(int mark)
		{
			lock (_lock) {
				Refresh();
				var ignored = new List<IgnoredTrigger>();
				for (int i = mark; i < _lines.Count; i++) {
					var match = IgnoredTriggerRegex().Match(_lines[i]);
					if (match.Success && Enum.TryParse<WorkflowState>(match.Groups["state"].Value, out var state)) {
						ignored.Add(new IgnoredTrigger(i, match.Groups["trigger"].Value, state));
					}
				}
				return ignored;
			}
		}

		/// <summary>Čeka prvi prelaz u <paramref name="to"/> posle kursora <paramref name="after"/>.</summary>
		public async Task<Transition> WaitForTransitionAsync(WorkflowState to, int after, TimeSpan timeout, Action? guard = null)
		{
			try {
				return await Poll.UntilAsync(() => TransitionsAfter(after).FirstOrDefault(t => t.To == to),
												timeout, $"prelaz u {to} (posle linije {after})", guard).ConfigureAwait(false);
			}
			catch (E2ETimeoutException ex) {
				throw new E2ETimeoutException($"{ex.Message}; prelazi posle kursora: {DescribeLastTransitions(after)}");
			}
		}

		/// <summary>
		/// Čeka da u logu <paramref name="quiet"/> nema nove linije koja sadrži neki od <paramref name="markers"/>
		/// (npr. posle starta klijenta, dok ne prestanu Reset-ovi od promene statusa paketomata).
		/// </summary>
		public async Task WaitForQuietAsync(TimeSpan quiet, TimeSpan timeout, Action? guard, params string[] markers)
		{
			int Count() {
				lock (_lock) {
					Refresh();
					return _lines.Count(l => markers.Any(m => l.Contains(m, StringComparison.Ordinal)));
				}
			}
			var lastCount = Count();
			var lastChange = DateTime.UtcNow;
			await Poll.UntilAsync(() => {
								var count = Count();
								if (count != lastCount) {
									lastCount = count;
									lastChange = DateTime.UtcNow;
								}
								return DateTime.UtcNow - lastChange >= quiet;
							},
							timeout, $"{quiet.TotalSeconds:0.#} s bez novih linija ({string.Join(", ", markers)})", guard).ConfigureAwait(false);
		}

		/// <summary>Indeks prve linije posle kursora koja sadrži <paramref name="text"/>, ili null.</summary>
		public int? FirstLineAfter(int mark, string text)
		{
			lock (_lock) {
				Refresh();
				for (int i = mark; i < _lines.Count; i++) {
					if (_lines[i].Contains(text, StringComparison.Ordinal)) {
						return i;
					}
				}
				return null;
			}
		}

		/// <summary>Vreme upisa linije (Serilog prefiks "[yyyy-MM-dd HH:mm:ss.fff LVL]") — vreme klijenta, ne trenutak čitanja.</summary>
		public DateTime TimeOf(int line)
		{
			string text;
			lock (_lock) {
				Refresh();
				text = _lines[line];
			}
			var match = TimestampRegex().Match(text);
			return match.Success
				? DateTime.ParseExact(match.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
				: throw new E2EException($"Linija {line} nema vreme: {text}");
		}

		/// <summary>Linije posle kursora koje sadrže <paramref name="text"/>.</summary>
		public IReadOnlyList<string> LinesAfter(int mark, string text)
		{
			lock (_lock) {
				Refresh();
				return _lines.Skip(mark).Where(l => l.Contains(text, StringComparison.Ordinal)).ToList();
			}
		}

		public string Tail(int count = 40)
		{
			lock (_lock) {
				Refresh();
				return string.Join(Environment.NewLine, _lines.Skip(Math.Max(0, _lines.Count - count)));
			}
		}

		private string DescribeLastTransitions(int after)
		{
			var transitions = TransitionsAfter(after);
			return transitions.Count == 0 ? "nema" : string.Join(", ", transitions.TakeLast(5));
		}

		private void Refresh()
		{
			_file ??= FindLogFile();
			if (_file == null) {
				return;
			}
			ReadNewLines();
			// Serilog (rollingInterval Day) posle ponoći nastavlja u novom fajlu, sa sledećim datumom u imenu — test koji
			// prelazi ponoć inače ne bi video ništa posle nje. Nov fajl nastaje tek kad je stari zatvoren, pa se stari pre
			// prelaska dočita do kraja.
			while (NextLogFile() is { } next) {
				ReadNewLines();
				_file = next;
				_position = 0;
				ReadNewLines();
			}
		}

		private void ReadNewLines()
		{
			// Čitaju se sirovi bajtovi, a kursor se pomera samo do poslednjeg završenog reda ('\n'). Klijent piše
			// istovremeno: ono što stigne posle čitanja (ili nedovršen red / UTF-8 znak) ostaje za sledeći put, umesto
			// da se kursor postavi na dužinu fajla i preskoči nepročitano.
			using var stream = new FileStream(_file!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			stream.Position = _position;
			var buffer = new MemoryStream();
			stream.CopyTo(buffer);
			var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
			var lastNewLine = bytes.LastIndexOf((byte)'\n');
			if (lastNewLine < 0) {
				return;
			}
			var complete = bytes[..(lastNewLine + 1)];
			_position += complete.Length;
			if (complete.StartsWith(Encoding.UTF8.Preamble)) {
				complete = complete[Encoding.UTF8.Preamble.Length..];
			}
			var lines = Encoding.UTF8.GetString(complete).Split('\n');
			_lines.AddRange(lines[..^1].Select(l => l.TrimEnd('\r')));	// poslednji element je prazan (posle '\n')
		}

		private string? FindLogFile()
			=> Directory.Exists(_logDirectory)
				? Directory.EnumerateFiles(_logDirectory, "log-*.txt").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
				: null;

		/// <summary>
		/// Fajl posle trenutnog po imenu (datum yyyyMMdd u imenu; i Serilog-ov nastavak „_001" ide posle osnovnog imena).
		/// </summary>
		private string? NextLogFile()
			=> Directory.EnumerateFiles(_logDirectory, "log-*.txt")
						.Where(f => string.CompareOrdinal(Path.GetFileName(f), Path.GetFileName(_file)) > 0)
						.OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
						.FirstOrDefault();
	}

	public sealed record Transition(int Line, WorkflowState From, string Trigger, WorkflowState To)
	{
		public override string ToString() => $"{From} --[{Trigger}]--> {To}";
	}

	public sealed record IgnoredTrigger(int Line, string Trigger, WorkflowState State)
	{
		public override string ToString() => $"{Trigger} iz {State}";
	}
}
