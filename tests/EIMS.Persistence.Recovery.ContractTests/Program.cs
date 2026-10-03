using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("P2-CT-01 logical contract ready while physical Oracle remains blocked", LogicalReadyPhysicalBlocked),
    ("P2-CT-02 complete non-secret binding evidence enables physical readiness", PhysicalBindingReady),
    ("P2-CT-03 atomic commit writes state audit outbox idempotency", AtomicCommit),
    ("P2-CT-04 exact idempotent replay does not mutate", IdempotentReplay),
    ("P2-CT-05 same idempotency key with different fingerprint conflicts", IdempotencyConflict),
    ("P2-CT-06 optimistic concurrency allows only one stale-version writer", VersionConflict),
    ("P2-CT-07 request expectedVersion must match read snapshot", RequestVersionMismatch),
    ("P2-CT-08 invalid next version is rejected before mutation", InvalidNextVersion),
    ("P2-CT-09 audit entity/version mismatch is rejected", AuditMismatch),
    ("P2-CT-10 outbox entity/version mismatch is rejected", OutboxMismatch),
    ("P2-CT-11 rollback after state write fault", () => AtomicRollback(PersistenceFaultPoint.AfterStateStaged)),
    ("P2-CT-12 rollback after audit write fault", () => AtomicRollback(PersistenceFaultPoint.AfterAuditStaged)),
    ("P2-CT-13 rollback after outbox write fault", () => AtomicRollback(PersistenceFaultPoint.AfterOutboxStaged)),
    ("P2-CT-14 rollback after idempotency write fault", () => AtomicRollback(PersistenceFaultPoint.AfterIdempotencyStaged)),
    ("P2-CT-15 rollback before commit publish fault", () => AtomicRollback(PersistenceFaultPoint.BeforeCommitPublish)),
    ("P2-CT-16 audit retains authority context", AuditContext),
    ("P2-CT-17 outbox retains aggregate version and correlation", OutboxContext),
    ("P2-CT-18 P1 authority kernel composes with P2 store", KernelComposition),
    ("P2-CT-19 concurrent different keys on same version produce one winner", ConcurrentVersionRace),
    ("P2-CT-20 concurrent exact same key becomes one commit plus replay", ConcurrentIdempotencyRace),
    ("P2-CT-21 cancellation before persistence causes no mutation", CancellationNoMutation),
    ("P2-CT-22 Oracle binding contract exposes no secret-bearing field", BindingContractHasNoSecrets),
    ("P2-CT-23 correlation mismatch is rejected before mutation", CorrelationMismatch),
    ("G01-IDEMP-RED-01 same key and fingerprint replays original committed result", G01IdempotentReplay),
    ("G01-IDEMP-RED-02 same key with different fingerprint conflicts without mutation", G01IdempotencyConflict),
    ("G01-IDEMP-RED-03 concurrent same-key decision has one logical commit", G01ConcurrentIdempotency),
    ("G01-IDEMP-RED-04 failed transaction leaves no successful decision idempotency outcome", G01FailedTransactionLeavesNoIdempotency),
    ("G01-IDEMP-RED-05 submission and decision idempotency remain independent", G01SubmissionDecisionIdempotencyIndependent),
    ("G01-RC-RED-01 APPROVE without ReasonCode is allowed", G01ReasonCodeApproveOptional),
    ("G01-RC-RED-02 RETURN without ReasonCode is rejected", G01ReasonCodeReturnRequired),
    ("G01-RC-RED-03 RETURN with ReasonCode is allowed", G01ReasonCodeReturnPresent),
    ("G01-RC-RED-04 REJECT without ReasonCode is rejected", G01ReasonCodeRejectRequired),
    ("G01-RC-RED-05 REJECT with ReasonCode is allowed", G01ReasonCodeRejectPresent),
    ("G01-SNAPSHOT-RED-01 APPROVE snapshot is complete", G01SnapshotApproveCompleteness),
    ("G01-SNAPSHOT-RED-02 RETURN snapshot captures ReasonCode", G01SnapshotReturnReasonCode),
    ("G01-SNAPSHOT-RED-03 REJECT snapshot captures ReasonCode", G01SnapshotRejectReasonCode),
    ("G01-SNAPSHOT-RED-04 snapshot remains immutable after commit", G01SnapshotImmutability),
    ("G01-SNAPSHOT-RED-05 snapshot binds evaluated Observation business version", G01SnapshotObservationVersion)
};

var passed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"RESULT {passed}/{tests.Count} PASS");
return passed == tests.Count ? 0 : 1;

static AggregateSnapshot Aggregate(long version = 1, string state = "READY") =>
    new("AGG-1", "TEST", state, version, "P-OWNER", "TEST_OWNER", "UNIT:RND");

