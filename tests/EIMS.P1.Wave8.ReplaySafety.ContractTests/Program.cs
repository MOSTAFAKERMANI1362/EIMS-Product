using System.Security.Cryptography;
using System.Text;
using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;

var route = new G04DecisionRouteMetadata("G04_COMMITTEE", "COMMITTEE", "MAJORITY", "GOV-1", "1.0");
var idea = new AggregateSnapshot("IDEA-1", "Idea", "UNDER_REVIEW", 7, "P-OWNER", "IDEA_OWNER", "UNIT:RND", null, null, 10);
var plan = new EvaluationPlanEnvelope(
    "PLAN-1", "IDEA-1", 7, 3, "READY_FOR_G04_DECISION", DateTimeOffset.UtcNow.AddMinutes(-20), "CORR-P",
    DateTimeOffset.UtcNow.AddMinutes(-5), route.DecisionRoute, route.DecisionRouteKind, route.DecisionMethod,
    route.GovernanceProfileId, route.GovernanceProfileVersion);
var assessment = new G04AssessmentEnvelope(
    "G04-1", "PLAN-1", "IDEA-1", 7, 3, "PENDING", "READY-SHA", DateTimeOffset.UtcNow.AddMinutes(-5), "CORR-A",
    route.DecisionRoute, route.DecisionRouteKind, route.DecisionMethod, route.GovernanceProfileId, route.GovernanceProfileVersion, 0);
var snapshot = new G04CommitteeSnapshotEnvelope(
    "SNAP-1", "G04-1", "IDEA-1", 7, "UNIT:RND", "G04_COMMITTEE", "GOV-1", "1.0", 1, "MAJORITY",
    "P-M1", "IDEA_DECISION", "GOV-REF",
    new[] { new G04CommitteeMemberEnvelope("P-M1", "Member 1", new[] { "G04_COMMITTEE_MEMBER" }, new[] { "ASG-M1" }) },
    DateTimeOffset.UtcNow.AddMinutes(-4), "CORR-VOTE");
var committeeState = new G04CommitteeStateEnvelope(
    "G04-1", "SNAP-1", 1, "COMPLETED", true, false, 1, 0, 1,
    DateTimeOffset.UtcNow.AddMinutes(-3), "CORR-VOTE", DateTimeOffset.UtcNow.AddMinutes(-3));

var store = new G04FinalDecisionTransactionalStore(idea, plan, assessment, snapshot, committeeState);
var profileJson = "{\"profile\":\"G04-GENERAL-V1.0\"}";
var evidence = new G04DecisionProfileEvidence(
    "G04-GENERAL-V1.0", "1.0", profileJson, Sha256(profileJson), "G04-RS-1.0", "6161",
    50, 65, 70, 80, true, false, false, false,
    "NEGATIVE_OUTCOME_ALLOWED", "MORE_EVIDENCE", "PASS");
var service = new G04FinalDecisionServiceWave8(store, new StaticG04FinalDecisionEvidenceProvider(evidence));
var actor = new AuthorityActor(
    "P-D1", "DOMAIN\\p-d1", "WINDOWS_PRINCIPAL", "ASG-D1",
    new[] { "IDEA_DECISION" }, new[] { "UNIT:RND" });
var command = new G04FinalDecisionCommand(
    "IDEA-1", 7, 10, "PLAN-1", 3, "G04-1", 0,
    "RETURN", "G04_EVIDENCE_INCOMPLETE", "Return for additional evidence and correction.", null,
    "RETURN-REPLAY-KEY", "CORR-RETURN-1", "UNIT:RND");

var first = await service.DecideAsync(command, actor);
Check(first.HttpStatus == 200 && first.StateMutated && !first.IdempotentReplay, "first RETURN must commit");
Check(store.CurrentIdea.State == "RETURNED" && store.CurrentIdea.Version == 8, "RETURN must increment business revision exactly once");
Check(store.CurrentIdea.StateMutationVersion == 11, "RETURN must increment technical mutation version exactly once");
Check(store.CurrentPlan.State == "SUPERSEDED", "RETURN must supersede prior evaluation plan");

var replay = await service.DecideAsync(command with { CorrelationId = "CORR-RETURN-RETRY" }, actor);
Check(replay.HttpStatus == 200 && replay.IdempotentReplay && !replay.StateMutated, "exact RETURN retry must replay prior result");
Check(store.CurrentIdea.Version == 8 && store.CurrentIdea.StateMutationVersion == 11, "replay must not increment either version again");
Check(store.FinalDecisionEvidence.Count == 1, "replay must not duplicate immutable decision evidence");
Check(store.FinalDecisionAuditLog.Count == 1, "replay must not duplicate audit");
Check(store.FinalDecisionOutbox.Count == 1, "replay must not duplicate outbox");
Check(store.FinalDecisionIdempotencyRecords.Count == 1, "replay must retain one idempotency record");

var conflict = await service.DecideAsync(
    command with { DecisionComment = "Changed retry payload must conflict.", CorrelationId = "CORR-RETURN-CONFLICT" }, actor);
Check(conflict.HttpStatus == 409 && conflict.Code == "P1_IDEMPOTENCY_CONFLICT", "same key with changed payload must conflict");
Check(store.FinalDecisionEvidence.Count == 1 && store.FinalDecisionOutbox.Count == 1, "conflict must not mutate persistence");

Console.WriteLine("RESULT 13/13 PASS");
return 0;

static string Sha256(string value) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS {message}");
}
