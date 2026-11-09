using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TopPos.Client.WPF.Workflow;
using Xunit;
using S = TopPos.Client.WPF.Workflow.WorkflowState;

namespace TopPos.Workflow.Tests
{
	/// <summary>
	/// <see cref="StatusSemaphore"/>: evidencija urednog gašenja u status.lock (doc/notes/status-lock-uredno-gasenje-plan.md).
	/// Pri startu se prethodni fajl pročita i ishod prethodne sesije upiše u log, pa se fajl prepiše početnim statusom
	/// (CleanShutdown=false); uredno gašenje upisuje CleanShutdown=true i posle toga se fajl više ne menja.
	/// </summary>
	public class StatusSemaphoreTests : IDisposable
	{
		private const string NotConfirmed = "program je nasilno isključen";
		private const string Unreadable = "nije moguće pouzdano pročitati";
		private const string PreviousTimeText = "2026-10-19T14:35:00.123+02:00";

		private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("toppos-status-");
		private readonly ListLogger _log = new();

		private string StatusFile => Path.Combine(_dir.FullName, "status.lock");

		public void Dispose() => _dir.Delete(recursive: true);

		[Fact]
		public void Missing_file_is_logged_and_session_starts_unconfirmed()
		{
			NewSemaphore().InitializeSession();

			var entry = Assert.Single(_log.Entries);
			Assert.Equal(LogLevel.Information, entry.Level);
			Assert.Contains("status.lock ne postoji; nema podatka o prethodnom gašenju", entry.Message);
			AssertInitialSnapshot();
		}

		[Fact]
		public void Previous_clean_shutdown_is_logged_without_warning()
		{
			var previous = NewSemaphore();
			previous.InitializeSession();
			previous.SetFree(S.Start);
			previous.MarkCleanShutdown();
			var previousTime = ReadStatus().GetProperty("Time").GetDateTimeOffset();
			_log.Clear();

			NewSemaphore().InitializeSession();

			var entry = Assert.Single(_log.Entries);
			Assert.Equal(LogLevel.Information, entry.Level);
			Assert.Contains("prethodna sesija je uredno isključena", entry.Message);
			Assert.Equal(previousTime, entry.Properties["PreviousTime"]);
			AssertInitialSnapshot();
		}

		[Theory]
		[InlineData(true, "RecipientPayment")]
		[InlineData(false, "Start")]
		public void Previous_unconfirmed_session_is_logged_with_previous_time(bool busy, string state)
		{
			File.WriteAllText(StatusFile, $$"""
				{ "Busy": {{(busy ? "true" : "false")}}, "Operational": true, "Role": "", "State": "{{state}}",
				  "Time": "{{PreviousTimeText}}", "CleanShutdown": false }
				""");

			NewSemaphore().InitializeSession();

			var entry = Assert.Single(_log.Entries);
			Assert.Equal(LogLevel.Warning, entry.Level);
			Assert.Contains(NotConfirmed, entry.Message);
			// Tačno vreme iz prethodnog fajla, sa offsetom — ne vreme novog starta.
			var previousTime = Assert.IsType<DateTimeOffset>(entry.Properties["PreviousTime"]);
			Assert.Equal(DateTimeOffset.Parse(PreviousTimeText), previousTime);
			Assert.Equal(TimeSpan.FromHours(2), previousTime.Offset);
			Assert.Contains("posle 2026-10-19T14:35:00.1230000+02:00", entry.Message);
			Assert.Equal(busy, entry.Properties["PreviousBusy"]);
			Assert.Equal(state, entry.Properties["PreviousState"]);
			AssertInitialSnapshot();
		}