static AuthorityActor Actor() =>
    new("P-001", "DOMAIN\\user", "WINDOWS_PRINCIPAL", "ASG-001", new[] { "TEST_ROLE" }, new[] { "UNIT:RND" });

static AuthorityCommand Command(string key = "KEY-1", long expected = 1, string body = "{}", string corr = "CORR-1") =>
    new("test.command", "AGG-1", expected, key, corr, body, "UNIT:RND");

static CommandPolicy Policy() =>
    new("test.command", new[] { "TEST_ROLE" }, new[] { "READY" }, "TEST-RULESET-1.0", "TestCommitted.v1");

static MutationRequest Request(AuthorityCommand command, AggregateSnapshot before) =>
    new(command, Actor(), before, Policy(), AuthorityKernel.Fingerprint(command));

static MutationCommit Commit(AggregateSnapshot before, AuthorityCommand command, string auditId = "AUD-1", string messageId = "MSG-1", bool includeDecision = false)
{
    var after = before with { Version = before.Version + 1 };
    var now = DateTimeOffset.UtcNow;
    var audit = new AuditEnvelope(
            auditId,
            "P-001",
            "DOMAIN\\user",
            "WINDOWS_PRINCIPAL",
            new[] { "TEST_ROLE" },
            "ASG-001",
            after.AggregateId,
            after.Version,
            "TEST-RULESET-1.0",
            now,
            command.CorrelationId,
            command.CommandName);
    var decisions = includeDecision
        ? new[]
        {
            new DomainDecisionEnvelope(
                $"DEC-{command.IdempotencyKey}",
                command.CommandName,
                command.RawBody.Contains("\"outcome\":\"RETURN\"", StringComparison.Ordinal) ? "RETURN" : command.RawBody.Contains("\"outcome\":\"REJECT\"", StringComparison.Ordinal) ? "REJECT" : "APPROVE",
                after.AggregateId,
                after.Version,
                "P-001",
                "ASG-001",
                now,
                command.CorrelationId,
                Facts: new Dictionary<string, string>
                {
                    ["RuleSetId"] = "G01-INQ",
                    ["RuleSetVersion"] = "1.0"
                })
        }
        : Array.Empty<DomainDecisionEnvelope>();

    return new MutationCommit(
        after,
        audit,
        new OutboxEnvelope(
            messageId,
            "TestCommitted.v1",
            after.AggregateId,
            after.Version,
            command.CorrelationId,
            now),
        decisions);
}

static TransactionalAuthorityStore Store() =>
    new(PersistenceContractDescriptor.RecoveryBaseline(), Aggregate());

static async Task LogicalReadyPhysicalBlocked()
{
    var contract = PersistenceContractDescriptor.RecoveryBaseline();
    True(contract.IsLogicalContractReady);
    False(contract.IsPhysicalOracleReady);
    Eq(6, contract.OracleBinding.MissingEvidence.Count);
}

static Task PhysicalBindingReady()
{
    var binding = new OracleBindingEvidence(
        "ORG-EVIDENCE-VERSION",
        "ORG-APPROVED-DOTNET-PROVIDER",
        "ORG-APPROVED-CONNECTIVITY",
        "DOMAIN-SERVICE-ACCOUNT-MODEL",
        "EIMS_SCHEMA_OWNER",
        "ENV-EVIDENCE-REF");
    var contract = PersistenceContractDescriptor.RecoveryBaseline() with { OracleBinding = binding };
    True(contract.IsLogicalContractReady);
    True(contract.IsPhysicalOracleReady);
    Eq(0, binding.MissingEvidence.Count);
    return Task.CompletedTask;
}

static async Task AtomicCommit()
{
    var store = Store();
    var before = Aggregate();
    var command = Command();
    var result = await store.CommitAsync(Request(command, before), Commit(before, command));
    Eq(200, result.HttpStatus); True(result.StateMutated); Eq(2L, result.NewVersion!.Value);
    Eq(2L, (await store.GetAggregateAsync("AGG-1"))!.Version);
    Eq(1, store.AuditLog.Count); Eq(1, store.Outbox.Count); Eq(1, store.IdempotencyRecords.Count);
}

static async Task IdempotentReplay()
{
    var store = Store();
    var before = Aggregate();
    var command = Command();
    var request = Request(command, before);
    var commit = Commit(before, command);
    var first = await store.CommitAsync(request, commit);
    var second = await store.CommitAsync(request, commit);
    True(first.StateMutated); True(second.IdempotentReplay); False(second.StateMutated);
    Eq(2L, (await store.GetAggregateAsync("AGG-1"))!.Version);
    Eq(1, store.AuditLog.Count); Eq(1, store.Outbox.Count); Eq(1, store.IdempotencyRecords.Count);
}

