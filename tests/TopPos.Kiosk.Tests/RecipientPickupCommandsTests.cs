using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.AgentCommunication;
using TopPos.Kiosk.Workflow;
using TopPos.LockController;
using TopPos.Service;
using Xunit;
using FakeController = TopPos.Kiosk.Tests.RemoteServiceSessionTests.FakeController;
using O = TopPos.Agent.Contracts.RecipientPickupOutcomes;
using P = TopPos.Agent.Contracts.RecipientPickupPhases;
using R = TopPos.Service.RecipientPickupLookupResult;
using S = TopPos.Kiosk.Workflow.WorkflowState;
using T = TopPos.Kiosk.Workflow.WorkflowTrigger;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Preuzimanje na zahtev Agenta (<see cref="AgentCommands.RecipientPickup"/>): mašina, koordinator, izvor statusa, dnevnik,
	/// host workflow-a i dispatcher su povezani kao u aplikaciji; provera na serveru i poruke na ekranu su lažne.
	/// </summary>
	public class RecipientPickupCommandsTests : IDisposable
	{
		// v4: šifra paketomata iz v2 TestAgentClient (klijent preko named pipe-a dolazi sa transportom u F2).
		private const string __lockerId = "112";
		private const string __phone = "0641234567";
		private const string __code = "123456";
		private const string __presence = "4729";
		private static readonly TimeSpan __wait = TimeSpan.FromSeconds(5);
		private static readonly AgentPeer __peer = new(1, "S-1-5-18", 4242);

		private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("toppos-recipient-pickup-");

		public void Dispose()
		{
			for (int attempt = 1; ; attempt++) {
				try {
					_dir.Delete(recursive: true);
					return;
				}
				catch (IOException) when (attempt < 50) {
					Thread.Sleep(50);
				}
			}
		}

		private string JournalFolder => Path.Combine(_dir.FullName, "AgentOperations");

		private sealed class Kiosk
		{
			public required WorkflowMachine M { get; init; }
			public required WorkflowContext Context { get; init; }
			public required KioskStatusSource Status { get; init; }
			public required KioskOperationCoordinator C { get; init; }
			public required AgentOperationJournal Journal { get; init; }
			public required AgentCommandDispatcher Dispatcher { get; init; }
			public TaskCompletionSource<(R, int?, decimal?, string?)> Lookup { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
			public Exception? LookupError { get; set; }
			public ConcurrentQueue<(string Phone, int Code)> Lookups { get; } = new();
			public ConcurrentQueue<RecipientPickupNotice> Notices { get; } = new();

			// Isto vreme izdavanja za sve zahteve testa: otisak ponovljenog zahteva uključuje rokove.
			private readonly DateTimeOffset _issued = DateTimeOffset.UtcNow;

			public AgentRequest Pickup(string phone = __phone, string code = __code, string? requestId = null, string presence = __presence) => new() {
				RequestId = requestId ?? Guid.NewGuid().ToString("N"),
				LockerId = __lockerId,
				ClientInstanceId = Status.ClientInstanceId,
				Command = AgentCommands.RecipientPickup,
				IssuedAtUtc = _issued,
				ExpiresAtUtc = _issued.AddSeconds(AgentProtocol.MaxRequestTtlSeconds),
				Payload = JsonSerializer.SerializeToElement(new RecipientPickupPayload { Phone = phone, Code = code, PresenceCode = presence }, AgentJson.Options),
				Reason = "test",
			};

			public async Task<AgentCommandResult> Send(AgentRequest request)
			{
				var result = Dispatcher.Dispatch(request, __peer)!;
				return result.Pending is { } pending ? await pending.WaitAsync(__wait) : result;
			}

			public async Task<AgentCommandResult> Result(string requestId)
				=> await Send(new AgentRequest {
					RequestId = Guid.NewGuid().ToString("N"),
					LockerId = __lockerId,
					Command = AgentCommands.GetCommandResult,
					Payload = JsonSerializer.SerializeToElement(new GetCommandResultPayload { RequestId = requestId }, AgentJson.Options),
				});

			public async Task<RecipientPickupResult> PickupResult(string requestId) => Read(await Result(requestId));

			/// <summary>Prihvaćen zahtev; provera na serveru čeka <see cref="Lookup"/>.</summary>
			public async Task<string> Accepted()
			{
				var request = Pickup();
				var result = await Send(request);
				Assert.True(result.ResultCode == AgentResultCodes.InProgress, $"{result.ResultCode}: {result.Message}");
				return request.RequestId;
			}

			/// <summary>Prihvaćen zahtev, server je dodelio vrata 5; ekran pita korisnika.</summary>
			public async Task<string> AwaitingAnswer()
			{
				var id = await Accepted();
				Lookup.SetResult((R.Found, 5, 0m, null));
				await WaitUntilAsync(() => M.State == S.RecipientRemoteConfirmation);
				return id;
			}
		}

		private async Task<Kiosk> StartKiosk(string lifecycle = KioskLifecycles.Ready, bool? operational = true, string? journalFolder = null)
		{
			var time = TimeProvider.System;
			var status = new KioskStatusSource(__lockerId, logger: null);
			var coordinator = new KioskOperationCoordinator(status, logger: null);
			var context = new WorkflowContext();
			var m = Mk.Machine(context);
			m.OnTransitioned((source, destination) => coordinator.OnWorkflowTransition(source, destination));
			m.OnTransitioned(destination => status.SetWorkflowState(destination));
			m.OnEntry(S.Start, () => coordinator.CompleteCleanup(() => Task.CompletedTask));
			var journal = new AgentOperationJournal(journalFolder ?? JournalFolder, logger: null);
			journal.Initialize(RecipientPickupManager.Recoverers);
			Kiosk? kiosk = null;
			var host = new WorkflowRecipientPickupHost(m, coordinator, context,
														async (phone, code) => {
															kiosk!.Lookups.Enqueue((phone, code));
															if (kiosk.LookupError is { } error) {
																throw error;
															}
															return await kiosk.Lookup.Task;
														},
														(notice, _) => {
															kiosk!.Notices.Enqueue(notice);
															return ValueTask.CompletedTask;
														}, logger: null);
			var manager = new RecipientPickupManager(journal, status, host, new RecipientPickupSettings(4, 10), logger: null);
			var remoteService = new RemoteServiceManager(journal, status, new FakeController(8),
														 new WorkflowRemoteServiceHost(m, coordinator, () => [], () => Task.CompletedTask, logger: null),
														 new RemoteServiceSettings { LockerBoxCount = 16, DeadlineCheckInterval = null }, logger: null);
			var dispatcher = new AgentCommandDispatcher([new RecipientPickupHandler(manager)], logger: null, time,
														queries: [new GetCommandResultHandler(remoteService)]);
			kiosk = new Kiosk { M = m, Context = context, Status = status, C = coordinator, Journal = journal, Dispatcher = dispatcher };
			status.SetOperational(operational);
			await m.FireAsync(T.Reset);
			status.SetLifecycle(lifecycle);
			return kiosk;
		}

		private static RecipientPickupResult Read(AgentCommandResult result)
			=> result.Result!.Value.Deserialize<RecipientPickupResult>(AgentJson.Options)!;

		private static async Task WaitUntilAsync(Func<bool> condition)
		{
			var deadline = DateTime.UtcNow + __wait;
			while (!condition()) {
				Assert.True(DateTime.UtcNow < deadline, "uslov nije ispunjen u roku");
				await Task.Delay(20);
			}
		}

		private static async Task<RecipientPickupResult> WaitForOutcome(Kiosk k, string requestId)
		{
			await WaitUntilAsync(() => k.Journal.TryGet(requestId, out var record) && record.IsFinished && k.C.Owner == null);
			var result = await k.Result(requestId);
			Assert.Equal(AgentResultCodes.Completed, result.ResultCode);
			var pickup = Read(result);
			Assert.Equal(P.Completed, pickup.Phase);
			return pickup;
		}

		// ─── Prijem ──────────────────────────────────────────────────────────────

		[Fact]
		public async Task Accepted_request_validates_on_server_with_kiosk_busy_as_Client()
		{
			var k = await StartKiosk();
			var request = k.Pickup();

			var result = await k.Send(request);

			Assert.Equal(AgentResultCodes.InProgress, result.ResultCode);
			Assert.Equal(P.Validating, Read(result).Phase);
			Assert.Null(Read(result).Outcome);
			Assert.Equal(S.RecipientRemoteValidating, k.M.State);
			await WaitUntilAsync(() => k.Lookups.Count == 1);
			Assert.Equal((__phone, 123456), k.Lookups.Single());
			Assert.Equal(new[] { RecipientPickupNotice.Checking }, k.Notices.ToArray());
			Assert.Equal((request.RequestId, __phone, __code), (k.Context.RemotePickupRequestId, k.Context.RecipientPhoneNumber, k.Context.UnlockCode));
			var status = k.Status.Current;
			Assert.True(status.Busy);
			Assert.Equal("Client", status.Role);
			Assert.Equal(request.RequestId, status.ActiveOperationId);
			Assert.Equal(RecipientPickupOperation.Activity, status.Activity);
			Assert.Equal(1, status.OperationSequence);
		}

		[Theory]
		[InlineData("06412a4567", __code)]
		[InlineData(__phone, "123")]			// kraće od minimuma (4)
		[InlineData(__phone, "12345678901")]	// duže od maksimuma (10)
		[InlineData(__phone, "9999999999")]		// ne staje u int
		[InlineData("", __code)]
		[InlineData(__phone, " 123456")]
		[InlineData(__phone, __code, "123")]	// kod prisustva kraći od 4
		[InlineData(__phone, __code, "123456")]	// kod prisustva duži od 5
		[InlineData(__phone, __code, "47a9")]
		[InlineData(__phone, __code, "")]
		public async Task Invalid_payload_is_rejected_without_record(string phone, string code, string presence = __presence)
		{
			var k = await StartKiosk();
			var request = k.Pickup(phone, code, presence: presence);

			Assert.Equal(AgentResultCodes.InvalidRequest, (await k.Send(request)).ResultCode);
			Assert.Equal(S.Start, k.M.State);
			Assert.Equal(AgentResultCodes.ResultNotFound, (await k.Result(request.RequestId)).ResultCode);
		}

		[Theory]
		[InlineData(KioskLifecycles.Initializing, true, AgentResultCodes.NotReady)]
		[InlineData(KioskLifecycles.ShuttingDown, true, AgentResultCodes.ShuttingDown)]
		[InlineData(KioskLifecycles.Ready, false, AgentResultCodes.NotOperational)]
		[InlineData(KioskLifecycles.Ready, null, AgentResultCodes.NotOperational)]
		public async Task Kiosk_that_is_not_ready_rejects_without_record(string lifecycle, bool? operational, string expected)
		{
			var k = await StartKiosk(lifecycle, operational);
			var request = k.Pickup();

			Assert.Equal(expected, (await k.Send(request)).ResultCode);
			Assert.Equal(S.Start, k.M.State);
			Assert.Null(k.C.Owner);
			Assert.Equal(AgentResultCodes.ResultNotFound, (await k.Result(request.RequestId)).ResultCode);
		}

		[Fact]
		public async Task Busy_kiosk_rejects_without_record()
		{
			var k = await StartKiosk();
			await k.M.FireAsync(T.RecipientPickupRequested);
			var request = k.Pickup();

			Assert.Equal(AgentResultCodes.Busy, (await k.Send(request)).ResultCode);
			Assert.Equal(S.RecipientCodeInput, k.M.State);
			Assert.Equal(AgentResultCodes.ResultNotFound, (await k.Result(request.RequestId)).ResultCode);
		}

		[Fact]
		public async Task Second_request_while_first_runs_is_Busy()
		{
			var k = await StartKiosk();
			await k.Accepted();

			Assert.Equal(AgentResultCodes.Busy, (await k.Send(k.Pickup())).ResultCode);
			Assert.Single(k.Lookups);
		}

		[Fact]
		public async Task Repeated_request_gets_current_state_without_new_validation()
		{
			var k = await StartKiosk();
			var request = k.Pickup();
			await k.Send(request);
			k.Lookup.SetResult((R.Found, 5, 0m, null));
			await WaitUntilAsync(() => k.M.State == S.RecipientRemoteConfirmation);

			var repeated = await k.Send(request);

			Assert.Equal(AgentResultCodes.InProgress, repeated.ResultCode);
			Assert.Equal(P.AwaitingAnswer, Read(repeated).Phase);
			Assert.Single(k.Lookups);
		}

		[Fact]
		public async Task Same_request_id_with_other_content_is_DuplicateConflict()
		{
			var k = await StartKiosk();
			var request = k.Pickup();
			await k.Send(request);

			var conflict = await k.Send(k.Pickup(code: "654321", requestId: request.RequestId));

			Assert.Equal(AgentResultCodes.DuplicateConflict, conflict.ResultCode);
			Assert.Single(k.Lookups);
		}

		[Fact]
		public async Task Journal_record_has_no_phone_code_or_presence_code()
		{
			var k = await StartKiosk();
			var id = await k.AwaitingAnswer();
			await k.Context.RemotePickup!.SubmitPresenceCodeAsync("1111");

			Assert.True(k.Journal.TryGet(id, out var record));
			Assert.Equal(64, record.Fingerprint.Length);	// SHA-256 heš otiska
			// Bez otiska: heš od 64 heksadecimalne cifre slučajno može da sadrži niz cifara.
			var json = JsonSerializer.Serialize(record with { Fingerprint = "" }, AgentJson.Options);
			Assert.DoesNotContain(__phone, json);
			Assert.DoesNotContain(__code, json);
			Assert.DoesNotContain(__presence, json);
			Assert.DoesNotContain("1111", json);
		}

		[Fact]
		public async Task Unavailable_journal_rejects_with_InternalError_and_frees_kiosk()
		{
			var k = await StartKiosk(journalFolder: "");
			var request = k.Pickup();

			Assert.Equal(AgentResultCodes.InternalError, (await k.Send(request)).ResultCode);
			Assert.Equal(S.Start, k.M.State);
			Assert.Null(k.C.Owner);
			Assert.Null(k.Status.Current.ActiveOperationId);
			Assert.Empty(k.Lookups);
		}

		// ─── Provera na serveru ──────────────────────────────────────────────────

		[Fact]
		public async Task Door_without_payment_awaits_answer_with_door_in_context()
		{
			var k = await StartKiosk();
			var id = await k.AwaitingAnswer();

			var result = await k.Result(id);
			Assert.Equal(AgentResultCodes.InProgress, result.ResultCode);
			Assert.Equal(P.AwaitingAnswer, Read(result).Phase);
			Assert.Equal(5, Read(result).DoorNumber);
			Assert.Equal(5, k.Context.RecipientPackageDoorNumber);
			Assert.Equal(new[] { RecipientPickupNotice.Checking, RecipientPickupNotice.Cleared }, k.Notices.ToArray());
			Assert.Equal(2, k.Status.Current.OperationSequence);
		}

		[Fact]
		public async Task Payment_required_ends_on_Start_without_card()
		{
			var k = await StartKiosk();
			var id = await k.Accepted();

			k.Lookup.SetResult((R.Found, 0, 350.50m, null));

			var pickup = await WaitForOutcome(k, id);
			Assert.Equal(O.PaymentRequired, pickup.Outcome);
			Assert.Equal(350.50m, pickup.Amount);
			Assert.Equal(S.Start, k.M.State);
			Assert.Contains(RecipientPickupNotice.PaymentCheck, k.Notices);
		}

		[Theory]
		[InlineData(R.Rejected, "Pogrešan kod", O.ServerRejected)]
		[InlineData(R.Rejected, null, O.ServerRejected)]
		[InlineData(R.NoResponse, null, O.ServerUnavailable)]
		[InlineData(R.NoResponse, "Nema paketa", O.ServerUnavailable)]
		public async Task Failed_validation_ends_on_Start_with_server_outcome(R lookup, string? message, string expected)
		{
			var k = await StartKiosk();
			var id = await k.Accepted();

			k.Lookup.SetResult((lookup, null, null, message));

			var pickup = await WaitForOutcome(k, id);
			Assert.Equal(expected, pickup.Outcome);
			Assert.Equal(message, pickup.ServerMessage);
			Assert.Null(pickup.DoorNumber);
			Assert.Contains(RecipientPickupNotice.Failed, k.Notices);
		}

		[Fact]
		public async Task Lookup_exception_ends_as_ServerUnavailable()
		{
			var k = await StartKiosk();
			k.LookupError = new InvalidOperationException("test");
			var id = await k.Accepted();

			Assert.Equal(O.ServerUnavailable, (await WaitForOutcome(k, id)).Outcome);
			Assert.Equal(S.Start, k.M.State);
		}

		[Fact]
		public async Task Reset_during_validation_does_not_abort_it()
		{
			var k = await StartKiosk();
			var id = await k.Accepted();

			_ = k.M.FireAsync(T.Reset);	// Home, rok ekrana — naređa se iza provere
			k.Lookup.SetResult((R.Found, 5, 0m, null));

			await WaitUntilAsync(() => k.M.State == S.RecipientRemoteConfirmation);
			Assert.Equal(P.AwaitingAnswer, (await k.PickupResult(id)).Phase);
		}

		// ─── Odgovor korisnika ───────────────────────────────────────────────────

		[Fact]
		public async Task Accepted_answer_goes_to_pickup_in_phase_Opening()
		{
			var k = await StartKiosk();
			var id = await k.AwaitingAnswer();

			await k.M.FireAsync(T.RecipientRemoteAccepted);

			Assert.Equal(S.RecipientPackagePickup, k.M.State);
			await WaitUntilAsync(() => k.Status.Current.OperationSequence == 3);
			var pickup = await k.PickupResult(id);
			Assert.Equal(P.Opening, pickup.Phase);
			Assert.Equal(5, pickup.DoorNumber);
		}

		[Theory]
		[InlineData(T.RecipientRemoteDeclined, O.Declined)]
		[InlineData(T.Reset, O.Declined)]	// Home na stranici potvrde
		[InlineData(T.RecipientRemoteNoAnswer, O.NoAnswer)]
		public async Task Answer_without_opening_ends_on_Start(T trigger, string expected)
		{
			var k = await StartKiosk();
			var id = await k.AwaitingAnswer();

			await k.M.FireAsync(trigger);

			var pickup = await WaitForOutcome(k, id);
			Assert.Equal(expected, pickup.Outcome);
			Assert.Equal(5, pickup.DoorNumber);
			await WaitUntilAsync(() => k.Status.Current.ActiveOperationId == null);	// posle upisa zapisa na disk
			Assert.Equal(S.Start, k.M.State);
		}

		[Fact]
		public async Task Pickup_that_reaches_Start_without_outcome_is_OutcomeUnknown()
		{
			var k = await StartKiosk();
			var id = await k.AwaitingAnswer();
			await k.M.FireAsync(T.RecipientRemoteAccepted);

			await k.M.FireAsync(T.RecipientPickupComplete);

			Assert.Equal(O.OutcomeUnknown, (await WaitForOutcome(k, id)).Outcome);
		}

		/// <summary>
		/// Ishod se upisuje iz prelaza u Start, posle početka završavanja sesije. Kiosk se oslobađa tek kad je upis gotov i
		/// operacija skinuta iz statusa; inače bi nova operacija zauzela status, a kraj ove se ne bi objavio.
		/// </summary>
		[Theory]
		[InlineData(T.RecipientRemoteDeclined)]
		[InlineData(T.Reset)]	// Home na stranici potvrde
		[InlineData(T.RecipientRemoteNoAnswer)]
		[InlineData(T.RecipientRemoteAccepted, T.RecipientPickupComplete)]	// Start bez ishoda: OutcomeUnknown
		public async Task Kiosk_is_freed_only_after_the_outcome_is_recorded(params T[] triggers)
		{
			var k = await StartKiosk();
			var id = await k.AwaitingAnswer();
			(bool Finished, string? ActiveOperationId)? whenFreed = null;
			k.C.OwnerChanged += () => {
				if (k.C.Owner == null) {
					whenFreed ??= (k.Journal.TryGet(id, out var record) && record.IsFinished, k.Status.Current.ActiveOperationId);
				}
			};

			foreach (var trigger in triggers) {
				await k.M.FireAsync(trigger);
			}

			await WaitUntilAsync(() => whenFreed != null);
			Assert.Equal((true, (string?)null), whenFreed);
		}

		/// <summary>
		/// Treći pogrešan kod postavlja WrongCode van reda mašine, a upis još traje (fajl zapisa je zaključan, pa se zamena
		/// ponavlja); Home u tom trenutku vraća na Start. Kiosk ostaje zauzet dok operacija ne objavi kraj.
		/// </summary>
		// v4: upis se zadržava zaključanim fajlom (FileShare.None); na Linuxu zaključavanje ne zaustavlja zamenu fajla.
		[WindowsFact("zaključan fajl zadržava upis samo na Windows-u")]
		public async Task Home_while_the_third_wrong_code_is_recorded_keeps_kiosk_busy_until_the_end_is_published()
		{
			var k = await StartKiosk();
			var id = await k.AwaitingAnswer();
			var progress = k.Context.RemotePickup!;
			Assert.Equal(PresenceCodeResult.Rejected, await progress.SubmitPresenceCodeAsync("1111"));
			Assert.Equal(PresenceCodeResult.Rejected, await progress.SubmitPresenceCodeAsync("2222"));
			(bool Finished, string? ActiveOperationId)? whenFreed = null;
			k.C.OwnerChanged += () => {
				if (k.C.Owner == null) {
					whenFreed ??= (k.Journal.TryGet(id, out var record) && record.IsFinished, k.Status.Current.ActiveOperationId);
				}
			};

			Task<PresenceCodeResult> third;
			using (new FileStream(Path.Combine(JournalFolder, AgentOperationJournal.FileNameOf(id)), FileMode.Open, FileAccess.Read, FileShare.None)) {
				third = progress.SubmitPresenceCodeAsync("3333");
				await k.M.FireAsync(T.Reset);
				Assert.Equal(S.Start, k.M.State);
				Assert.NotNull(k.C.Owner);						// upis trećeg koda traje
				Assert.Equal(id, k.Status.Current.ActiveOperationId);
			}

			Assert.Equal(PresenceCodeResult.Failed, await third.WaitAsync(__wait));
			await WaitUntilAsync(() => whenFreed != null);
			Assert.Equal((true, (string?)null), whenFreed);
			Assert.Equal(O.WrongCode, (await WaitForOutcome(k, id)).Outcome);
		}

		[Fact]
		public async Task Local_flow_after_remote_pickup_is_not_affected()
		{
			var k = await StartKiosk();
			var id = await k.AwaitingAnswer();
			await k.M.FireAsync(T.RecipientRemoteDeclined);
			await WaitForOutcome(k, id);

			await k.M.FireAsync(T.RecipientPickupRequested);
			await k.M.FireAsync(T.Reset);

			Assert.Equal(O.Declined, (await k.PickupResult(id)).Outcome);
			Assert.Null(k.Context.RemotePickupRequestId);
			Assert.Null(k.Context.RemotePickup);
		}

		// ─── Kod prisustva ───────────────────────────────────────────────────────

		[Fact]
		public async Task Correct_presence_code_is_accepted_once()
		{
			var k = await StartKiosk();
			var id = await k.AwaitingAnswer();
			var progress = k.Context.RemotePickup!;

			Assert.Equal(PresenceCodeResult.Accepted, await progress.SubmitPresenceCodeAsync(" " + __presence));
			Assert.Equal(PresenceCodeResult.NotAwaiting, await progress.SubmitPresenceCodeAsync(__presence));
			Assert.Equal(PresenceCodeResult.NotAwaiting, await progress.SubmitPresenceCodeAsync("1111"));	// ne troši pokušaj

			var pickup = await k.PickupResult(id);
			Assert.Equal(P.AwaitingAnswer, pickup.Phase);	// otvaranje počinje tek trigerom stranice
			Assert.Equal(0, pickup.FailedCodeAttempts);
			Assert.Equal(2, k.Status.Current.OperationSequence);
		}

		[Fact]
		public async Task Wrong_code_is_visible_to_Agent_while_awaiting_the_code()
		{
			var k = await StartKiosk();
			var id = await k.AwaitingAnswer();
			var progress = k.Context.RemotePickup!;

			Assert.Equal(PresenceCodeResult.Rejected, await progress.SubmitPresenceCodeAsync("1111"));

			var result = await k.Result(id);
			Assert.Equal(AgentResultCodes.InProgress, result.ResultCode);
			Assert.Equal((P.AwaitingAnswer, 1, (string?)null), (Read(result).Phase, Read(result).FailedCodeAttempts, Read(result).Outcome));
			Assert.Equal(3, k.Status.Current.OperationSequence);
			Assert.Equal(RecipientPickupLimits.MaxCodeAttempts - 1, progress.RemainingCodeAttempts);

			Assert.Equal(PresenceCodeResult.Accepted, await progress.SubmitPresenceCodeAsync(__presence));
			await k.M.FireAsync(T.RecipientRemoteAccepted);
			Assert.Equal(S.RecipientPackagePickup, k.M.State);
			Assert.Equal(1, (await k.PickupResult(id)).FailedCodeAttempts);
		}

		[Fact]
		public async Task Exhausted_attempts_end_as_WrongCode_without_opening()
		{
			var k = await StartKiosk();
			var id = await k.AwaitingAnswer();
			var progress = k.Context.RemotePickup!;

			Assert.Equal(PresenceCodeResult.Rejected, await progress.SubmitPresenceCodeAsync("1111"));
			Assert.Equal(PresenceCodeResult.Rejected, await progress.SubmitPresenceCodeAsync("2222"));
			Assert.Equal(PresenceCodeResult.Failed, await progress.SubmitPresenceCodeAsync("3333"));
			Assert.Equal(PresenceCodeResult.NotAwaiting, await progress.SubmitPresenceCodeAsync(__presence));

			Assert.Equal(O.WrongCode, (await k.PickupResult(id)).Outcome);	// upisan pre trigera stranice
			await k.M.FireAsync(T.RecipientRemoteCodeFailed);

			var pickup = await WaitForOutcome(k, id);
			Assert.Equal((O.WrongCode, 3, 5), (pickup.Outcome, pickup.FailedCodeAttempts, pickup.DoorNumber));
			Assert.Null(pickup.Confirmation);
			Assert.Equal(S.Start, k.M.State);
		}

		[Fact]
		public async Task Presence_code_is_not_accepted_before_the_door_is_assigned()
		{
			var journal = new AgentOperationJournal(JournalFolder, logger: null);
			journal.Initialize(RecipientPickupManager.Recoverers);
			IRemotePickupProgress operation = new RecipientPickupOperation("r1", "otisak", __presence, journal, status: null, logger: null);

			Assert.Equal(PresenceCodeResult.NotAwaiting, await operation.SubmitPresenceCodeAsync(__presence));
		}

		[Theory]
		[InlineData(T.RecipientRemoteDeclined, O.Declined)]
		[InlineData(T.RecipientRemoteNoAnswer, O.NoAnswer)]
		public async Task Failed_attempts_stay_in_result_when_the_user_gives_up(T trigger, string expected)
		{
			var k = await StartKiosk();
			var id = await k.AwaitingAnswer();
			await k.Context.RemotePickup!.SubmitPresenceCodeAsync("1111");

			await k.M.FireAsync(trigger);

			var pickup = await WaitForOutcome(k, id);
			Assert.Equal((expected, 1), (pickup.Outcome, pickup.FailedCodeAttempts));
		}

		// ─── Ishod sa stranice preuzimanja ───────────────────────────────────────

		public static TheoryData<DoorStatusCode?, string> DoorOutcomes() => new() {
			{ DoorStatusCode.Open, O.OpenConfirmed },
			{ DoorStatusCode.Closed, O.OpenNotConfirmed },
			{ null, O.OutcomeUnknown },	// kontroler nije odgovorio: vrata su možda otvorena
		};

		[Theory]
		[MemberData(nameof(DoorOutcomes))]
		public void Outcome_follows_last_controller_answer(DoorStatusCode? status, string expected)
			=> Assert.Equal(expected, RemotePickupReport.OutcomeOf(status));

		[Theory]
		[InlineData(true, false, PickupConfirmationStates.Confirmed)]
		[InlineData(false, true, PickupConfirmationStates.Queued)]
		[InlineData(false, false, PickupConfirmationStates.Failed)]
		public void Confirmation_state_follows_server_answer(bool confirmed, bool queued, string expected)
			=> Assert.Equal(expected, RemotePickupReport.ConfirmationOf(confirmed, queued));

		[Fact]
		public async Task Pickup_page_gets_progress_only_when_Agent_started_the_flow()
		{
			var k = await StartKiosk();
			await k.M.FireAsync(T.RecipientPickupRequested);
			Assert.Null(k.Context.RemotePickup);	// lokalni tok
			await k.M.FireAsync(T.Reset);

			await k.AwaitingAnswer();

			Assert.NotNull(k.Context.RemotePickup);
		}

		[Fact]
		public async Task Opening_is_recorded_before_the_door_opens()
		{
			var k = await StartKiosk();
			await k.AwaitingAnswer();
			var progress = k.Context.RemotePickup!;
			Assert.False(await progress.OpeningRecordedAsync());	// faza još nije dostignuta

			await k.M.FireAsync(T.RecipientRemoteAccepted);

			Assert.True(await progress.OpeningRecordedAsync().WaitAsync(__wait));
		}

		[Fact]
		public async Task Opening_outcome_is_final_before_the_door_closes()
		{
			var k = await StartKiosk();
			var id = await k.AwaitingAnswer();
			var progress = k.Context.RemotePickup!;
			await k.M.FireAsync(T.RecipientRemoteAccepted);

			await progress.CompleteOpeningAsync(O.OpenConfirmed, PickupConfirmationStates.Queued);

			Assert.Equal(S.RecipientPackagePickup, k.M.State);	// vrata još nisu zatvorena
			var result = await k.Result(id);
			Assert.Equal(AgentResultCodes.Completed, result.ResultCode);
			Assert.Equal((P.Completed, O.OpenConfirmed, 5, PickupConfirmationStates.Queued),
						 (Read(result).Phase, Read(result).Outcome, Read(result).DoorNumber, Read(result).Confirmation));

			await k.M.FireAsync(T.RecipientPickupComplete);	// zatvaranje vrata ne menja ishod

			Assert.Equal(O.OpenConfirmed, (await WaitForOutcome(k, id)).Outcome);
		}

		[Fact]
		public async Task Failed_opening_outcome_survives_the_door_failure_exit()
		{
			var k = await StartKiosk();
			var id = await k.AwaitingAnswer();
			var progress = k.Context.RemotePickup!;
			await k.M.FireAsync(T.RecipientRemoteAccepted);

			await progress.CompleteOpeningAsync(O.OpenNotConfirmed, confirmation: null);
			await k.M.FireAsync(T.RecipientPickupDoorActionFailed);

			var pickup = await WaitForOutcome(k, id);
			Assert.Equal(O.OpenNotConfirmed, pickup.Outcome);
			Assert.Null(pickup.Confirmation);
		}

		// ─── Restart ─────────────────────────────────────────────────────────────

		[Theory]
		[InlineData(false, O.Interrupted)]
		[InlineData(true, O.OutcomeUnknown)]
		public async Task Restart_completes_unfinished_record(bool opening, string expected)
		{
			var journal = new AgentOperationJournal(JournalFolder, logger: null);
			journal.Initialize(RecipientPickupManager.Recoverers);
			var operation = new RecipientPickupOperation("r1", "otisak", __presence, journal, status: null, logger: null);
			Assert.True(await operation.StartAsync());
			Assert.True(await operation.AwaitAnswerAsync(5));
			if (opening) {
				Assert.True(await operation.BeginOpeningAsync());
			}

			var restarted = new AgentOperationJournal(JournalFolder, logger: null);
			restarted.Initialize(RecipientPickupManager.Recoverers);

			Assert.True(restarted.TryGet("r1", out var record));
			Assert.Equal(AgentResultCodes.Completed, record.ResultCode);
			var result = record.Result!.Value.Deserialize<RecipientPickupResult>(AgentJson.Options)!;
			Assert.Equal(P.Completed, result.Phase);
			Assert.Equal(expected, result.Outcome);
			Assert.Equal(5, result.DoorNumber);
		}

		[Fact]
		public async Task First_outcome_wins()
		{
			var journal = new AgentOperationJournal(JournalFolder, logger: null);
			journal.Initialize(RecipientPickupManager.Recoverers);
			var operation = new RecipientPickupOperation("r1", "otisak", __presence, journal, status: null, logger: null);
			await operation.StartAsync();

			Assert.True(await operation.CompleteAsync(O.ServerUnavailable));
			Assert.False(await operation.CompleteAsync(O.Declined));
			Assert.False(await operation.AwaitAnswerAsync(5));

			Assert.Equal(O.ServerUnavailable, operation.GetResult().Outcome);
			Assert.True(journal.TryGet("r1", out var record));
			Assert.Equal(O.ServerUnavailable, record.Result!.Value.Deserialize<RecipientPickupResult>(AgentJson.Options)!.Outcome);
		}
	}
}