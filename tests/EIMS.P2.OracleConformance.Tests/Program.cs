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

    // 19) G04 final decision: idea + plan + assessment + immutable evidence + audit + outbox + idempotency in one transaction
    var fd = await SetupFinalAsync(create);
    var fAid = fd.Assessment.AssessmentId;
    var fBefore = await fd.F.Counts(fd.Idea.AggregateId, fAid);
    var (freq1, fcom1) = BuildFinal(fd.Idea, fd.Plan, fd.Assessment, "kf1", "FPF1", "CF1", false);
    var fres1 = await fd.F.Store.CommitFinalDecisionAsync(freq1, fcom1);
    Check($"{name}: final decision commit succeeds", fres1.Allowed && fres1.Code == "P1_G04_FINAL_DECISION_COMMITTED" && fres1.NewVersion == fd.Idea.Version);
    Check($"{name}: idea reads back field-for-field after the decision", await fd.F.Store.GetIdeaAsync(fd.Idea.AggregateId) == fcom1.IdeaAfter);
    Check($"{name}: plan reads back field-for-field after the decision", await fd.F.Store.GetPlanAsync(fd.Plan.PlanId) == fcom1.PlanAfter);
    Check($"{name}: assessment reads back DECIDED field-for-field", await fd.F.Store.GetAssessmentAsync(fAid) == fcom1.AssessmentAfter);
    Check($"{name}: 41-field decision evidence reads back field-for-field", await fd.F.Evidence(fAid) == fcom1.Evidence);
    Check($"{name}: decision evidence delta = 1 audit, 1 outbox, 1 idempotency, 1 evidence",
        Delta(fBefore, await fd.F.Counts(fd.Idea.AggregateId, fAid)) == (1, 1, 1, 1));
    var freplay = await fd.F.Store.CommitFinalDecisionAsync(freq1, fcom1);
    Check($"{name}: final decision replay is idempotent", freplay.IdempotentReplay && !freplay.StateMutated);
    var (freq2, fcom2) = BuildFinal(fd.Idea, fd.Plan, fd.Assessment, "kf2", "FPF2", "CF2", false);
    var fstale = await fd.F.Store.CommitFinalDecisionAsync(freq2, fcom2);
    Check($"{name}: second decision on a decided assessment -> 409 P1_G04_FINAL_CONCURRENT_CONFLICT",
        fstale.HttpStatus == 409 && fstale.Code == "P1_G04_FINAL_CONCURRENT_CONFLICT");
    var (freq3, fcom3) = BuildFinal(fd.Idea, fd.Plan, fd.Assessment, "kf1", "OTHER-FP", "CF3", false);
    var fconf = await fd.F.Store.CommitFinalDecisionAsync(freq3, fcom3);
    Check($"{name}: key reuse with different fingerprint -> 409 P1_IDEMPOTENCY_CONFLICT", fconf.HttpStatus == 409 && fconf.Code == "P1_IDEMPOTENCY_CONFLICT");

    // 20) an invalid commit (technical version not advanced by exactly 1) throws before any write
    var fi = await SetupFinalAsync(create);
    var (freqI, fcomI) = BuildFinal(fi.Idea, fi.Plan, fi.Assessment, "kfi", "FPFI", "CFI", true);
    string? invalidMessage = null;
    try { await fi.F.Store.CommitFinalDecisionAsync(freqI, fcomI); }
    catch (InvalidOperationException ex) { invalidMessage = ex.Message; }
    Check($"{name}: invalid technical version -> P1_G04_FINAL_TECHNICAL_VERSION_INVALID and nothing written",
        invalidMessage == "P1_G04_FINAL_TECHNICAL_VERSION_INVALID"
        && await fi.F.Store.GetIdeaAsync(fi.Idea.AggregateId) == fi.Idea
        && await fi.F.Store.GetAssessmentAsync(fi.Assessment.AssessmentId) == fi.Assessment);

    // 21) rollback at every final-decision fault point
    var finalPoints = new[]
    {
        G04FinalDecisionPersistenceFaultPoint.AfterIdeaStaged, G04FinalDecisionPersistenceFaultPoint.AfterPlanStaged,
        G04FinalDecisionPersistenceFaultPoint.AfterAssessmentStaged, G04FinalDecisionPersistenceFaultPoint.AfterEvidenceStaged,
        G04FinalDecisionPersistenceFaultPoint.AfterAuditStaged, G04FinalDecisionPersistenceFaultPoint.AfterOutboxStaged,
        G04FinalDecisionPersistenceFaultPoint.AfterIdempotencyStaged, G04FinalDecisionPersistenceFaultPoint.BeforeCommitPublish
    };
    foreach (var fp in finalPoints)
    {
        var fx = await SetupFinalAsync(create);
        var fxBefore = await fx.F.Counts(fx.Idea.AggregateId, fx.Assessment.AssessmentId);
        var (freqX, fcomX) = BuildFinal(fx.Idea, fx.Plan, fx.Assessment, "kfx", "FPFX", "CFX", false);
        fx.F.SetFault(fp);
        var threwF = false;
        try { await fx.F.Store.CommitFinalDecisionAsync(freqX, fcomX); }
        catch (PersistenceAtomicityException) { threwF = true; }
        fx.F.SetFault(G04FinalDecisionPersistenceFaultPoint.None);
        Check($"{name}: final-decision fault at {fp} -> full rollback",
            threwF && await fx.F.Store.GetIdeaAsync(fx.Idea.AggregateId) == fx.Idea
            && await fx.F.Store.GetPlanAsync(fx.Plan.PlanId) == fx.Plan
            && await fx.F.Store.GetAssessmentAsync(fx.Assessment.AssessmentId) == fx.Assessment
            && await fx.F.Evidence(fx.Assessment.AssessmentId) is null
            && Delta(fxBefore, await fx.F.Counts(fx.Idea.AggregateId, fx.Assessment.AssessmentId)) == (0, 0, 0, 0));
    }

    // 22) Portfolio thread: eligibility creates the candidate, four commands grow the thread to the execution hand-off
    var pb = await create(Seed(NewId()));
    var pf = pb.MakePortfolio();
    var pIdea = NewId();
    var pCand = "PC-" + pIdea;
    var pBefore = await pf.Counts(pIdea, pCand);
    var (ereq, ecom) = BuildEligibility(pIdea, 2, pCand, "CPE", "FPE1");
    var eres = await pf.Store.CommitEligibilityAsync(ereq, ecom);
    Check($"{name}: eligibility creates the portfolio candidate", eres.Allowed && eres.Code == "P1_PORTFOLIO_CANDIDATE_CREATED" && eres.NewVersion == 1);
    Check($"{name}: thread reads back by candidate field-for-field", await pf.Store.GetByCandidateAsync(pCand) == ecom.After);
    Check($"{name}: thread reads back by idea version field-for-field", await pf.Store.GetByIdeaVersionAsync(pIdea, 2) == ecom.After);
    Check($"{name}: eligibility evidence delta = 1 audit, 2 outbox, 1 idempotency", Delta(pBefore, await pf.Counts(pIdea, pCand)) == (1, 2, 1, 0));
    var ereplay = await pf.Store.CommitEligibilityAsync(ereq, ecom);
    Check($"{name}: eligibility replay is idempotent", ereplay.IdempotentReplay && !ereplay.StateMutated);
    var (ereq2, ecom2) = BuildEligibility(pIdea, 2, pCand, "CPE2", "FPE2");
    var ealready = await pf.Store.CommitEligibilityAsync(ereq2, ecom2);
    Check($"{name}: second eligibility for the same idea version is already materialized",
        ealready.Allowed && !ealready.StateMutated && ealready.Code == "P1_PORTFOLIO_ELIGIBILITY_ALREADY_MATERIALIZED");

    var thread = ecom.After;
    PortfolioCommandRequestWave9? firstReq = null;
    PortfolioCommitWave9? firstCom = null;
    var steps = new (string Name, Func<PortfolioThreadEnvelope, DateTimeOffset, PortfolioThreadEnvelope> Advance)[]
    {
        ("portfolio.assign", (x, at) => x with
        {
            Candidate = x.Candidate with { State = "ASSIGNED_CANDIDATE", Version = x.Candidate.Version + 1 },
            Assignment = new PortfolioAssignmentEnvelope("PA-" + x.Candidate.CandidateId, x.Candidate.CandidateId, "PF-1", "ASSIGNED", 1, at, "CPS")
        }),
        ("portfolio.decide-membership", (x, at) => x with
        {
            Candidate = x.Candidate with { State = "MEMBER", Version = x.Candidate.Version + 1 },
            Membership = new PortfolioMembershipEnvelope("PM-" + x.Candidate.CandidateId, x.Candidate.CandidateId, "PA-" + x.Candidate.CandidateId, "PF-1", "ACCEPTED", 1, at, "P1", "ASG-1", "CPS")
        }),
        ("portfolio.recommend-execution", (x, at) => x with
        {
            Candidate = x.Candidate with { State = "RECOMMENDED", Version = x.Candidate.Version + 1 },
            Recommendation = new ExecutionRecommendationEnvelope("PR-" + x.Candidate.CandidateId, x.Candidate.CandidateId, "PM-" + x.Candidate.CandidateId, "APPROVED", 1, "GOV-1", true, "BASE-1", at, "CPS")
        }),
        ("portfolio.handoff-execution", (x, at) => x with
        {
            Candidate = x.Candidate with { State = "HANDED_OFF", Version = x.Candidate.Version + 1 },
            Execution = new ExecutionHandoffEnvelope("PX-" + x.Candidate.CandidateId, "PR-" + x.Candidate.CandidateId, x.Candidate.CandidateId, x.Candidate.IdeaId, x.Candidate.ApprovedIdeaVersion, "HANDOFF_PENDING", 1, at, "CPS")
        })
    };
    var stepNo = 0;
    foreach (var step in steps)
    {
        stepNo++;
        var (sreq, scom) = BuildPortfolioCommand(thread, step.Name, "kps" + stepNo, "FPS" + stepNo, "CPS" + stepNo, step.Advance, false);
        var sres = await pf.Store.CommitPortfolioCommandAsync(sreq, scom);
        Check($"{name}: portfolio command '{step.Name}' commits and the thread reads back field-for-field",
            sres.Allowed && sres.Code == "P1_PORTFOLIO_COMMAND_COMMITTED" && await pf.Store.GetByCandidateAsync(pCand) == scom.After);
        if (stepNo == 1) { firstReq = sreq; firstCom = scom; }
        thread = scom.After;
    }
    var preplay = await pf.Store.CommitPortfolioCommandAsync(firstReq!, firstCom!);
    Check($"{name}: portfolio command replay is idempotent", preplay.IdempotentReplay && !preplay.StateMutated);
    var (stReq, stCom) = BuildPortfolioCommand(ecom.After, "portfolio.assign", "kps-stale", "FPS-stale", "CPS-stale", steps[0].Advance, false);
    var pstale = await pf.Store.CommitPortfolioCommandAsync(stReq, stCom);
    Check($"{name}: stale thread version -> 409 P1_PORTFOLIO_CONCURRENT_CONFLICT", pstale.HttpStatus == 409 && pstale.Code == "P1_PORTFOLIO_CONCURRENT_CONFLICT");
    var (kReq, kCom) = BuildPortfolioCommand(ecom.After, "portfolio.assign", "kps1", "OTHER-FP", "CPS-key", steps[0].Advance, false);
    var pkey = await pf.Store.CommitPortfolioCommandAsync(kReq, kCom);
    Check($"{name}: portfolio key reuse with different fingerprint -> 409 P1_IDEMPOTENCY_CONFLICT", pkey.HttpStatus == 409 && pkey.Code == "P1_IDEMPOTENCY_CONFLICT");

    var invIdea = NewId();
    var invCand = "PC-" + invIdea;
    var pi = pb.MakePortfolio();
    var (ireq, icom) = BuildEligibility(invIdea, 1, invCand, "CPI", "FPI1");
    await pi.Store.CommitEligibilityAsync(ireq, icom);
    var (breq, bcom) = BuildPortfolioCommand(icom.After, "portfolio.assign", "kpi", "FPI2", "CPI2", steps[0].Advance, true);
    string? portfolioInvalid = null;
    try { await pi.Store.CommitPortfolioCommandAsync(breq, bcom); }
    catch (InvalidOperationException ex) { portfolioInvalid = ex.Message; }
    Check($"{name}: thread version not advanced -> P1_PORTFOLIO_THREAD_VERSION_INVALID and nothing written",
        portfolioInvalid == "P1_PORTFOLIO_THREAD_VERSION_INVALID" && await pi.Store.GetByCandidateAsync(invCand) == icom.After);

    // 23) rollback at every portfolio fault point (eligibility path and command path)
    var portfolioPoints = new[]
    {
        PortfolioWave9PersistenceFaultPoint.AfterThreadStaged, PortfolioWave9PersistenceFaultPoint.AfterAuditStaged,
        PortfolioWave9PersistenceFaultPoint.AfterOutboxStaged, PortfolioWave9PersistenceFaultPoint.AfterIdempotencyStaged,
        PortfolioWave9PersistenceFaultPoint.BeforeCommitPublish
    };
    foreach (var pp in portfolioPoints)
    {
        var fIdea = NewId();
        var fCand = "PC-" + fIdea;
        var fh = pb.MakePortfolio();
        var fBase = await fh.Counts(fIdea, fCand);
        var (feReq, feCom) = BuildEligibility(fIdea, 3, fCand, "CPF", "FPF1");
        fh.SetFault(pp);
        var threwE2 = false;
        try { await fh.Store.CommitEligibilityAsync(feReq, feCom); }
        catch (PersistenceAtomicityException) { threwE2 = true; }
        fh.SetFault(PortfolioWave9PersistenceFaultPoint.None);
        Check($"{name}: eligibility fault at {pp} -> full rollback",
            threwE2 && await fh.Store.GetByCandidateAsync(fCand) is null && Delta(fBase, await fh.Counts(fIdea, fCand)) == (0, 0, 0, 0));
    }
    foreach (var pp in portfolioPoints)
    {
        var cIdea = NewId();
        var cCand = "PC-" + cIdea;
        var ch = pb.MakePortfolio();
        var (ceReq, ceCom) = BuildEligibility(cIdea, 4, cCand, "CPC", "FPC1");
        await ch.Store.CommitEligibilityAsync(ceReq, ceCom);
        var cBase = await ch.Counts(cIdea, cCand);
        var (ccReq, ccCom) = BuildPortfolioCommand(ceCom.After, "portfolio.assign", "kpc", "FPC2", "CPC2", steps[0].Advance, false);
        ch.SetFault(pp);
        var threwC = false;
        try { await ch.Store.CommitPortfolioCommandAsync(ccReq, ccCom); }
        catch (PersistenceAtomicityException) { threwC = true; }
        ch.SetFault(PortfolioWave9PersistenceFaultPoint.None);
        Check($"{name}: portfolio command fault at {pp} -> full rollback",
            threwC && await ch.Store.GetByCandidateAsync(cCand) == ceCom.After && Delta(cBase, await ch.Counts(cIdea, cCand)) == (0, 0, 0, 0));
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

async Task<(Backend B, AggregateSnapshot Idea, EvaluationPlanEnvelope Plan, G04AssessmentEnvelope Assessment, FinalHandle F)> SetupFinalAsync(
    Func<AggregateSnapshot, Task<Backend>> factory)
{
    var g = await SetupG04Async(factory);
    var plan = (await g.B.Store.GetEvaluationPlanAsync(g.Assessment.PlanId))!;
    return (g.B, g.Idea, plan, g.Assessment, g.B.MakeFinal(g.Idea, plan, g.Assessment));
}

static (int, int, int, int) Delta((int, int, int, int) before, (int, int, int, int) after) =>
    (after.Item1 - before.Item1, after.Item2 - before.Item2, after.Item3 - before.Item3, after.Item4 - before.Item4);

static (G04FinalDecisionRequest Request, G04FinalDecisionCommit Commit) BuildFinal(
    AggregateSnapshot idea, EvaluationPlanEnvelope plan, G04AssessmentEnvelope assessment,
    string key, string fingerprint, string corr, bool breakTechnicalVersion)
{
    const string command = "g04.final-decision";
    var now = DateTimeOffset.UtcNow;
    var ts = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    var roles = new[] { "G04_COMMITTEE_CHAIR" };
    var actor = new AuthorityActor("P-CHAIR", "DOMAIN\\chair", "TEST", "ASG-CHAIR", roles, new[] { "SCOPE-A" });
    var tag = key + "-" + assessment.AssessmentId;
    var technical = idea.StateMutationVersion ?? idea.Version;
    var ideaAfter = idea with { State = "APPROVED", StateMutationVersion = breakTechnicalVersion ? technical : technical + 1 };
    var planAfter = plan with { PlanVersion = plan.PlanVersion + 1, State = "DECIDED" };
    var assessmentAfter = assessment with { State = "DECIDED", DecisionVersion = assessment.DecisionVersion + 1 };
    var decisionId = "GD-" + tag;
    var evidence = new G04FinalDecisionEvidenceEnvelope(
        decisionId, assessment.AssessmentId, plan.PlanId, idea.AggregateId, idea.Version, ideaAfter.Version, technical,
        ideaAfter.StateMutationVersion ?? ideaAfter.Version, assessment.DecisionVersion, assessmentAfter.DecisionVersion,
        "APPROVED", "R-1", "Decision comment text", null, "P-CHAIR", "ASG-CHAIR", "DOMAIN\\chair", "TEST",
        "G04_COMMITTEE", "COMMITTEE", "MAJORITY", "GP-1", "1.0", "DP-1", "1.0", "{\"p\":1}", new string('c', 64), "RS-1.0", "HC-1",
        80, 90, "{\"checks\":\"ok\"}", "none", null, null, null, null, "PASS", null, ts, corr);
    var audit = new AuditEnvelope("AFD-" + tag, "P-CHAIR", "DOMAIN\\chair", "TEST", roles, "ASG-CHAIR", idea.AggregateId, idea.Version,
        "RS-1.0", ts, corr, command);
    var outbox = new OutboxEnvelope("OBFD-" + tag, "G04FinalDecisionRecorded.v1", idea.AggregateId, idea.Version, corr, ts,
        new Dictionary<string, string> { ["decisionEvidenceId"] = decisionId });
    var cmd = new G04FinalDecisionCommand(idea.AggregateId, idea.Version, technical, plan.PlanId, plan.PlanVersion,
        assessment.AssessmentId, assessment.DecisionVersion, "APPROVED", "R-1", "Decision comment text", null, key, corr);
    return (new G04FinalDecisionRequest(cmd, actor, idea, plan, assessment, fingerprint),
        new G04FinalDecisionCommit(ideaAfter, planAfter, assessmentAfter, evidence, audit, outbox));
}

static (PortfolioEligibilityRequestWave9 Request, PortfolioCommitWave9 Commit) BuildEligibility(
    string ideaId, long approvedVersion, string candidateId, string corr, string fingerprint)
{
    const string command = "portfolio.evaluate-eligibility-from-approved-idea";
    var now = DateTimeOffset.UtcNow;
    var ts = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    var tag = candidateId + "-" + fingerprint;
    var trigger = new PortfolioEligibilityTrigger("IdeaApproved.v1", ideaId, approvedVersion, "APPROVED", 0, false, corr);
    var candidate = new PortfolioCandidateEnvelope(candidateId, ideaId, approvedVersion, "UNASSIGNED_CANDIDATE", 1, ts, corr);
    var thread = new PortfolioThreadEnvelope(candidate, null, null, null, null, 1);
    var audit = new AuditEnvelope("APE-" + tag, "SYSTEM", "SYSTEM", "SYSTEM", new[] { "SYSTEM" }, "ASG-SYS", candidateId, 1, "RS-1.0", ts, corr, command);
    var events = new[]
    {
        new OutboxEnvelope("OBPE1-" + tag, "PortfolioEligibilityEvaluated.v1", candidateId, 1, corr, ts, new Dictionary<string, string> { ["k"] = "v" }),
        new OutboxEnvelope("OBPE2-" + tag, "PortfolioIntakeCandidateCreated.v1", candidateId, 1, corr, ts, new Dictionary<string, string> { ["k"] = "v" })
    };
    return (new PortfolioEligibilityRequestWave9(trigger, fingerprint), new PortfolioCommitWave9(thread, audit, events));
}

static (PortfolioCommandRequestWave9 Request, PortfolioCommitWave9 Commit) BuildPortfolioCommand(
    PortfolioThreadEnvelope before, string commandName, string key, string fingerprint, string corr,
    Func<PortfolioThreadEnvelope, DateTimeOffset, PortfolioThreadEnvelope> advance, bool keepThreadVersion)
{
    var now = DateTimeOffset.UtcNow;
    var ts = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    var candidateId = before.Candidate.CandidateId;
    var roles = new[] { "PORTFOLIO_MANAGER" };
    var after = advance(before, ts) with { ThreadVersion = before.ThreadVersion + (keepThreadVersion ? 0 : 1) };
    var tag = key + "-" + candidateId;
    var command = new PortfolioCommandWave9(commandName, candidateId, before.ThreadVersion, key, corr);
    var actor = new AuthorityActor("P1", "DOMAIN\\p1", "TEST", "ASG-1", roles, new[] { "SCOPE-A" });
    var policy = new CommandPolicy(commandName, roles, new[] { before.Candidate.State }, "RS-1.0", "PortfolioCommandApplied");
    var audit = new AuditEnvelope("APC-" + tag, "P1", "DOMAIN\\p1", "TEST", roles, "ASG-1", candidateId, after.ThreadVersion, "RS-1.0", ts, corr, commandName);
    var events = new[]
    {
        new OutboxEnvelope("OBPC-" + tag, "PortfolioCommandApplied.v1", candidateId, after.ThreadVersion, corr, ts, new Dictionary<string, string> { ["k"] = "v" })
    };
    return (new PortfolioCommandRequestWave9(command, actor, before, policy, fingerprint), new PortfolioCommitWave9(after, audit, events));
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

internal sealed record PortfolioHandle(
    IPortfolioWave9Store Store,
    Action<PortfolioWave9PersistenceFaultPoint> SetFault,
    Func<string, string, Task<(int, int, int, int)>> Counts);

internal sealed record FinalHandle(
    IG04FinalDecisionStore Store,
    Action<G04FinalDecisionPersistenceFaultPoint> SetFault,
    Func<string, string, Task<(int, int, int, int)>> Counts,
    Func<string, Task<G04FinalDecisionEvidenceEnvelope?>> Evidence);

internal sealed class Backend
{
    public required IEvaluationWorkflowStore Store { get; init; }
    public required IFaultInjectablePersistence Fault { get; init; }
    public required Func<string, Task<(int, int, int, int)>> Counts { get; init; }
    public required Func<string, Task<AssessmentSnapshotEnvelope?>> SnapshotFor { get; init; }
    public required IG04VotingStore Voting { get; init; }
    public required Action<G04VotingPersistenceFaultPoint> SetVotingFault { get; init; }
    public required Func<string, Task<(int, int, int, int)>> VotingCounts { get; init; }
    public required Func<AggregateSnapshot, EvaluationPlanEnvelope, G04AssessmentEnvelope, FinalHandle> MakeFinal { get; init; }
    public required Func<PortfolioHandle> MakePortfolio { get; init; }

    public static Backend InMemory(AggregateSnapshot seed)
    {
        var s = new TransactionalAuthorityStore(null, seed);
        var v = new G04VotingTransactionalStore(s);
        return new Backend
        {
            MakePortfolio = () =>
            {
                var ps = new PortfolioTransactionalStoreWave9();
                return new PortfolioHandle(ps, p => ps.FaultPoint = p,
                    (_, _) => Task.FromResult((ps.PortfolioAuditLog.Count, ps.PortfolioOutbox.Count, ps.PortfolioIdempotencyRecords.Count, 0)));
            },
            MakeFinal = (idea, plan, assessment) =>
            {
                var f = new G04FinalDecisionTransactionalStore(idea, plan, assessment);
                return new FinalHandle(f, p => f.FaultPoint = p,
                    (_, _) => Task.FromResult((f.FinalDecisionAuditLog.Count, f.FinalDecisionOutbox.Count, f.FinalDecisionIdempotencyRecords.Count, f.FinalDecisionEvidence.Count)),
                    _ => Task.FromResult(f.FinalDecisionEvidence.FirstOrDefault()));
            },
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
            MakePortfolio = () => new PortfolioHandle(s, p => s.PortfolioFaultPoint = p,
                async (ideaId, candidateId) =>
                {
                    var byCandidate = await s.CountEvidenceAsync(candidateId);
                    var byIdea = await s.CountEvidenceAsync(ideaId);
                    return (byCandidate.Audit, byCandidate.Outbox, byCandidate.Idempotency + byIdea.Idempotency, 0);
                }),
            MakeFinal = (_, _, _) => new FinalHandle(s, p => s.FinalDecisionFaultPoint = p,
                async (ideaId, assessmentId) =>
                {
                    var c = await s.CountEvidenceAsync(ideaId);
                    return (c.Audit, c.Outbox, c.Idempotency, await s.CountFinalDecisionEvidenceAsync(assessmentId));
                },
                assessmentId => s.GetFinalDecisionEvidenceAsync(assessmentId)),
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