		/// <summary>Stariji format bez polja i eksplicitni null nemaju potvrdu urednog gašenja (ugovor iz plana).</summary>
		[Theory]
		[InlineData("")]
		[InlineData(""", "CleanShutdown": null""")]
		public void Previous_without_confirmation_field_is_logged_as_unconfirmed(string cleanShutdown)
		{
			File.WriteAllText(StatusFile, $$"""{ "Busy": false, "Operational": true, "Role": "", "State": "Start", "Time": "{{PreviousTimeText}}"{{cleanShutdown}} }""");

			NewSemaphore().InitializeSession();

			var entry = Assert.Single(_log.Entries);
			Assert.Equal(LogLevel.Warning, entry.Level);
			Assert.Contains(NotConfirmed, entry.Message);
			Assert.Equal(DateTimeOffset.Parse(PreviousTimeText), entry.Properties["PreviousTime"]);
			AssertInitialSnapshot();
		}

		[Theory]
		[InlineData("prazan", "")]
		[InlineData("skracen", """{ "Busy": false, "Sta""")]
		[InlineData("niz", "[]")]
		[InlineData("CleanShutdown kao tekst", """{ "Busy": false, "State": "Start", "Time": "2026-10-19T14:35:00+02:00", "CleanShutdown": "true" }""")]
		[InlineData("CleanShutdown kao broj", """{ "Busy": false, "State": "Start", "Time": "2026-10-19T14:35:00+02:00", "CleanShutdown": 1 }""")]
		public void Unreadable_previous_file_is_logged_without_inventing_outcome(string variant, string content)
		{
			_ = variant;
			File.WriteAllText(StatusFile, content);

			NewSemaphore().InitializeSession();

			var entry = Assert.Single(_log.Entries);
			Assert.Equal(LogLevel.Warning, entry.Level);
			Assert.Contains(Unreadable, entry.Message);
			Assert.IsAssignableFrom<JsonException>(entry.Exception);
			Assert.False(entry.Properties.ContainsKey("PreviousTime"));
			AssertInitialSnapshot();
		}

		[Theory]
		[InlineData("")]
		[InlineData(""", "Time": "juče" """)]
		[InlineData(""", "Time": 5""")]
		[InlineData(""", "Time": null""")]
		public void Unconfirmed_session_with_invalid_time_does_not_invent_time(string time)
		{
			File.WriteAllText(StatusFile, $$"""{ "Busy": true, "State": "CourierPackagePickup", "CleanShutdown": false{{time}} }""");

			NewSemaphore().InitializeSession();

			var entry = Assert.Single(_log.Entries);
			Assert.Equal(LogLevel.Warning, entry.Level);
			Assert.Contains(NotConfirmed, entry.Message);
			Assert.Contains("vreme prekida nije moguće ograničiti", entry.Message);
			Assert.False(entry.Properties.ContainsKey("PreviousTime"));
			Assert.DoesNotContain("0001", entry.Message);
			Assert.Equal(true, entry.Properties["PreviousBusy"]);
			Assert.Equal("CourierPackagePickup", entry.Properties["PreviousState"]);
			AssertInitialSnapshot();
		}

		[Fact]
		public void Clean_shutdown_with_invalid_time_keeps_clean_outcome()
		{
			File.WriteAllText(StatusFile, """{ "Busy": false, "State": "Start", "Time": "juče", "CleanShutdown": true }""");

			NewSemaphore().InitializeSession();

			var entry = Assert.Single(_log.Entries);
			Assert.Equal(LogLevel.Warning, entry.Level);
			Assert.Contains("prethodna sesija je uredno isključena, ali Time u status.lock nije ispravan", entry.Message);
			Assert.DoesNotContain(_log.Entries, e => e.Message.Contains(NotConfirmed));
			AssertInitialSnapshot();
		}

		/// <summary>Fajl zaključan bez deljenja: čitanje i početni upis ne uspevaju, oba su u logu, a start ne pada.</summary>
		[Fact]
		public void Locked_previous_file_is_logged_and_start_continues()
		{
			const string content = """{ "Busy": false, "State": "Start", "Time": "2026-10-19T14:35:00+02:00", "CleanShutdown": true }""";
			File.WriteAllText(StatusFile, content);

			using (new FileStream(StatusFile, FileMode.Open, FileAccess.Read, FileShare.None)) {
				NewSemaphore().InitializeSession();
			}

			Assert.Collection(_log.Entries,
				read => {
					Assert.Equal(LogLevel.Warning, read.Level);
					Assert.Contains(Unreadable, read.Message);
					Assert.IsAssignableFrom<IOException>(read.Exception);
				},
				write => {
					Assert.Equal(LogLevel.Warning, write.Level);
					Assert.Contains("neuspešno pisanje", write.Message);
				});
			Assert.Equal(content, File.ReadAllText(StatusFile));
		}