static async Task IdempotencyConflict()
{
    var store = Store();
    var before = Aggregate();
    var firstCommand = Command(body: "{\"value\":1}");
    await store.CommitAsync(Request(firstCommand, before), Commit(before, firstCommand));

    var secondCommand = Command(body: "{\"value\":2}");
    var result = await store.CommitAsync(Request(secondCommand, before), Commit(before, secondCommand, "AUD-2", "MSG-2"));
    Eq(409, result.HttpStatus); Eq("P2_IDEMPOTENCY_CONFLICT", result.Code);
    Eq(1, store.AuditLog.Count); Eq(1, store.Outbox.Count); Eq(1, store.IdempotencyRecords.Count);
}

static async Task VersionConflict()
{
    var store = Store();
    var before = Aggregate();
    var c1 = Command(key: "K-1", corr: "C-1");
    var c2 = Command(key: "K-2", corr: "C-2");
    var r1 = await store.CommitAsync(Request(c1, before), Commit(before, c1, "AUD-1", "MSG-1"));
    var r2 = await store.CommitAsync(Request(c2, before), Commit(before, c2, "AUD-2", "MSG-2"));
    Eq(200, r1.HttpStatus); Eq(409, r2.HttpStatus); Eq("P2_VERSION_CONFLICT", r2.Code);
    Eq(1, store.AuditLog.Count); Eq(1, store.Outbox.Count);
}

static async Task RequestVersionMismatch()
{
    var store = Store();
    var before = Aggregate();
    var command = Command(expected: 7);
    var result = await store.CommitAsync(Request(command, before), Commit(before, command));
    Eq(409, result.HttpStatus); Eq("P2_REQUEST_VERSION_MISMATCH", result.Code);
    await AssertUnchanged(store);
}

static async Task InvalidNextVersion()
{
    var store = Store();
    var before = Aggregate();
    var command = Command();
    var good = Commit(before, command);
    var bad = good with { After = good.After with { Version = 3 } };
    var result = await store.CommitAsync(Request(command, before), bad);
    Eq(500, result.HttpStatus); Eq("P2_INVALID_NEXT_VERSION", result.Code);
    await AssertUnchanged(store);
}

static async Task AuditMismatch()
{
    var store = Store();
    var before = Aggregate();
    var command = Command();
    var good = Commit(before, command);
    var bad = good with { Audit = good.Audit with { EntityVersion = 99 } };
    var result = await store.CommitAsync(Request(command, before), bad);
    Eq(500, result.HttpStatus); Eq("P2_AUDIT_ENTITY_VERSION_MISMATCH", result.Code);
    await AssertUnchanged(store);
}

static async Task OutboxMismatch()
{
    var store = Store();
    var before = Aggregate();
    var command = Command();
    var good = Commit(before, command);
    var bad = good with { Outbox = good.Outbox with { AggregateVersion = 99 } };
    var result = await store.CommitAsync(Request(command, before), bad);
    Eq(500, result.HttpStatus); Eq("P2_OUTBOX_ENTITY_VERSION_MISMATCH", result.Code);
    await AssertUnchanged(store);
}

static async Task AtomicRollback(PersistenceFaultPoint fault)
{
    var store = Store();
    store.FaultPoint = fault;
    var before = Aggregate();
    var command = Command();
    var threw = false;
    try
    {
        await store.CommitAsync(Request(command, before), Commit(before, command));
    }
    catch (PersistenceAtomicityException)
    {
        threw = true;
    }
    True(threw);
    await AssertUnchanged(store);
}

static async Task AuditContext()
{
    var store = Store();
    var before = Aggregate();
    var command = Command();
    await store.CommitAsync(Request(command, before), Commit(before, command));
    var audit = store.AuditLog.Single();
    Eq("P-001", audit.PersonId);
    Eq("DOMAIN\\user", audit.NetworkIdentity);
    Eq("WINDOWS_PRINCIPAL", audit.IdentitySource);
    Eq("ASG-001", audit.Assignment);
    Eq("TEST-RULESET-1.0", audit.RuleSet);
    Eq("CORR-1", audit.CorrelationId);
    Eq(2L, audit.EntityVersion);
    True(audit.Roles.Contains("TEST_ROLE"));
}

static async Task OutboxContext()
{
    var store = Store();
    var before = Aggregate();
    var command = Command();
    await store.CommitAsync(Request(command, before), Commit(before, command));
    var msg = store.Outbox.Single();
    Eq("AGG-1", msg.AggregateId);
    Eq(2L, msg.AggregateVersion);
    Eq("CORR-1", msg.CorrelationId);
    Eq("TestCommitted.v1", msg.EventName);
}

