using EIMS.Authority.Recovery;
using EIMS.Persistence.OracleAdapter;
using EIMS.Persistence.Recovery;

// S2a conformance suite: the SAME scenarios run against the in-memory store (reference) and Oracle.
// Oracle runs only when EIMS_ORACLE_CONNECTION is set (e.g. User Id=EIMS_APP;Password=...;Data Source=localhost:1521/FREEPDB1).
// Test rows stay in Oracle (EIMS_APP cannot delete audit rows); each run uses unique IDs. Rebuild with dev_rebuild_all.sql to clean.

var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (!ok) failures++;
}

var backends = new List<(string Name, Func<AggregateSnapshot, Task<Backend>> Create)>
{
    ("InMemory", seed => Task.FromResult(Backend.InMemory(seed)))
};
var connection = Environment.GetEnvironmentVariable("EIMS_ORACLE_CONNECTION");
if (string.IsNullOrWhiteSpace(connection))
    Console.WriteLine("SKIP Oracle backend (EIMS_ORACLE_CONNECTION is not set)");
else
{
    var schema = Environment.GetEnvironmentVariable("EIMS_ORACLE_SCHEMA") ?? "EIMS_OWNER";
    backends.Add(("Oracle", seed => Backend.OracleAsync(connection!, schema, seed)));
}