		[Fact]
		public void Clean_shutdown_keeps_last_status_and_refreshes_time()
		{
			var semaphore = NewSemaphore();
			semaphore.InitializeSession();
			semaphore.SetOperational(false);
			semaphore.SetBusy(S.CourierPackagePickup);
			Assert.True(semaphore.WaitForPendingWrites(TimeSpan.FromSeconds(5)));
			var running = ReadStatus();
			Assert.True(running.GetProperty("Busy").GetBoolean());
			Assert.False(running.GetProperty("CleanShutdown").GetBoolean());

			semaphore.MarkCleanShutdown();

			var final = ReadStatus();
			Assert.True(final.GetProperty("CleanShutdown").GetBoolean());
			Assert.True(final.GetProperty("Busy").GetBoolean());
			Assert.False(final.GetProperty("Operational").GetBoolean());
			Assert.Equal("Courier", final.GetProperty("Role").GetString());
			Assert.Equal("CourierPackagePickup", final.GetProperty("State").GetString());
			Assert.True(final.GetProperty("Time").GetDateTimeOffset() >= running.GetProperty("Time").GetDateTimeOffset());
			Assert.Contains(_log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("uredno gašenje zabeleženo"));
		}

		[Fact]
		public void Late_status_writes_and_repeated_shutdown_do_not_change_final_snapshot()
		{
			var semaphore = NewSemaphore();
			semaphore.InitializeSession();
			semaphore.SetFree(S.Start);
			semaphore.MarkCleanShutdown();
			var final = File.ReadAllText(StatusFile);

			semaphore.SetBusy(S.RecipientPayment);
			semaphore.SetFree(S.Idle);
			semaphore.SetOperational(false);
			semaphore.MarkCleanShutdown();

			Assert.Equal(final, File.ReadAllText(StatusFile));
			Assert.Single(_log.Entries, e => e.Message.Contains("uredno gašenje zabeleženo"));
		}

		/// <summary>
		/// Statusni upisi sa više niti i završni upis dele isti lock: nema sudara na temp fajlu (upozorenja), a na kraju je
		/// završni snapshot.
		/// </summary>
		[Fact]
		public async Task Concurrent_status_writes_and_clean_shutdown_are_serialized()
		{
			var semaphore = NewSemaphore();
			semaphore.InitializeSession();
			using var start = new Barrier(5);
			var writers = Enumerable.Range(0, 4)
									.Select(i => Task.Factory.StartNew(() => {
										start.SignalAndWait();
										for (int k = 0; k < 100; k++) {
											if (k % 2 == 0) {
												semaphore.SetBusy(S.CourierPackagePickup);
											}
											else {
												semaphore.SetOperational(i % 2 == 0);
											}
										}
									}, TaskCreationOptions.LongRunning));
			var closer = Task.Factory.StartNew(() => {
								start.SignalAndWait();
								semaphore.MarkCleanShutdown();
							}, TaskCreationOptions.LongRunning);

			await Task.WhenAll(writers.Append(closer));

			Assert.True(ReadStatus().GetProperty("CleanShutdown").GetBoolean());
			Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
		}