static async Task KernelComposition()
{
    var store = Store();
    var kernel = new AuthorityKernel(
        new SinglePolicyCatalog(Policy()),
        store,
        new PassRuleEvaluator(),
        new BaselineSodEvaluator(),
        new IncrementPlanner());
    var command = Command();
    var first = await kernel.ExecuteAsync(command, Actor());
    var replay = await kernel.ExecuteAsync(command, Actor());
    Eq(200, first.HttpStatus); True(first.StateMutated);
    True(replay.IdempotentReplay); False(replay.StateMutated);
    Eq(1, store.AuditLog.Count); Eq(1, store.Outbox.Count); Eq(1, store.IdempotencyRecords.Count);
}

static async Task ConcurrentVersionRace()
{
    var store = Store();
    var before = Aggregate();
    var c1 = Command(key: "RACE-1", corr: "RACE-C1");
    var c2 = Command(key: "RACE-2", corr: "RACE-C2");
    var t1 = Task.Run(async () => await store.CommitAsync(Request(c1, before), Commit(before, c1, "AUD-R1", "MSG-R1")));
    var t2 = Task.Run(async () => await store.CommitAsync(Request(c2, before), Commit(before, c2, "AUD-R2", "MSG-R2")));
    var results = await Task.WhenAll(t1, t2);
    Eq(1, results.Count(x => x.HttpStatus == 200));
    Eq(1, results.Count(x => x.Code == "P2_VERSION_CONFLICT"));
    Eq(1, store.AuditLog.Count); Eq(1, store.Outbox.Count); Eq(1, store.IdempotencyRecords.Count);
    Eq(2L, (await store.GetAggregateAsync("AGG-1"))!.Version);
}

static async Task ConcurrentIdempotencyRace()
{
    var store = Store();
    var before = Aggregate();
    var command = Command(key: "SAME-KEY", corr: "RACE-SAME");
    var request = Request(command, before);
    var commit = Commit(before, command, "AUD-SAME", "MSG-SAME");
    var t1 = Task.Run(async () => await store.CommitAsync(request, commit));
    var t2 = Task.Run(async () => await store.CommitAsync(request, commit));
    var results = await Task.WhenAll(t1, t2);
    Eq(1, results.Count(x => x.StateMutated));
    Eq(1, results.Count(x => x.IdempotentReplay));
    Eq(1, store.AuditLog.Count); Eq(1, store.Outbox.Count); Eq(1, store.IdempotencyRecords.Count);
}

static async Task CancellationNoMutation()
{
    var store = Store();
    var cts = new CancellationTokenSource();
    cts.Cancel();
    var before = Aggregate();
    var command = Command();
    var cancelled = false;
    try
    {
        await store.CommitAsync(Request(command, before), Commit(before, command), cts.Token);
    }
    catch (OperationCanceledException)
    {
        cancelled = true;
    }
    True(cancelled);
    await AssertUnchanged(store);
}

static Task BindingContractHasNoSecrets()
{
    var prohibited = new[] { "password", "secret", "token", "connectionstring", "credential" };
    var names = typeof(OracleBindingEvidence).GetProperties().Select(x => x.Name.ToLowerInvariant()).ToArray();
    False(names.Any(name => prohibited.Any(p => name.Contains(p, StringComparison.Ordinal))));
    False(PersistenceContractDescriptor.RecoveryBaseline().ConnectionSecretsAllowedInContract);
    return Task.CompletedTask;
}


static async Task G01IdempotentReplay()
{
    var store = Store();
    var before = Aggregate();
    var command = CommandNamed("g01.decide", key: "G01-RED-01", body: "{\"outcome\":\"APPROVE\"}");
    var request = Request(command, before);
    var commit = Commit(before, command, includeDecision: true);
    var first = await store.CommitAsync(request, commit);
    var replay = await store.CommitAsync(request, commit);

    Eq(200, first.HttpStatus);
    True(first.StateMutated);
    True(replay.IdempotentReplay);
    False(replay.StateMutated);
    Eq(first.NewVersion!.Value, replay.NewVersion!.Value);
    Eq(1, store.IdempotencyRecords.Count);
    Eq(1, store.DomainDecisions.Count);
    Eq(1, store.AuditLog.Count);
    Eq(1, store.Outbox.Count);
}

