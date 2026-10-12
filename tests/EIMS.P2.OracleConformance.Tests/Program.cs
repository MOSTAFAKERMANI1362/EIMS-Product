using System.Text.Json;
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

    // 11) completion of the only required assignment: assignment + snapshot + plan readiness + G04 assessment in one transaction
    var s11 = await SetupEvaluationAsync(create);
    var idea11 = (await s11.B.Store.GetAggregateAsync(s11.IdeaId))!;
    var (creq, ccom) = BuildCompletion(idea11, s11.Plan, s11.Asg[0], "kc1", "FPC1", "CC1", true);
    var cres = await s11.B.Store.CommitEvaluationCompletionAsync(creq, ccom);
    Check($"{name}: completion commit succeeds", cres.Allowed && cres.Code == "P2_EVALUATION_COMPLETION_ATOMIC_COMMIT" && cres.NewVersion == 2);
    Check($"{name}: plan reads back READY_FOR_G04_DECISION field-for-field", await s11.B.Store.GetEvaluationPlanAsync(s11.Plan.PlanId) == ccom.PlanAfter);
    Check($"{name}: completed assignment reads back field-for-field", await s11.B.Store.GetEvaluationAssignmentAsync(s11.Asg[0].AssignmentId) == ccom.AssignmentAfter);
    Check($"{name}: assessment snapshot reads back field-for-field", await s11.B.SnapshotFor(s11.Asg[0].AssignmentId) == ccom.AssessmentSnapshot);
    Check($"{name}: G04 assessment reads back field-for-field", await s11.B.Store.GetG04AssessmentForPlanAsync(s11.Plan.PlanId) == ccom.G04Assessment);
    var creplay = await s11.B.Store.CommitEvaluationCompletionAsync(creq, ccom);
    Check($"{name}: completion replay is idempotent", creplay.IdempotentReplay && !creplay.StateMutated);
    var (creq2, ccom2) = BuildCompletion(idea11, s11.Plan, s11.Asg[0], "kc2", "FPC2", "CC2", true);
    var cstale = await s11.B.Store.CommitEvaluationCompletionAsync(creq2, ccom2);
    Check($"{name}: stale plan version -> 409 P2_EVALUATION_PLAN_VERSION_CONFLICT", cstale.HttpStatus == 409 && cstale.Code == "P2_EVALUATION_PLAN_VERSION_CONFLICT");
    Check($"{name}: completion evidence = 2 audit, 3 outbox, 1 decision, 2 idempotency", await s11.B.Counts(s11.IdeaId) == (2, 3, 1, 2));

    // 12) completing a non-required assignment leaves the plan ACTIVE and creates no G04 assessment
    var s12 = await SetupEvaluationAsync(create);
    var idea12 = (await s12.B.Store.GetAggregateAsync(s12.IdeaId))!;
    var (nreq, ncom) = BuildCompletion(idea12, s12.Plan, s12.Asg[1], "kn1", "FPN1", "CN1", false);
    var nres = await s12.B.Store.CommitEvaluationCompletionAsync(nreq, ncom);
    var plan12 = await s12.B.Store.GetEvaluationPlanAsync(s12.Plan.PlanId);
    Check($"{name}: non-required completion keeps plan ACTIVE and creates no G04 assessment",
        nres.Allowed && plan12 is { State: "ACTIVE", PlanVersion: 2 } && await s12.B.Store.GetG04AssessmentForPlanAsync(s12.Plan.PlanId) is null);

    // 13) rollback at every completion fault point
    var completionPoints = new[]
    {
        PersistenceFaultPoint.AfterEvaluationAssignmentCompletionStaged, PersistenceFaultPoint.AfterAssessmentSnapshotStaged,
        PersistenceFaultPoint.AfterEvaluationPlanReadinessStaged, PersistenceFaultPoint.AfterG04AssessmentStaged,
        PersistenceFaultPoint.AfterAuditStaged, PersistenceFaultPoint.AfterCompletionOutboxStaged,
        PersistenceFaultPoint.AfterIdempotencyStaged, PersistenceFaultPoint.BeforeCommitPublish
    };
    foreach (var cp in completionPoints)
    {
        var sx = await SetupEvaluationAsync(create);
        var ideaX = (await sx.B.Store.GetAggregateAsync(sx.IdeaId))!;
        var (xreq, xcom) = BuildCompletion(ideaX, sx.Plan, sx.Asg[0], "kx", "FPX", "CX", true);
        sx.B.Fault.FaultPoint = cp;
        var threwX = false;
        try { await sx.B.Store.CommitEvaluationCompletionAsync(xreq, xcom); }
        catch (PersistenceAtomicityException) { threwX = true; }
        sx.B.Fault.FaultPoint = PersistenceFaultPoint.None;
        var planX = await sx.B.Store.GetEvaluationPlanAsync(sx.Plan.PlanId);
        var asgX = await sx.B.Store.GetEvaluationAssignmentAsync(sx.Asg[0].AssignmentId);
        Check($"{name}: completion fault at {cp} -> full rollback",
            threwX && planX == sx.Plan && asgX == sx.Asg[0]
            && await sx.B.SnapshotFor(sx.Asg[0].AssignmentId) is null
            && await sx.B.Store.GetG04AssessmentForPlanAsync(sx.Plan.PlanId) is null
            && await sx.B.Counts(sx.IdeaId) == (1, 1, 1, 1));
    }

    // 14) committee voting: first vote creates the frozen snapshot + state + effective vote in one transaction
    var g = await SetupG04Async(create);
    var gPlan = g.Assessment.PlanId;
    var aid = g.Assessment.AssessmentId;
    var none = Array.Empty<G04EffectiveVoteEnvelope>();
    var (vreq1, vcom1) = BuildVote(g.Idea, g.Assessment, gPlan, "kv1", "FPV1", "CV1", "P1", "APPROVE", null, null, none);
    var vres1 = await g.B.Voting.CommitG04VoteAsync(vreq1, vcom1);
    Check($"{name}: first committee vote succeeds", vres1.Allowed && vres1.Code == "P2_G04_VOTE_ATOMIC_COMMIT" && vres1.NewVersion == 1);
    var snap1 = await g.B.Voting.GetCommitteeSnapshotAsync(aid);
    Check($"{name}: committee snapshot reads back field-for-field", snap1 is not null && JsonSerializer.Serialize(snap1) == JsonSerializer.Serialize(vcom1.SnapshotAfter));
    Check($"{name}: committee state reads back field-for-field", await g.B.Voting.GetCommitteeStateAsync(aid) == vcom1.StateAfter);
    var eff1 = await g.B.Voting.GetEffectiveVotesAsync(aid);
    Check($"{name}: effective vote reads back field-for-field", eff1.Count == 1 && eff1.Single() == vcom1.EffectiveVoteAfter);
    Check($"{name}: voting evidence = 1 audit, 1 outbox, 0 decision, 1 idempotency", await g.B.VotingCounts(aid) == (1, 1, 0, 1));
    var vreplay = await g.B.Voting.CommitG04VoteAsync(vreq1, vcom1);
    Check($"{name}: vote replay is idempotent", vreplay.IdempotentReplay && !vreplay.StateMutated);

    // 15) a member revises own vote (supersedes revision 1); a second member then completes the vote
    var ctx1 = await ReadVotingContextAsync(g.B.Voting, aid);
    var (vreq2, vcom2) = BuildVote(g.Idea, g.Assessment, gPlan, "kv2", "FPV2", "CV2", "P1", "REJECT", ctx1.Snapshot, ctx1.State, ctx1.Effective);
    var vres2 = await g.B.Voting.CommitG04VoteAsync(vreq2, vcom2);
    Check($"{name}: member can revise own vote (revision 2 supersedes revision 1)", vres2.Allowed && vres2.NewVersion == 2 && vcom2.VoteRevision.RevisionNumber == 2);
    var ctx2 = await ReadVotingContextAsync(g.B.Voting, aid);
    var (vreq3, vcom3) = BuildVote(g.Idea, g.Assessment, gPlan, "kv3", "FPV3", "CV3", "P2", "APPROVE", ctx2.Snapshot, ctx2.State, ctx2.Effective);
    var vres3 = await g.B.Voting.CommitG04VoteAsync(vreq3, vcom3);
    Check($"{name}: second member vote reaches quorum and completes voting",
        vres3.Allowed && await g.B.Voting.GetCommitteeStateAsync(aid) is { VotingStageState: "COMPLETED", CommitteeVersion: 3, EffectiveVoteCount: 2 });
    var effFinal = (await g.B.Voting.GetEffectiveVotesAsync(aid)).OrderBy(x => x.PersonId, StringComparer.Ordinal).ToArray();
    Check($"{name}: effective votes = P1 REJECT (rev 2) and P2 APPROVE (rev 1)",
        effFinal.Length == 2 && effFinal[0] == vcom2.EffectiveVoteAfter && effFinal[1] == vcom3.EffectiveVoteAfter);

    // 16) a completed voting stage denies further votes
    var ctx3 = await ReadVotingContextAsync(g.B.Voting, aid);
    var (vreq4, vcom4) = BuildVote(g.Idea, g.Assessment, gPlan, "kv4", "FPV4", "CV4", "P1", "APPROVE", ctx3.Snapshot, ctx3.State, ctx3.Effective);
    var vres4 = await g.B.Voting.CommitG04VoteAsync(vreq4, vcom4);
    Check($"{name}: vote after completion -> 409 P2_G04_VOTING_STAGE_COMPLETED", vres4.HttpStatus == 409 && vres4.Code == "P2_G04_VOTING_STAGE_COMPLETED");

    // 17) a request built on a stale committee context is rejected
    var (vreq5, vcom5) = BuildVote(g.Idea, g.Assessment, gPlan, "kv5", "FPV5", "CV5", "P2", "REJECT", null, null, none);
    var vres5 = await g.B.Voting.CommitG04VoteAsync(vreq5, vcom5);
    Check($"{name}: stale committee context -> 409 P2_G04_COMMITTEE_CONTEXT_CHANGED", vres5.HttpStatus == 409 && vres5.Code == "P2_G04_COMMITTEE_CONTEXT_CHANGED");

    // 18) rollback at every voting fault point
    var votingPoints = new[]
    {
        G04VotingPersistenceFaultPoint.AfterSnapshotStaged, G04VotingPersistenceFaultPoint.AfterVoteRevisionStaged,
        G04VotingPersistenceFaultPoint.AfterEffectiveProjectionStaged, G04VotingPersistenceFaultPoint.AfterCommitteeStateStaged,
        G04VotingPersistenceFaultPoint.AfterAuditStaged, G04VotingPersistenceFaultPoint.AfterOutboxStaged,
        G04VotingPersistenceFaultPoint.AfterIdempotencyStaged, G04VotingPersistenceFaultPoint.BeforeCommitPublish
    };
    foreach (var vp in votingPoints)
    {
        var gx = await SetupG04Async(create);
        var aidX = gx.Assessment.AssessmentId;
        var (xvreq, xvcom) = BuildVote(gx.Idea, gx.Assessment, gx.Assessment.PlanId, "kvx", "FPVX", "CVX", "P1", "APPROVE", null, null, none);
        gx.B.SetVotingFault(vp);
        var threwV = false;
        try { await gx.B.Voting.CommitG04VoteAsync(xvreq, xvcom); }
        catch (PersistenceAtomicityException) { threwV = true; }
        gx.B.SetVotingFault(G04VotingPersistenceFaultPoint.None);
        Check($"{name}: voting fault at {vp} -> full rollback",
            threwV && await gx.B.Voting.GetCommitteeSnapshotAsync(aidX) is null
            && await gx.B.Voting.GetCommitteeStateAsync(aidX) is null
            && (await gx.B.Voting.GetEffectiveVotesAsync(aidX)).Count == 0
            && await gx.B.VotingCounts(aidX) == (0, 0, 0, 0));
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

async Task<(Backend B, string IdeaId, EvaluationPlanEnvelope Plan, EvaluationAssignmentEnvelope[] Asg)> SetupEvaluationAsync(
    Func<AggregateSnapshot, Task<Backend>> factory)
{
    var id = NewId();
    var b = await factory(Seed(id));
    var (req, com) = BuildWithPlan(id, 1, "ks", "FPS", "CS", "EPLAN-" + id);
    var res = await b.Store.CommitAsync(req, com);
    if (!res.Allowed) throw new InvalidOperationException("setup commit failed: " + res.Code);
    return (b, id, com.EvaluationPlan!, com.EvaluationAssignments!.OrderBy(x => x.AssignmentId, StringComparer.Ordinal).ToArray());
}

static (EvaluationCompletionRequest Request, EvaluationCompletionCommit Commit) BuildCompletion(
    AggregateSnapshot idea, EvaluationPlanEnvelope plan, EvaluationAssignmentEnvelope assignment,
    string key, string fingerprint, string corr, bool ready)
{
    const string command = "evaluation-assignments.complete";
    const string json = "{\"score\":80}";
    var now = DateTimeOffset.UtcNow;
    var ts = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    var roles = new[] { "IDEA_OWNER" };
    var actor = new AuthorityActor("P1", "DOMAIN\\u1", "TEST", "ASG-1", roles, new[] { "SCOPE-A" });
    var tag = key + "-" + assignment.AssignmentId;
    var after = assignment with
    {
        State = "COMPLETED", AssignmentVersion = assignment.AssignmentVersion + 1, CompletedAt = ts,
        CompletedByPersonId = "P1", AuthorityAssignmentId = "ASG-1",
        AssessmentSchemaId = "SCHEMA-1", AssessmentSchemaVersion = "1.0", AssessmentOutcome = "PASS"
    };
    var planAfter = plan with
    {
        PlanVersion = plan.PlanVersion + 1,
        State = ready ? "READY_FOR_G04_DECISION" : "ACTIVE",
        ReadyAt = ready ? ts : null
    };
    var snapshot = new AssessmentSnapshotEnvelope("SNAP-" + tag, assignment.AssignmentId, plan.PlanId, idea.AggregateId, idea.Version,
        assignment.Role, assignment.Scope, "SCHEMA-1", "1.0", "PASS", json, new string('a', 64), "P1", "ASG-1", ts, corr);
    var g04 = ready
        ? new G04AssessmentEnvelope("G04A-" + plan.PlanId, plan.PlanId, idea.AggregateId, idea.Version, planAfter.PlanVersion,
            "PENDING", new string('b', 64), ts, corr)
        : null;
    var audit = new AuditEnvelope("ACMP-" + tag, "P1", "DOMAIN\\u1", "TEST", roles, "ASG-1", idea.AggregateId, idea.Version,
        "RS-1.0", ts, corr, command);
    var events = new List<OutboxEnvelope>
    {
        new("OBC1-" + tag, "EvaluationAssignmentCompleted.v1", idea.AggregateId, idea.Version, corr, ts, new Dictionary<string, string> { ["k"] = "v" })
    };
    if (ready)
        events.Add(new OutboxEnvelope("OBC2-" + tag, "G04DecisionAssessmentCreated.v1", idea.AggregateId, idea.Version, corr, ts,
            new Dictionary<string, string> { ["k"] = "v" }));
    var cmd = new EvaluationCompletionCommand(idea.AggregateId, idea.Version, plan.PlanId, plan.PlanVersion,
        assignment.AssignmentId, assignment.AssignmentVersion, key, corr, json);
    return (new EvaluationCompletionRequest(cmd, actor, idea, plan, assignment, fingerprint),
        new EvaluationCompletionCommit(after, planAfter, snapshot, g04, audit, events));
}

async Task<(Backend B, AggregateSnapshot Idea, G04AssessmentEnvelope Assessment)> SetupG04Async(Func<AggregateSnapshot, Task<Backend>> factory)
{
    var s = await SetupEvaluationAsync(factory);
    var idea = (await s.B.Store.GetAggregateAsync(s.IdeaId))!;
    var (creq, ccom) = BuildCompletion(idea, s.Plan, s.Asg[0], "kg", "FPG", "CG", true);
    var cres = await s.B.Store.CommitEvaluationCompletionAsync(creq, ccom);
    if (!cres.Allowed) throw new InvalidOperationException("setup completion failed: " + cres.Code);
    return (s.B, idea, ccom.G04Assessment!);
}

static async Task<(G04CommitteeSnapshotEnvelope? Snapshot, G04CommitteeStateEnvelope? State, IReadOnlyCollection<G04EffectiveVoteEnvelope> Effective)> ReadVotingContextAsync(
    IG04VotingStore voting, string assessmentId) =>
    (await voting.GetCommitteeSnapshotAsync(assessmentId), await voting.GetCommitteeStateAsync(assessmentId), await voting.GetEffectiveVotesAsync(assessmentId));

static (G04VoteRequest Request, G04VoteCommit Commit) BuildVote(
    AggregateSnapshot idea, G04AssessmentEnvelope assessment, string planId, string key, string fingerprint, string corr,
    string personId, string vote, G04CommitteeSnapshotEnvelope? snapshotBefore, G04CommitteeStateEnvelope? stateBefore,
    IReadOnlyCollection<G04EffectiveVoteEnvelope> effectiveBefore)
{
    const string command = "g04.vote";
    var now = DateTimeOffset.UtcNow;
    var ts = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    var roles = new[] { "G04_COMMITTEE_MEMBER" };
    var assignmentId = "ASG-" + personId;
    var actor = new AuthorityActor(personId, "DOMAIN\\" + personId, "TEST", assignmentId, roles, new[] { "SCOPE-A" });
    var tag = key + "-" + assessment.AssessmentId + "-" + personId;
    var snapshot = snapshotBefore ?? new G04CommitteeSnapshotEnvelope(
        "GSNAP-" + assessment.AssessmentId, assessment.AssessmentId, idea.AggregateId, idea.Version, idea.Scope ?? "SCOPE-A",
        "G04_COMMITTEE", "GP-1", "1.0", 2, "MAJORITY", "P1", "AUTH-1", "REF-1",
        new[]
        {
            new G04CommitteeMemberEnvelope("P1", "Member One", new[] { "G04_COMMITTEE_MEMBER" }, new[] { "ASG-P1" }),
            new G04CommitteeMemberEnvelope("P2", "Member Two", new[] { "G04_COMMITTEE_MEMBER" }, new[] { "ASG-P2" })
        },
        ts, corr);
    var nextVersion = (stateBefore?.CommitteeVersion ?? 0) + 1;
    var prior = effectiveBefore.SingleOrDefault(x => string.Equals(x.PersonId, personId, StringComparison.OrdinalIgnoreCase));
    var revisionNumber = (prior?.RevisionNumber ?? 0) + 1;
    var revisionId = "GREV-" + tag;
    var revision = new G04VoteRevisionEnvelope(revisionId, assessment.AssessmentId, snapshot.SnapshotId, nextVersion, personId, assignmentId,
        vote, "Reasoned committee note for the vote", revisionNumber, prior?.VoteRevisionId, ts, corr);
    var effectiveAfter = new G04EffectiveVoteEnvelope(assessment.AssessmentId, personId, revisionId, vote, revisionNumber, ts);
    var all = effectiveBefore.Where(x => !string.Equals(x.PersonId, personId, StringComparison.OrdinalIgnoreCase)).Append(effectiveAfter).ToList();
    var approve = all.Count(x => x.Vote == "APPROVE");
    var reject = all.Count(x => x.Vote == "REJECT");
    var completed = all.Count >= snapshot.QuorumRequired;
    var state = new G04CommitteeStateEnvelope(assessment.AssessmentId, snapshot.SnapshotId, nextVersion, completed ? "COMPLETED" : "OPEN",
        completed, approve > reject, all.Count, approve, reject, ts, corr, completed ? ts : null);
    var audit = new AuditEnvelope("AV-" + tag, personId, "DOMAIN\\" + personId, "TEST", roles, assignmentId, assessment.AssessmentId,
        nextVersion, "RS-1.0", ts, corr, command);
    var events = new List<OutboxEnvelope>
    {
        new("OBV1-" + tag, "G04CommitteeVoteRecorded.v1", assessment.AssessmentId, nextVersion, corr, ts, new Dictionary<string, string> { ["k"] = "v" })
    };
    if (completed)
        events.Add(new OutboxEnvelope("OBV2-" + tag, "G04CommitteeVotingCompleted.v1", assessment.AssessmentId, nextVersion, corr, ts,
            new Dictionary<string, string> { ["k"] = "v" }));
    var cmd = new G04VoteCommand(planId, assessment.AssessmentId, idea.Version, stateBefore?.CommitteeVersion ?? 0, key, corr, vote, revision.Note);
    return (new G04VoteRequest(cmd, actor, idea, assessment, snapshotBefore, stateBefore, effectiveBefore, fingerprint),
        new G04VoteCommit(snapshot, state, revision, effectiveAfter, audit, events));
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
    public required Func<string, Task<AssessmentSnapshotEnvelope?>> SnapshotFor { get; init; }
    public required IG04VotingStore Voting { get; init; }
    public required Action<G04VotingPersistenceFaultPoint> SetVotingFault { get; init; }
    public required Func<string, Task<(int, int, int, int)>> VotingCounts { get; init; }

    public static Backend InMemory(AggregateSnapshot seed)
    {
        var s = new TransactionalAuthorityStore(null, seed);
        var v = new G04VotingTransactionalStore(s);
        return new Backend
        {
            Voting = v,
            SetVotingFault = p => v.FaultPoint = p,
            VotingCounts = id => Task.FromResult((
                v.VotingAuditLog.Count(x => x.AggregateId == id),
                v.VotingOutbox.Count(x => x.AggregateId == id),
                0,
                v.VotingIdempotencyRecords.Count(x => x.AggregateId == id))),
            Store = s,
            Fault = s,
            Counts = id => Task.FromResult((
                s.AuditLog.Count(x => x.AggregateId == id),
                s.Outbox.Count(x => x.AggregateId == id),
                s.DomainDecisions.Count(x => x.AggregateId == id),
                s.IdempotencyRecords.Count(x => x.AggregateId == id))),
            SnapshotFor = a => Task.FromResult(s.AssessmentSnapshots.FirstOrDefault(x => x.EvaluationAssignmentId == a))
        };
    }

    public static async Task<Backend> OracleAsync(string connection, string schema, AggregateSnapshot seed)
    {
        var s = new OracleAuthorityStore(connection, schema);
        await s.SeedAggregateAsync(seed);
        return new Backend
        {
            Voting = s,
            SetVotingFault = p => s.VotingFaultPoint = p,
            VotingCounts = async id =>
            {
                var c = await s.CountEvidenceAsync(id);
                return (c.Audit, c.Outbox, c.Decisions, c.Idempotency);
            },
            Store = s,
            Fault = s,
            Counts = async id =>
            {
                var c = await s.CountEvidenceAsync(id);
                return (c.Audit, c.Outbox, c.Decisions, c.Idempotency);
            },
            SnapshotFor = a => s.GetAssessmentSnapshotForAssignmentAsync(a)
        };
    }
}