foreach (var (name, create) in backends)
{
    Console.WriteLine($"=== {name} ===");

    // 1) commit + read-back + evidence counts
    var id1 = NewId();
    var b1 = await create(Seed(id1));
    var (req1, com1) = Build(id1, 1, "k1", "FP1", "C1", "A1-" + id1, "O1-" + id1, "D1-" + id1);
    var r1 = await b1.Store.CommitAsync(req1, com1);
    Check($"{name}: atomic commit succeeds", r1.Allowed && r1.Code == "P2_ATOMIC_COMMIT" && r1.NewVersion == 2 && r1.StateMutated);
    var agg1 = await b1.Store.GetAggregateAsync(id1);
    Check($"{name}: aggregate read back at version 2 / SUBMITTED", agg1 is { Version: 2, State: "SUBMITTED" });
    Check($"{name}: evidence = 1 audit, 1 outbox, 1 decision, 1 idempotency", await b1.Counts(id1) == (1, 1, 1, 1));

    // 2) idempotent replay
    var replay = await b1.Store.CommitAsync(req1, com1);
    Check($"{name}: same key + fingerprint is an idempotent replay", replay.IdempotentReplay && !replay.StateMutated && replay.Allowed);
    Check($"{name}: replay writes nothing", await b1.Counts(id1) == (1, 1, 1, 1));

    // 3) same key, different fingerprint
    var (reqFp, comFp) = Build(id1, 1, "k1", "OTHER-FP", "C1", "A1x-" + id1, "O1x-" + id1, "D1x-" + id1);
    var conflict = await b1.Store.CommitAsync(reqFp, comFp);
    Check($"{name}: key reuse with different fingerprint -> 409 P2_IDEMPOTENCY_CONFLICT", conflict.HttpStatus == 409 && conflict.Code == "P2_IDEMPOTENCY_CONFLICT");

    // 4) stale version
    var (reqStale, comStale) = Build(id1, 1, "k2", "FP2", "C2", "A2-" + id1, "O2-" + id1, "D2-" + id1);
    var stale = await b1.Store.CommitAsync(reqStale, comStale);
    Check($"{name}: stale version -> 409 P2_VERSION_CONFLICT", stale.HttpStatus == 409 && stale.Code == "P2_VERSION_CONFLICT");

    // 5) duplicate audit id on a valid next version
    var (reqDup, comDup) = Build(id1, 2, "k3", "FP3", "C3", "A1-" + id1, "O3-" + id1, "D3-" + id1);
    var dup = await b1.Store.CommitAsync(reqDup, comDup);
    Check($"{name}: duplicate audit id -> 409 P2_DUPLICATE_AUDIT_ID", dup.HttpStatus == 409 && dup.Code == "P2_DUPLICATE_AUDIT_ID");
    var aggAfterDup = await b1.Store.GetAggregateAsync(id1);
    Check($"{name}: rejected duplicate left state and evidence untouched", aggAfterDup?.Version == 2 && await b1.Counts(id1) == (1, 1, 1, 1));

    // 6) aggregate not found
    var missing = NewId();
    var (reqMissing, comMissing) = Build(missing, 1, "k9", "FP9", "C9", "A9-" + missing, "O9-" + missing, "D9-" + missing);
    var nf = await b1.Store.CommitAsync(reqMissing, comMissing);
    Check($"{name}: unknown aggregate -> 404 P2_AGGREGATE_NOT_FOUND", nf.HttpStatus == 404 && nf.Code == "P2_AGGREGATE_NOT_FOUND");

    // 7) atomic rollback at every fault point
    var points = new[]
    {
        PersistenceFaultPoint.AfterStateStaged, PersistenceFaultPoint.AfterDecisionStaged,
        PersistenceFaultPoint.AfterAuditStaged, PersistenceFaultPoint.AfterOutboxStaged,
        PersistenceFaultPoint.AfterIdempotencyStaged, PersistenceFaultPoint.BeforeCommitPublish
    };
    foreach (var point in points)
    {
        var fid = NewId();
        var bf = await create(Seed(fid));
        var (reqF, comF) = Build(fid, 1, "kf", "FPF", "CF", "AF-" + fid, "OF-" + fid, "DF-" + fid);
        bf.Fault.FaultPoint = point;
        var threw = false;
        try { await bf.Store.CommitAsync(reqF, comF); }
        catch (PersistenceAtomicityException) { threw = true; }
        bf.Fault.FaultPoint = PersistenceFaultPoint.None;
        var aggF = await bf.Store.GetAggregateAsync(fid);
        Check($"{name}: fault at {point} -> exception + full rollback",
            threw && aggF?.Version == 1 && aggF.State == "DRAFT" && await bf.Counts(fid) == (0, 0, 0, 0));
    }

    // 8) evaluation plan + assignments are persisted in the same transaction and read back field-for-field
    var id8 = NewId();
    var b8 = await create(Seed(id8));
    var planId8 = "EPLAN-" + id8;
    var (req8, com8) = BuildWithPlan(id8, 1, "k8", "FP8", "C8", planId8);
    var r8 = await b8.Store.CommitAsync(req8, com8);
    Check($"{name}: commit with evaluation plan + 2 assignments succeeds", r8.Allowed && r8.Code == "P2_ATOMIC_COMMIT");
    var plan8 = await b8.Store.GetEvaluationPlanAsync(planId8);
    Check($"{name}: plan reads back field-for-field", plan8 == com8.EvaluationPlan);
    var asg8 = (await b8.Store.GetEvaluationAssignmentsForPlanAsync(planId8)).OrderBy(x => x.AssignmentId, StringComparer.Ordinal).ToArray();
    var exp8 = com8.EvaluationAssignments!.OrderBy(x => x.AssignmentId, StringComparer.Ordinal).ToArray();
    Check($"{name}: 2 assignments read back field-for-field", asg8.Length == 2 && asg8.SequenceEqual(exp8));
    var one8 = await b8.Store.GetEvaluationAssignmentAsync(exp8[0].AssignmentId);
    Check($"{name}: single assignment lookup works", one8 == exp8[0]);

    // 9) duplicate plan id on the next valid version is rejected and leaves nothing behind
    var (req9, com9) = BuildWithPlan(id8, 2, "k9", "FP9", "C9", planId8);
    var r9 = await b8.Store.CommitAsync(req9, com9);
    Check($"{name}: duplicate plan id -> 409 P2_DUPLICATE_EVALUATION_PLAN", r9.HttpStatus == 409 && r9.Code == "P2_DUPLICATE_EVALUATION_PLAN");
    Check($"{name}: rejected duplicate plan left aggregate at version 2", (await b8.Store.GetAggregateAsync(id8))?.Version == 2);

    // 10) rollback at the two evaluation fault points
    foreach (var point in new[] { PersistenceFaultPoint.AfterEvaluationPlanStaged, PersistenceFaultPoint.AfterEvaluationAssignmentsStaged })
    {
        var eid = NewId();
        var be = await create(Seed(eid));
        var ePlan = "EPLAN-" + eid;
        var (reqE, comE) = BuildWithPlan(eid, 1, "ke", "FPE", "CE", ePlan);
        be.Fault.FaultPoint = point;
        var threwE = false;
        try { await be.Store.CommitAsync(reqE, comE); }
        catch (PersistenceAtomicityException) { threwE = true; }
        be.Fault.FaultPoint = PersistenceFaultPoint.None;
        Check($"{name}: fault at {point} -> rollback incl. plan and assignments",
            threwE && (await be.Store.GetAggregateAsync(eid))?.Version == 1
            && await be.Store.GetEvaluationPlanAsync(ePlan) is null
            && (await be.Store.GetEvaluationAssignmentsForPlanAsync(ePlan)).Count == 0
            && await be.Counts(eid) == (0, 0, 0, 0));
    }
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;

static string NewId() => "S2A-" + Guid.NewGuid().ToString("N");

static AggregateSnapshot Seed(string id) =>
    new(id, "IDEA", "DRAFT", 1, "P1", "IDEA_OWNER", "SCOPE-A", null, null, null);

static (MutationRequest Request, MutationCommit Commit) Build(
    string id, long before, string key, string fingerprint, string corr, string auditId, string outboxId, string decisionId)
{
    const string command = "ideas.submit-g04";
    var now = DateTimeOffset.UtcNow;
    var ts = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero); // Oracle stores microseconds
    var roles = new[] { "IDEA_OWNER" };
    var actor = new AuthorityActor("P1", "DOMAIN\\u1", "TEST", "ASG-1", roles, new[] { "SCOPE-A" });
    var policy = new CommandPolicy(command, roles, new[] { "DRAFT" }, "RS-1.0", "IdeaSubmitted");
    var beforeSnap = new AggregateSnapshot(id, "IDEA", before == 1 ? "DRAFT" : "SUBMITTED", before, "P1", "IDEA_OWNER", "SCOPE-A", null, null, null);
    var afterSnap = beforeSnap with { State = "SUBMITTED", Version = before + 1 };
    var request = new MutationRequest(new AuthorityCommand(command, id, before, key, corr, "{}"), actor, beforeSnap, policy, fingerprint);
    var audit = new AuditEnvelope(auditId, "P1", "DOMAIN\\u1", "TEST", roles, "ASG-1", id, afterSnap.Version, "RS-1.0", ts, corr, command);
    var outbox = new OutboxEnvelope(outboxId, "IdeaSubmitted", id, afterSnap.Version, corr, ts, new Dictionary<string, string> { ["k"] = "v" });
    var decision = new DomainDecisionEnvelope(decisionId, "G04", "APPROVED", id, afterSnap.Version, "P1", "ASG-1", ts, corr, "note", new Dictionary<string, string> { ["f"] = "1" });
    return (request, new MutationCommit(afterSnap, audit, outbox, new[] { decision }));
}