static async Task G01IdempotencyConflict()
{
    var store = Store();
    var before = Aggregate();
    var first = CommandNamed("g01.decide", key: "G01-RED-02", body: "{\"outcome\":\"APPROVE\"}");
    await store.CommitAsync(Request(first, before), Commit(before, first, includeDecision: true));

    var conflicting = CommandNamed("g01.decide", key: "G01-RED-02", body: "{\"outcome\":\"RETURN\"}");
    var result = await store.CommitAsync(
        Request(conflicting, before),
        Commit(before, conflicting, "AUD-G01-RED-02", "MSG-G01-RED-02", includeDecision: true));

    Eq(409, result.HttpStatus);
    Eq("P2_IDEMPOTENCY_CONFLICT", result.Code);
    Eq(2L, (await store.GetAggregateAsync("AGG-1"))!.Version);
    Eq(1, store.IdempotencyRecords.Count);
    Eq(1, store.DomainDecisions.Count);
    Eq(1, store.AuditLog.Count);
    Eq(1, store.Outbox.Count);
}

static async Task G01ConcurrentIdempotency()
{
    var store = Store();
    var before = Aggregate();
    var command = CommandNamed("g01.decide", key: "G01-RED-03", corr: "G01-RED-03-RACE");
    var request = Request(command, before);
    var commit = Commit(before, command, "AUD-G01-RED-03", "MSG-G01-RED-03", includeDecision: true);

    var results = await Task.WhenAll(
        Task.Run(() => store.CommitAsync(request, commit).AsTask()),
        Task.Run(() => store.CommitAsync(request, commit).AsTask()));

    Eq(1, results.Count(x => x.StateMutated));
    Eq(1, results.Count(x => x.IdempotentReplay));
    Eq(1, store.IdempotencyRecords.Count);
    Eq(1, store.DomainDecisions.Count);
    Eq(1, store.AuditLog.Count);
    Eq(1, store.Outbox.Count);
}

static async Task G01FailedTransactionLeavesNoIdempotency()
{
    var store = Store();
    store.FaultPoint = PersistenceFaultPoint.AfterIdempotencyStaged;
    var before = Aggregate();
    var command = CommandNamed("g01.decide", key: "G01-RED-04");

    var threw = false;
    try
    {
        await store.CommitAsync(Request(command, before), Commit(before, command));
    }
    catch (PersistenceAtomicityException)
    {
        threw = true;
    }

    True(threw);
    Eq(1L, (await store.GetAggregateAsync("AGG-1"))!.Version);
    Eq(0, store.IdempotencyRecords.Count);
    Eq(0, store.DomainDecisions.Count);
    Eq(0, store.AuditLog.Count);
    Eq(0, store.Outbox.Count);
}

static async Task G01SubmissionDecisionIdempotencyIndependent()
{
    var store = Store();
    var submissionBefore = Aggregate();
    var submission = CommandNamed("observation.submit", key: "SHARED-G01-RED-05", corr: "SUBMIT-G01-RED-05");
    var submissionResult = await store.CommitAsync(
        Request(submission, submissionBefore),
        Commit(submissionBefore, submission, "AUD-G01-SUBMIT-05", "MSG-G01-SUBMIT-05"));
    Eq(200, submissionResult.HttpStatus);

    var decisionBefore = (await store.GetAggregateAsync("AGG-1"))!;
    var decision = CommandNamed("g01.decide", key: "SHARED-G01-RED-05", expected: 2, corr: "DECIDE-G01-RED-05");
    var decisionResult = await store.CommitAsync(
        Request(decision, decisionBefore),
        Commit(decisionBefore, decision, "AUD-G01-DECIDE-05", "MSG-G01-DECIDE-05", includeDecision: true));

    Eq(200, decisionResult.HttpStatus);
    False(decisionResult.IdempotentReplay);
    Eq(2, store.IdempotencyRecords.Count);
    True(store.IdempotencyRecords.Any(x => x.CommandName == "observation.submit" && x.IdempotencyKey == "SHARED-G01-RED-05"));
    True(store.IdempotencyRecords.Any(x => x.CommandName == "g01.decide" && x.IdempotencyKey == "SHARED-G01-RED-05"));
}


static async Task G01ReasonCodeApproveOptional()
{
    var store = Store();
    var before = Aggregate();
    var command = CommandNamed("g01.decide", key: "G01-RC-RED-01",
        body: "{\"outcome\":\"APPROVE\",\"observationVersion\":1,\"gateOutcome\":\"G01_COMPLETE\",\"ruleResults\":{\"R02\":\"PASS\",\"R03\":\"PASS\",\"R04\":\"PASS\",\"R05\":\"PASS\"}}");
    var result = await ExecuteG01RuleValidationOnly(store, command, before);
    Eq(200, result.HttpStatus);
}

