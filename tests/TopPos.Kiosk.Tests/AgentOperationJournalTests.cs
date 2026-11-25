using System.IO;
using System.Text.Json;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.AgentCommunication;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// <see cref="AgentOperationJournal"/>: fajl na disku je jedini dokaz posle restarta, pa se proverava preko nove instance
	/// dnevnika nad istim folderom (kao start programa posle pada).
	/// </summary>
	public class AgentOperationJournalTests : IDisposable
	{
		private sealed class ManualTime(DateTimeOffset now) : TimeProvider
		{
			public DateTimeOffset Now { get; set; } = now;

			public override DateTimeOffset GetUtcNow() => Now;
		}

		private static readonly IReadOnlyDictionary<string, Func<AgentOperationRecord, AgentOperationRecord>> __noRecovery
			= new Dictionary<string, Func<AgentOperationRecord, AgentOperationRecord>>();

		private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("toppos-journal-");
		private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));

		private string Folder => Path.Combine(_dir.FullName, "AgentOperations");

		public void Dispose() => _dir.Delete(recursive: true);

		private AgentOperationJournal Open(IReadOnlyDictionary<string, Func<AgentOperationRecord, AgentOperationRecord>>? recoverers = null)
		{
			var journal = new AgentOperationJournal(Folder, logger: null, _time);
			journal.Initialize(recoverers ?? __noRecovery);
			return journal;
		}

		private static AgentOperationRecord Record(string requestId, string resultCode = AgentResultCodes.InProgress, string command = AgentCommands.OpenDoors)
			=> new() {
				RequestId = requestId,
				Command = command,
				Fingerprint = "f",
				ResultCode = resultCode,
				Result = AgentJson.ToResult(new OpenDoorsResult { SessionId = "s1", Openings = [new DoorOpening { DoorNumber = 7, Outcome = DoorOpenOutcomes.OutcomeUnknown }] }),
			};

		[Fact]
		public async Task Saved_record_survives_restart_with_result()
		{
			var journal = Open();
			Assert.True(await journal.SaveAsync(Record("r1", AgentResultCodes.Completed)));

			var reopened = Open();
			Assert.True(reopened.TryGet("r1", out var record));
			Assert.Equal(AgentResultCodes.Completed, record.ResultCode);
			Assert.Equal(_time.Now, record.CreatedUtc);
			var result = record.Result!.Value.Deserialize<OpenDoorsResult>(AgentJson.Options)!;
			Assert.Equal(7, Assert.Single(result.Openings).DoorNumber);
		}

		[Fact]
		public async Task Update_keeps_creation_time_and_latest_version_wins()
		{
			var journal = Open();
			Assert.True(await journal.SaveAsync(Record("r1")));
			var created = _time.Now;
			_time.Now += TimeSpan.FromMinutes(1);
			Assert.True(journal.TryGet("r1", out var first));
			Assert.True(await journal.SaveAsync(first with { ResultCode = AgentResultCodes.Completed }));

			Assert.True(Open().TryGet("r1", out var record));
			Assert.Equal(AgentResultCodes.Completed, record.ResultCode);
			Assert.Equal(created, record.CreatedUtc);
			Assert.Equal(_time.Now, record.UpdatedUtc);
		}

		[Fact]
		public async Task Unfinished_record_goes_through_command_recovery_and_is_written()
		{
			Assert.True(await Open().SaveAsync(Record("r1")));
			_time.Now += TimeSpan.FromMinutes(5);

			var recovery = new Dictionary<string, Func<AgentOperationRecord, AgentOperationRecord>> {
				[AgentCommands.OpenDoors] = r => r with { ResultCode = AgentResultCodes.Completed, Message = "oporavljeno" },
			};
			Assert.True(Open(recovery).TryGet("r1", out var recovered));
			Assert.Equal("oporavljeno", recovered.Message);

			// oporavak je upisan: sledeći start ga ne ponavlja
			Assert.True(Open().TryGet("r1", out var again));
			Assert.Equal(AgentResultCodes.Completed, again.ResultCode);
			Assert.Equal("oporavljeno", again.Message);
		}

		[Fact]
		public async Task Unfinished_record_without_recovery_becomes_outcome_unknown()
		{
			Assert.True(await Open().SaveAsync(Record("r1", command: "Nepoznata")));

			Assert.True(Open().TryGet("r1", out var record));
			Assert.Equal(AgentResultCodes.OutcomeUnknown, record.ResultCode);
		}

		[Fact]
		public async Task Recovery_that_throws_or_leaves_record_unfinished_gives_outcome_unknown()
		{
			Assert.True(await Open().SaveAsync(Record("r1")));
			Assert.True(await Open().SaveAsync(Record("r2", command: AgentCommands.EndRemoteService)));

			var recovery = new Dictionary<string, Func<AgentOperationRecord, AgentOperationRecord>> {
				[AgentCommands.OpenDoors] = _ => throw new InvalidOperationException("test"),
				[AgentCommands.EndRemoteService] = r => r with { Message = "i dalje u toku" },
			};
			var journal = Open(recovery);
			Assert.True(journal.TryGet("r1", out var thrown));
			Assert.Equal(AgentResultCodes.OutcomeUnknown, thrown.ResultCode);
			Assert.True(journal.TryGet("r2", out var unfinished));
			Assert.Equal(AgentResultCodes.OutcomeUnknown, unfinished.ResultCode);
		}

		[Fact]
		public async Task Records_older_than_retention_are_deleted_at_start()
		{
			var journal = Open();
			Assert.True(await journal.SaveAsync(Record("old", AgentResultCodes.Completed)));
			_time.Now += TimeSpan.FromDays(6);
			Assert.True(await journal.SaveAsync(Record("recent", AgentResultCodes.Completed)));
			_time.Now += TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1);

			var reopened = Open();
			Assert.False(reopened.TryGet("old", out _));
			Assert.True(reopened.TryGet("recent", out _));
			Assert.False(File.Exists(Path.Combine(Folder, AgentOperationJournal.FileNameOf("old"))));
		}

		[Fact]
		public async Task Corrupt_file_is_moved_aside_and_other_records_load()
		{
			Assert.True(await Open().SaveAsync(Record("good", AgentResultCodes.Completed)));
			var corrupt = Path.Combine(Folder, "bad.json");
			File.WriteAllText(corrupt, "{ nije json");

			var journal = Open();
			Assert.True(journal.TryGet("good", out _));
			Assert.False(File.Exists(corrupt));
			Assert.Single(Directory.GetFiles(Folder, "bad.json.corrupt-*"));
			Assert.Equal(1, journal.Count);
		}

		[Fact]
		public async Task Request_id_that_is_not_a_plain_file_name_is_stored_under_hash()
		{
			const string id = @"..\..\evil:id";
			var journal = Open();
			Assert.True(await journal.SaveAsync(Record(id, AgentResultCodes.Completed)));

			var name = AgentOperationJournal.FileNameOf(id);
			Assert.StartsWith("h-", name);
			Assert.True(File.Exists(Path.Combine(Folder, name)));
			Assert.True(Open().TryGet(id, out _));
		}

		[Fact]
		public async Task Request_ids_differing_only_in_case_or_looking_like_a_hash_get_separate_files()
		{
			const string upper = "Session-A", lower = "session-a";
			string hashed = AgentOperationJournal.FileNameOf(@"a\b");
			string lookalike = Path.GetFileNameWithoutExtension(hashed);   // ID koji izgleda kao ime iz heša
			var journal = Open();
			foreach (var id in new[] { upper, lower, @"a\b", lookalike }) {
				Assert.True(await journal.SaveAsync(Record(id, AgentResultCodes.Completed)));
			}

			Assert.Equal(4, Directory.GetFiles(Folder, "*.json").Length);
			var reopened = Open();
			foreach (var id in new[] { upper, lower, @"a\b", lookalike }) {
				Assert.True(reopened.TryGet(id, out var record));
				Assert.Equal(id, record.RequestId);
			}
		}

		[Fact]
		public async Task Replace_succeeds_while_another_process_reads_the_file()
		{
			var journal = Open();
			Assert.True(await journal.SaveAsync(Record("r1")));
			var path = Path.Combine(Folder, AgentOperationJournal.FileNameOf("r1"));

			// čitalac sa FileShare.Delete (antivirus, alat za prikupljanje) drži fajl kratko otvorenim
			var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			var release = Task.Delay(120).ContinueWith(_ => reader.Dispose(), TaskScheduler.Default);
			Assert.True(journal.TryGet("r1", out var record));
			Assert.True(await journal.SaveAsync(record with { ResultCode = AgentResultCodes.Completed }));
			await release;

			Assert.True(Open().TryGet("r1", out var saved));
			Assert.Equal(AgentResultCodes.Completed, saved.ResultCode);
		}

		[Fact]
		public async Task Unavailable_folder_reports_failed_write_but_keeps_record_in_memory()
		{
			// folder dnevnika je zauzet fajlom istog imena: upis ne može da uspe
			File.WriteAllText(Folder, "");
			var journal = Open();

			Assert.False(await journal.SaveAsync(Record("r1")));
			Assert.True(journal.TryGet("r1", out _));
		}
	}
}