static (MutationRequest Request, MutationCommit Commit) BuildWithPlan(
    string id, long before, string key, string fingerprint, string corr, string planId)
{
    var tag = key + "-" + id;
    var (request, commit) = Build(id, before, key, fingerprint, corr, "A-" + tag, "O-" + tag, "D-" + tag);
    var ts = commit.Audit.Timestamp;
    var version = commit.After.Version;
    var plan = new EvaluationPlanEnvelope(planId, id, version, 1, "ACTIVE", ts, corr);
    var assignments = new[]
    {
        new EvaluationAssignmentEnvelope("EASG-1-" + tag, planId, id, version, "IDEA_EVALUATOR", "SCOPE-A", true, "PENDING", ts, corr),
        new EvaluationAssignmentEnvelope("EASG-2-" + tag, planId, id, version, "UNIT_OWNER", "SCOPE-A", false, "PENDING", ts, corr)
    };
    return (request, commit with { EvaluationPlan = plan, EvaluationAssignments = assignments });
}

internal sealed class Backend
{
    public required IEvaluationWorkflowStore Store { get; init; }
    public required IFaultInjectablePersistence Fault { get; init; }
    public required Func<string, Task<(int, int, int, int)>> Counts { get; init; }

    public static Backend InMemory(AggregateSnapshot seed)
    {
        var s = new TransactionalAuthorityStore(null, seed);
        return new Backend
        {
            Store = s,
            Fault = s,
            Counts = id => Task.FromResult((
                s.AuditLog.Count(x => x.AggregateId == id),
                s.Outbox.Count(x => x.AggregateId == id),
                s.DomainDecisions.Count(x => x.AggregateId == id),
                s.IdempotencyRecords.Count(x => x.AggregateId == id)))
        };
    }

    public static async Task<Backend> OracleAsync(string connection, string schema, AggregateSnapshot seed)
    {
        var s = new OracleAuthorityStore(connection, schema);
        await s.SeedAggregateAsync(seed);
        return new Backend
        {
            Store = s,
            Fault = s,
            Counts = async id =>
            {
                var c = await s.CountEvidenceAsync(id);
                return (c.Audit, c.Outbox, c.Decisions, c.Idempotency);
            }
        };
    }
}