static async Task G01ReasonCodeReturnRequired()
{
    var store = Store();
    var before = Aggregate();
    var command = CommandNamed("g01.decide", key: "G01-RC-RED-02",
        body: "{\"outcome\":\"RETURN\",\"observationVersion\":1,\"gateOutcome\":\"G01_INCOMPLETE\",\"ruleResults\":{\"R02\":\"PASS\",\"R03\":\"PASS\",\"R04\":\"PASS\",\"R05\":\"WARNING\"}}");
    var result = await ExecuteG01RuleValidationOnly(store, command, before);
    Eq(422, result.HttpStatus);
}

static async Task G01ReasonCodeReturnPresent()
{
    var store = Store();
    var before = Aggregate();
    var command = CommandNamed("g01.decide", key: "G01-RC-RED-03",
        body: "{\"outcome\":\"RETURN\",\"reasonCode\":\"RC-CORRECTION\",\"observationVersion\":1,\"gateOutcome\":\"G01_INCOMPLETE\",\"ruleResults\":{\"R02\":\"PASS\",\"R03\":\"PASS\",\"R04\":\"PASS\",\"R05\":\"WARNING\"}}");
    var result = await ExecuteG01RuleValidationOnly(store, command, before);
    Eq(200, result.HttpStatus);
}

static async Task G01ReasonCodeRejectRequired()
{
    var store = Store();
    var before = Aggregate();
    var command = CommandNamed("g01.decide", key: "G01-RC-RED-04",
        body: "{\"outcome\":\"REJECT\",\"observationVersion\":1,\"gateOutcome\":\"G01_COMPLETE\",\"ruleResults\":{\"R02\":\"PASS\",\"R03\":\"PASS\",\"R04\":\"FAIL\",\"R05\":\"PASS\"}}");
    var result = await ExecuteG01RuleValidationOnly(store, command, before);
    Eq(422, result.HttpStatus);
}

static async Task G01ReasonCodeRejectPresent()
{
    var store = Store();
    var before = Aggregate();
    var command = CommandNamed("g01.decide", key: "G01-RC-RED-05",
        body: "{\"outcome\":\"REJECT\",\"reasonCode\":\"RC-NOT-WORTHY\",\"observationVersion\":1,\"gateOutcome\":\"G01_COMPLETE\",\"ruleResults\":{\"R02\":\"PASS\",\"R03\":\"PASS\",\"R04\":\"FAIL\",\"R05\":\"PASS\"}}");
    var result = await ExecuteG01RuleValidationOnly(store, command, before);
    Eq(200, result.HttpStatus);
}

static async Task<AuthorityResult> ExecuteG01RuleValidationOnly(
    TransactionalAuthorityStore store, AuthorityCommand command, AggregateSnapshot before)
{
    var kernel = new AuthorityKernel(
        new SinglePolicyCatalog(new CommandPolicy("g01.decide", new[] { "TEST_ROLE" }, new[] { "READY" }, "G01-INQ", "G01.Decided.v1")),
        store,
        new G01ReasonCodeRedRuleEvaluator(),
        new BaselineSodEvaluator(),
        new IncrementPlanner());
    return await kernel.ExecuteAsync(command, Actor());
}

static async Task G01SnapshotApproveCompleteness()
{
    var store = Store();
    var before = Aggregate();
    var command = CommandNamed(
        "g01.decide",
        key: "G01-SNAPSHOT-RED-01",
        body: "{\"outcome\":\"APPROVE\",\"observationVersion\":1,\"gateOutcome\":\"G01_COMPLETE\",\"ruleResults\":{\"R02\":\"PASS\",\"R03\":\"PASS\",\"R04\":\"PASS\",\"R05\":\"PASS\"}}");
    var result = await store.CommitAsync(Request(command, before), Commit(before, command, includeDecision: true));
    Eq(200, result.HttpStatus);

    var snapshot = RequireSingleDecisionSnapshot(store);
    Eq("G01-INQ", Read(snapshot, "RuleSetId"));
    Eq("1.0", Read(snapshot, "RuleSetVersion"));
    Eq("G01_COMPLETE", Read(snapshot, "GateOutcome"));
    Eq("1", Read(snapshot, "ObservationVersion"));
    Eq("APPROVE", Read(snapshot, "DecisionOutcome"));
    True(HasFourRuleExecutions(snapshot));
    True(HasAuthorizationEvidence(snapshot));
}