		/// <summary>Zamena fajla pada (čitalac bez FileShare.Delete): upozorenje, bez loga uspeha, bez izuzetka; ostaje false.</summary>
		[Fact]
		public void Failed_clean_shutdown_write_is_logged_and_leaves_unconfirmed_file()
		{
			var semaphore = NewSemaphore();
			semaphore.InitializeSession();
			_log.Clear();

			using (new FileStream(StatusFile, FileMode.Open, FileAccess.Read, FileShare.Read)) {
				semaphore.MarkCleanShutdown();
			}

			Assert.False(ReadStatus().GetProperty("CleanShutdown").GetBoolean());
			Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("neuspešno pisanje") && e.Exception != null);
			Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("uredno gašenje nije zabeleženo"));
			Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("uredno gašenje zabeleženo"));

			_log.Clear();
			NewSemaphore().InitializeSession();
			Assert.Contains(NotConfirmed, Assert.Single(_log.Entries).Message);
		}

		[Fact]
		public void Declaration_is_in_initial_regular_and_final_snapshot()
		{
			var declaration = new StatusDeclaration(4312, new DateTime(2026, 10, 23, 7, 15, 3, DateTimeKind.Utc).AddTicks(1234567), "TopPos.Kiosk.Agent.v1");
			var semaphore = new StatusSemaphore(_dir.FullName, _log, declaration);

			semaphore.InitializeSession();
			var initial = ReadStatus();
			semaphore.SetBusy(S.CourierPackagePickup);
			Assert.True(semaphore.WaitForPendingWrites(TimeSpan.FromSeconds(5)));
			var regular = ReadStatus();
			semaphore.MarkCleanShutdown();
			var final = ReadStatus();

			Assert.True(regular.GetProperty("Busy").GetBoolean());
			Assert.True(final.GetProperty("CleanShutdown").GetBoolean());
			foreach (var status in new[] { initial, regular, final }) {
				Assert.Equal(4312, status.GetProperty("ProcessId").GetInt32());
				Assert.Equal("2026-10-23T07:15:03.1234567Z", status.GetProperty("ProcessStartUtc").GetString());
				Assert.Equal("TopPos.Kiosk.Agent.v1", status.GetProperty("AgentChannel").GetProperty("PipeName").GetString());
			}
		}

		/// <summary>Kanal isključen: AgentChannel je eksplicitno null, a identitet procesa je i dalje upisan.</summary>
		[Fact]
		public void Disabled_channel_is_written_as_null_with_process_identity()
		{
			new StatusSemaphore(_dir.FullName, _log, new StatusDeclaration(77, new DateTime(2026, 10, 23, 7, 0, 0, DateTimeKind.Utc), null)).InitializeSession();

			var status = ReadStatus();
			Assert.Equal(JsonValueKind.Null, status.GetProperty("AgentChannel").ValueKind);
			Assert.Equal(77, status.GetProperty("ProcessId").GetInt32());
			Assert.Equal("2026-10-23T07:00:00.0000000Z", status.GetProperty("ProcessStartUtc").GetString());
		}

		/// <summary>
		/// Izjava je po nameri: ime se upisuje tačno kako je u konfiguraciji, i kad nije ispravno (server ga odbija, Agent
		/// vidi nepoznato stanje umesto da čita Busy iz fajla).
		/// </summary>
		[Theory]
		[InlineData("TopPos.E2E.custom")]
		[InlineData("")]
		[InlineData(@"neispravno\ime:pipe")]
		public void Declared_pipe_name_is_written_as_configured(string pipeName)
		{
			new StatusSemaphore(_dir.FullName, _log, new StatusDeclaration(1, DateTime.UnixEpoch, pipeName)).InitializeSession();

			Assert.Equal(pipeName, ReadStatus().GetProperty("AgentChannel").GetProperty("PipeName").GetString());
		}

		[Theory]
		[InlineData(1230000, "2026-10-23T07:15:03.1230000Z")]
		[InlineData(0, "2026-10-23T07:15:03.0000000Z")]
		[InlineData(1, "2026-10-23T07:15:03.0000001Z")]
		public void Process_start_is_written_with_seven_decimals(long ticks, string expected)
		{
			var start = new DateTime(2026, 10, 23, 7, 15, 3, DateTimeKind.Utc).AddTicks(ticks);
			new StatusSemaphore(_dir.FullName, _log, new StatusDeclaration(1, start, null)).InitializeSession();

			Assert.Equal(expected, ReadStatus().GetProperty("ProcessStartUtc").GetString());
		}

		/// <summary>Podrazumevani konstruktor upisuje tekući proces; vreme starta se poredi sa nezavisnim izvorom.</summary>
		[Fact]
		public void Default_declaration_is_current_process()
		{
			NewSemaphore().InitializeSession();

			var status = ReadStatus();
			Assert.Equal(Environment.ProcessId, status.GetProperty("ProcessId").GetInt32());
			var written = DateTime.Parse(status.GetProperty("ProcessStartUtc").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
			Assert.Equal(DateTimeKind.Utc, written.Kind);
			using var process = Process.GetCurrentProcess();
			Assert.True((written - process.StartTime.ToUniversalTime()).Duration() < TimeSpan.FromSeconds(1));
			Assert.Equal(JsonValueKind.Null, status.GetProperty("AgentChannel").ValueKind);
		}

		[Fact]
		public void Process_start_failure_of_primary_source_uses_fallback_with_warning()
		{
			var fallback = new DateTime(2026, 10, 23, 7, 15, 3, DateTimeKind.Utc);

			var start = StatusDeclaration.ResolveProcessStartUtc(() => throw new Win32Exception(5), () => fallback, _log);

			Assert.Equal(fallback, start);
			var entry = Assert.Single(_log.Entries);
			Assert.Equal(LogLevel.Warning, entry.Level);
			Assert.IsType<Win32Exception>(entry.Exception);
		}

		/// <summary>Kiosk ne sme da padne zbog vremena starta: oba izvora ne uspevaju → null i Error, bez izuzetka.</summary>
		[Fact]
		public void Process_start_failure_of_both_sources_gives_null_without_exception()
		{
			var start = StatusDeclaration.ResolveProcessStartUtc(() => throw new Win32Exception(5), () => throw new InvalidOperationException("nema"), _log);

			Assert.Null(start);
			Assert.Collection(_log.Entries,
				primary => Assert.Equal(LogLevel.Warning, primary.Level),
				fallback => {
					Assert.Equal(LogLevel.Error, fallback.Level);
					Assert.IsType<InvalidOperationException>(fallback.Exception);
				});
		}

		/// <summary>Nepoznato vreme starta: polje je null, a izjava kanala ostaje (Agent vidi nepoznato stanje, ne Busy iz fajla).</summary>
		[Fact]
		public void Unknown_process_start_is_written_as_null_and_channel_stays()
		{
			new StatusSemaphore(_dir.FullName, _log, new StatusDeclaration(4312, null, "TopPos.Kiosk.Agent.v1")).InitializeSession();

			var status = ReadStatus();
			Assert.Equal(JsonValueKind.Null, status.GetProperty("ProcessStartUtc").ValueKind);
			Assert.Equal(4312, status.GetProperty("ProcessId").GetInt32());
			Assert.Equal("TopPos.Kiosk.Agent.v1", status.GetProperty("AgentChannel").GetProperty("PipeName").GetString());
		}

		private StatusSemaphore NewSemaphore() => new(_dir.FullName, _log);

		private JsonElement ReadStatus()
		{
			using var stream = new FileStream(StatusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var document = JsonDocument.Parse(stream);
			return document.RootElement.Clone();
		}

		private void AssertInitialSnapshot()
		{
			var status = ReadStatus();
			Assert.False(status.GetProperty("Busy").GetBoolean());
			Assert.Equal(JsonValueKind.Null, status.GetProperty("Operational").ValueKind);
			Assert.Equal("", status.GetProperty("Role").GetString());
			Assert.Equal("Idle", status.GetProperty("State").GetString());
			Assert.False(status.GetProperty("CleanShutdown").GetBoolean());
			Assert.True(DateTimeOffset.Now - status.GetProperty("Time").GetDateTimeOffset() < TimeSpan.FromMinutes(1));
		}

		private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> Properties);

		private sealed class ListLogger : ILogger
		{
			private readonly ConcurrentQueue<LogEntry> _entries = new();

			public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

			public void Clear() => _entries.Clear();

			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
			public bool IsEnabled(LogLevel logLevel) => true;
			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
				=> _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception,
									state is IEnumerable<KeyValuePair<string, object?>> properties ? properties.ToDictionary(p => p.Key, p => p.Value) : []));
		}
	}
}