static async Task G01SnapshotReturnReasonCode()
{
    var store = Store();
    var before = Aggregate();
    var command = CommandNamed(
        "g01.decide",
        key: "G01-SNAPSHOT-RED-02",
        body: "{\"outcome\":\"RETURN\",\"reasonCode\":\"RC-CORRECTION\",\"observationVersion\":1,\"gateOutcome\":\"G01_INCOMPLETE\",\"ruleResults\":{\"R02\":\"PASS\",\"R03\":\"PASS\",\"R04\":\"PASS\",\"R05\":\"WARNING\"}}");
    await store.CommitAsync(Request(command, before), Commit(before, command, includeDecision: true));

    var snapshot = RequireSingleDecisionSnapshot(store);
    Eq("RETURN", Read(snapshot, "DecisionOutcome"));
    Eq("RC-CORRECTION", Read(snapshot, "ReasonCode"));
}

static async Task G01SnapshotRejectReasonCode()
{
    var store = Store();
    var before = Aggregate();
    var command = CommandNamed(
        "g01.decide",
        key: "G01-SNAPSHOT-RED-03",
        body: "{\"outcome\":\"REJECT\",\"reasonCode\":\"RC-NOT-WORTHY\",\"observationVersion\":1,\"gateOutcome\":\"G01_COMPLETE\",\"ruleResults\":{\"R02\":\"PASS\",\"R03\":\"PASS\",\"R04\":\"FAIL\",\"R05\":\"PASS\"}}");
    await store.CommitAsync(Request(command, before), Commit(before, command, includeDecision: true));

    var snapshot = RequireSingleDecisionSnapshot(store);
    Eq("REJECT", Read(snapshot, "DecisionOutcome"));
    Eq("RC-NOT-WORTHY", Read(snapshot, "ReasonCode"));
}

static async Task G01SnapshotImmutability()
{
    var store = Store();
    var before = Aggregate();
    var command = CommandNamed(
        "g01.decide",
        key: "G01-SNAPSHOT-RED-04",
        body: "{\"outcome\":\"RETURN\",\"reasonCode\":\"RC-IMMUTABLE\",\"observationVersion\":1,\"gateOutcome\":\"G01_INCOMPLETE\",\"ruleResults\":{\"R02\":\"PASS\",\"R03\":\"PASS\",\"R04\":\"PASS\",\"R05\":\"WARNING\"}}");
    await store.CommitAsync(Request(command, before), Commit(before, command, includeDecision: true));

    var snapshotBefore = RequireSingleDecisionSnapshot(store);
    var fingerprintBefore = Read(snapshotBefore, "Fingerprint");
    var decisionBefore = Read(snapshotBefore, "DecisionOutcome");
    var reasonBefore = Read(snapshotBefore, "ReasonCode");

    var later = (await store.GetAggregateAsync("AGG-1"))!;
    var followUp = CommandNamed("observation.followup", key: "G01-SNAPSHOT-RED-04-FOLLOWUP", expected: later.Version);
    await store.CommitAsync(Request(followUp, later), Commit(later, followUp));

    var snapshotAfter = RequireSingleDecisionSnapshot(store);
    Eq(fingerprintBefore, Read(snapshotAfter, "Fingerprint"));
    Eq(decisionBefore, Read(snapshotAfter, "DecisionOutcome"));
    Eq(reasonBefore, Read(snapshotAfter, "ReasonCode"));
}

static async Task G01SnapshotObservationVersion()
{
    var before = Aggregate(version: 7);
    var store = new TransactionalAuthorityStore(PersistenceContractDescriptor.RecoveryBaseline(), before);
    var command = CommandNamed(
        "g01.decide",
        key: "G01-SNAPSHOT-RED-05",
        expected: 7,
        body: "{\"outcome\":\"APPROVE\",\"observationVersion\":7,\"gateOutcome\":\"G01_COMPLETE\",\"ruleResults\":{\"R02\":\"PASS\",\"R03\":\"PASS\",\"R04\":\"PASS\",\"R05\":\"PASS\"}}");
    await store.CommitAsync(Request(command, before), Commit(before, command, includeDecision: true));

    var snapshot = RequireSingleDecisionSnapshot(store);
    Eq("7", Read(snapshot, "ObservationVersion"));
}

static object RequireSingleDecisionSnapshot(TransactionalAuthorityStore store)
{
    var snapshotCollection = store.GetType()
        .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
        .Select(property => property.GetValue(store))
        .Where(value => value is System.Collections.IEnumerable && value is not string)
        .SelectMany(value => ((System.Collections.IEnumerable)value!).Cast<object>())
        .Where(item => HasProperty(item, "SnapshotId")
            && HasProperty(item, "DecisionId")
            && HasProperty(item, "ObservationId")
            && HasProperty(item, "ObservationVersion")
            && HasProperty(item, "RuleSetId")
            && HasProperty(item, "RuleSetVersion")
            && HasProperty(item, "GateOutcome")
            && HasProperty(item, "RuleExecutions")
            && HasProperty(item, "AuthorizationContext")
            && HasProperty(item, "DecisionOutcome")
            && HasProperty(item, "CreatedAt")
            && HasProperty(item, "SchemaVersion")
            && HasProperty(item, "Fingerprint"))
        .ToArray();

    if (snapshotCollection.Length != 1)
        throw new InvalidOperationException($"Expected one committed Decision Snapshot evidence record, actual '{snapshotCollection.Length}'.");

    return snapshotCollection[0];
}

static bool HasProperty(object value, string name) =>
    value.GetType().GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public) is not null;

static string Read(object value, string name)
{
    var property = value.GetType().GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
    if (property is null)
        throw new InvalidOperationException($"Expected Snapshot property '{name}'.");
    return Convert.ToString(property.GetValue(value)) ?? "";
}

static bool HasFourRuleExecutions(object snapshot)
{
    var property = snapshot.GetType().GetProperty("RuleExecutions");
    if (property?.GetValue(snapshot) is not System.Collections.IEnumerable rules)
        return false;
    return rules.Cast<object>().Count() == 4;
}

static bool HasAuthorizationEvidence(object snapshot)
{
    var property = snapshot.GetType().GetProperty("AuthorizationContext");
    var value = property?.GetValue(snapshot);
    if (value is null)
        return false;

    var names = value.GetType().GetProperties()
        .Select(property => property.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    return names.Contains("Principal")
        || names.Contains("PrincipalId")
        || names.Contains("AuthenticatedPrincipal")
        || names.Contains("Assignment")
        || names.Contains("AssignmentId");
}

static AuthorityCommand CommandNamed(
    string commandName,
    string key = "KEY-1",
    long expected = 1,
    string body = "{}",
    string corr = "CORR-1") =>
    new(commandName, "AGG-1", expected, key, corr, body, "UNIT:RND");

static async Task CorrelationMismatch()
{
    var store = Store();
    var before = Aggregate();
    var command = Command(corr: "CORR-COMMAND");
    var good = Commit(before, command);
    var bad = good with { Outbox = good.Outbox with { CorrelationId = "CORR-OTHER" } };
    var result = await store.CommitAsync(Request(command, before), bad);
    Eq(500, result.HttpStatus); Eq("P2_CORRELATION_MISMATCH", result.Code);
    await AssertUnchanged(store);
}

static async Task AssertUnchanged(TransactionalAuthorityStore store)
{
    Eq(1L, (await store.GetAggregateAsync("AGG-1"))!.Version);
    Eq(0, store.AuditLog.Count);
    Eq(0, store.Outbox.Count);
    Eq(0, store.IdempotencyRecords.Count);
}

static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
static void Eq<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
}

sealed class SinglePolicyCatalog(CommandPolicy policy) : ICommandPolicyCatalog
{
    public IReadOnlyCollection<CommandPolicy> All { get; } = new[] { policy };
    public bool TryGet(string commandName, out CommandPolicy found)
    {
        if (string.Equals(commandName, policy.CommandName, StringComparison.OrdinalIgnoreCase))
        {
            found = policy;
            return true;
        }
        found = null!;
        return false;
    }
}

sealed class G01ReasonCodeRedRuleEvaluator : IRuleEvaluator
{
    public ValueTask<RuleEvaluation> EvaluateAsync(
        AuthorityCommand command,
        AuthorityActor actor,
        AggregateSnapshot aggregate,
        CommandPolicy policy,
        CancellationToken cancellationToken = default)
    {
        using var document = System.Text.Json.JsonDocument.Parse(command.RawBody);
        var root = document.RootElement;
        var outcome = root.TryGetProperty("outcome", out var outcomeValue) && outcomeValue.ValueKind == System.Text.Json.JsonValueKind.String
            ? outcomeValue.GetString()?.Trim().ToUpperInvariant()
            : null;
        var hasReasonCode = root.TryGetProperty("reasonCode", out var reasonCode)
            && reasonCode.ValueKind == System.Text.Json.JsonValueKind.String
            && !string.IsNullOrWhiteSpace(reasonCode.GetString());

        if ((outcome == "RETURN" || outcome == "REJECT") && !hasReasonCode)
            return ValueTask.FromResult(RuleEvaluation.Fail("G01_REASON_CODE_REQUIRED"));

        return ValueTask.FromResult(RuleEvaluation.Pass());
    }
}

sealed class IncrementPlanner : ICommandMutationPlanner
{
    public ValueTask<MutationPlan?> PlanAsync(
        AuthorityCommand command,
        AuthorityActor actor,
        AggregateSnapshot aggregate,
        CommandPolicy policy,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<MutationPlan?>(new MutationPlan(
            aggregate with { Version = aggregate.Version + 1 },
            policy.EventName));
}
