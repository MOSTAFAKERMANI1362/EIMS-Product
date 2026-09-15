using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;

if (args.Length != 2 || args.Any(x => !File.Exists(x)))
{
    Console.Error.WriteLine("Usage: EIMS.P1.Wave8.G04FinalDecision.ContractTests <ACR-P0-007-json> <P1-wave8-json>");
    return 2;
}

using var acrDoc = JsonDocument.Parse(File.ReadAllText(args[0]));
using var waveDoc = JsonDocument.Parse(File.ReadAllText(args[1]));
var acr = acrDoc.RootElement;
var wave = waveDoc.RootElement;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("W8F-01 ACR and Wave8 identities are exact", ArtifactIdentity),
    ("W8F-02 cumulative catalog promotes exactly six mutations", CatalogPromotion),
    ("W8F-03 APPROVE commits without changing business Idea revision", ApproveSuccess),
    ("W8F-04 RETURN increments business revision and supersedes Plan", ReturnSuccess),
    ("W8F-05 HOLD requires and freezes review date", HoldSuccess),
    ("W8F-06 REJECT closes assessment without changing business revision", RejectSuccess),
    ("W8F-07 short final comment is denied", ShortCommentDenied),
    ("W8F-08 approve requires G04_READY reason", ApproveReasonDenied),
    ("W8F-09 negative outcome cannot use G04_READY reason", NegativeReadyReasonDenied),
    ("W8F-10 HOLD without review date is denied", HoldDateDenied),
    ("W8F-11 exact IDEA_DECISION role is required", WrongRoleDenied),
    ("W8F-12 authoritative Idea scope is enforced", WrongScopeDenied),
    ("W8F-13 stale business Idea revision is denied", StaleIdeaRevisionDenied),
    ("W8F-14 stale technical mutation version is denied", StaleTechnicalVersionDenied),
    ("W8F-15 Plan and Assessment route mismatch is denied", RouteMismatchDenied),
    ("W8F-16 committee route requires completed voting stage for every outcome", CommitteeStageRequired),
    ("W8F-17 committee APPROVE requires positive approval result", CommitteePositiveRequired),
    ("W8F-18 negative outcome may proceed after completed non-approving committee result", NegativeAfterNonApprovalAllowed),
    ("W8F-19 frozen committee member cannot be final authority", CommitteeMemberSodDenied),
    ("W8F-20 individual route may finalize without committee evidence", IndividualRouteSuccess),
    ("W8F-21 individual route rejects unexpected committee context", IndividualCommitteeContextDenied),
    ("W8F-22 score threshold blocks APPROVE only", ScoreGateApproveOnly),
    ("W8F-23 specialist blocking outcome blocks APPROVE", SpecialistBlocksApprove),
    ("W8F-24 missing or corrupt frozen profile evidence fails closed", EvidenceIntegrityDenied),
    ("W8F-25 evaluator/final-decider conflict defaults deny for APPROVE", EvaluatorConflictDenied),
    ("W8F-26 frozen ALLOW_WITH_AUDIT exception is preserved in evidence", EvaluatorConflictAuditedException),
    ("W8F-27 exact replay is idempotent and duplicates no evidence", ExactReplay),
    ("W8F-28 changed replay conflicts", ChangedReplayConflict),
    ("W8F-29 all final-decision fault points roll back atomically", AtomicRollback),
    ("W8F-30 decision comment/profile snapshot stay out of integration event", EventMinimization),
    ("W8F-31 G04 transaction does not create PortfolioIntakeCandidate", NoPortfolioCandidateCreation),
    ("W8F-32 reference persistence remains logically ready but Oracle-unbound", PersistenceBoundary),
    ("W8F-33 Wave8 safety claims remain bounded", SafetyBoundaries)
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

Task ArtifactIdentity()
{
    Eq("ACR-P0-007", S(acr, "acrId"));
    Eq("APPROVED_FOR_P1_IMPLEMENTATION", S(acr, "status"));
    Eq("EIMS-P1-RECOVERY-WAVE8-G04-FINAL-DECISION-REBASELINE-1.0", S(wave, "schema"));
    Eq("IMPLEMENTED_PENDING_CONTRACT_AND_REGRESSION_GATES", S(wave, "status"));
    return Task.CompletedTask;
}

Task CatalogPromotion()
{
    var catalog = new RecoveredApiCommandCatalogWave8();
    Eq(21, catalog.All.Count);
    Eq(6, catalog.All.Count(x => x.MutationContractRecovered));
    Eq(6, RecoveredApiCommandCatalogWave8.Wave8RecoveredMutationCommandCount);
    True(catalog.TryGet("g04.final-decision", out var policy));
    True(policy.MutationContractRecovered);
    Eq("IdeaApprovedForPortfolio.v1", policy.ResolveEventName("APPROVE")!);
    Eq("IdeaReturnedFromG04.v1", policy.ResolveEventName("RETURN")!);
    Eq("IdeaHeldAtG04.v1", policy.ResolveEventName("HOLD")!);
    Eq("IdeaRejectedAtG04.v1", policy.ResolveEventName("REJECT")!);
    return Task.CompletedTask;
}

async Task ApproveSuccess()
{
    var r = CommitteeRuntime();
    var result = await r.Service.DecideAsync(Cmd("APPROVE"), FinalActor());
    Eq(200, result.HttpStatus);
    Eq("APPROVED", r.Store.CurrentIdea.State);
    Eq(7L, r.Store.CurrentIdea.Version);
    Eq(11L, r.Store.CurrentIdea.StateMutationVersion!.Value);
    Eq("DECIDED", r.Store.CurrentAssessment.State);
    Eq(1, r.Store.CurrentAssessment.DecisionVersion);
    Eq("IdeaApprovedForPortfolio.v1", Single(r.Store.FinalDecisionOutbox).EventName);
}

async Task ReturnSuccess()
{
    var r = CommitteeRuntime(committeeApproval: false);
    var result = await r.Service.DecideAsync(Cmd("RETURN"), FinalActor());
    Eq(200, result.HttpStatus);
    Eq("RETURNED", r.Store.CurrentIdea.State);
    Eq(8L, r.Store.CurrentIdea.Version);
    Eq(11L, r.Store.CurrentIdea.StateMutationVersion!.Value);
    Eq("SUPERSEDED", r.Store.CurrentPlan.State);
    Eq(4, r.Store.CurrentPlan.PlanVersion);
    Eq("IdeaReturnedFromG04.v1", Single(r.Store.FinalDecisionOutbox).EventName);
}

async Task HoldSuccess()
{
    var r = CommitteeRuntime(committeeApproval: false);
    var review = DateTimeOffset.UtcNow.AddDays(7);
    var result = await r.Service.DecideAsync(Cmd("HOLD", reviewDate: review), FinalActor());
    Eq(200, result.HttpStatus);
    Eq("HOLD", r.Store.CurrentIdea.State);
    Eq(7L, r.Store.CurrentIdea.Version);
    Eq(review, Single(r.Store.FinalDecisionEvidence).ReviewDate!.Value);
    Eq("IdeaHeldAtG04.v1", Single(r.Store.FinalDecisionOutbox).EventName);
}

async Task RejectSuccess()
{
    var r = CommitteeRuntime(committeeApproval: false);
    var result = await r.Service.DecideAsync(Cmd("REJECT"), FinalActor());
    Eq(200, result.HttpStatus);
    Eq("REJECTED", r.Store.CurrentIdea.State);
    Eq(7L, r.Store.CurrentIdea.Version);
    Eq("DECIDED", r.Store.CurrentAssessment.State);
    Eq("IdeaRejectedAtG04.v1", Single(r.Store.FinalDecisionOutbox).EventName);
}

async Task ShortCommentDenied()
{
    var r = CommitteeRuntime();
    var result = await r.Service.DecideAsync(Cmd("APPROVE") with { DecisionComment = "too short" }, FinalActor());
    Eq(422, result.HttpStatus); Eq("P1_G04_FINAL_COMMENT_REQUIRED", result.Code); Pristine(r.Store);
}

async Task ApproveReasonDenied()
{
    var r = CommitteeRuntime();
    var result = await r.Service.DecideAsync(Cmd("APPROVE") with { ReasonCode = "OTHER" }, FinalActor());
    Eq(422, result.HttpStatus); Eq("P1_G04_APPROVE_REASON_MUST_BE_READY", result.Code); Pristine(r.Store);
}

async Task NegativeReadyReasonDenied()
{
    var r = CommitteeRuntime(committeeApproval: false);
    var result = await r.Service.DecideAsync(Cmd("RETURN") with { ReasonCode = "G04_READY" }, FinalActor());
    Eq(422, result.HttpStatus); Eq("P1_G04_NEGATIVE_REASON_CANNOT_BE_READY", result.Code); Pristine(r.Store);
}

async Task HoldDateDenied()
{
    var r = CommitteeRuntime(committeeApproval: false);
    var result = await r.Service.DecideAsync(Cmd("HOLD", reviewDate: null), FinalActor());
    Eq(422, result.HttpStatus); Eq("P1_G04_HOLD_REVIEW_DATE_REQUIRED", result.Code); Pristine(r.Store);
}

async Task WrongRoleDenied()
{
    var r = CommitteeRuntime();
    var actor = FinalActor() with { Roles = new[] { "G04_COMMITTEE_MEMBER" } };
    var result = await r.Service.DecideAsync(Cmd("APPROVE"), actor);
    Eq(403, result.HttpStatus); Eq("P1_G04_FINAL_AUTHORITY_REQUIRED", result.Code); Pristine(r.Store);
}

async Task WrongScopeDenied()
{
    var r = CommitteeRuntime();
    var actor = FinalActor() with { Scopes = new[] { "UNIT:FIN" } };
    var result = await r.Service.DecideAsync(Cmd("APPROVE"), actor);
    Eq(403, result.HttpStatus); Eq("P1_G04_FINAL_SCOPE_DENIED", result.Code); Pristine(r.Store);
}

async Task StaleIdeaRevisionDenied()
{
    var r = CommitteeRuntime();
    var result = await r.Service.DecideAsync(Cmd("APPROVE") with { ExpectedIdeaRevision = 6 }, FinalActor());
    Eq(409, result.HttpStatus); Eq("P1_G04_IDEA_REVISION_CONFLICT", result.Code); Pristine(r.Store);
}

async Task StaleTechnicalVersionDenied()
{
    var r = CommitteeRuntime();
    var result = await r.Service.DecideAsync(Cmd("APPROVE") with { ExpectedStateMutationVersion = 9 }, FinalActor());
    Eq(409, result.HttpStatus); Eq("P1_G04_STATE_MUTATION_VERSION_CONFLICT", result.Code); Pristine(r.Store);
}

async Task RouteMismatchDenied()
{
    var idea = Idea();
    var plan = Plan(CommitteeRoute());
    var bad = Assessment(new G04DecisionRouteMetadata("G04_COMMITTEE", "COMMITTEE", "CONSENSUS", "GOV-1", "1.0"));
    var store = new G04FinalDecisionTransactionalStore(idea, plan, bad, CommitteeSnapshot(), CommitteeState(true, true));
    var service = new G04FinalDecisionService(store, new StaticG04FinalDecisionEvidenceProvider(GoodEvidence()));
    var result = await service.DecideAsync(Cmd("APPROVE"), FinalActor());
    Eq(409, result.HttpStatus); Eq("P1_G04_FINAL_ROUTE_MISMATCH", result.Code); Pristine(store);
}

async Task CommitteeStageRequired()
{
    var r = CommitteeRuntime(stageCompleted: false, committeeApproval: false);
    var result = await r.Service.DecideAsync(Cmd("RETURN"), FinalActor());
    Eq(409, result.HttpStatus); Eq("P1_G04_COMMITTEE_STAGE_NOT_COMPLETED", result.Code); Pristine(r.Store);
}

async Task CommitteePositiveRequired()
{
    var r = CommitteeRuntime(committeeApproval: false);
    var result = await r.Service.DecideAsync(Cmd("APPROVE"), FinalActor());
    Eq(422, result.HttpStatus); Eq("P1_G04_COMMITTEE_APPROVAL_NOT_SATISFIED", result.Code); Pristine(r.Store);
}

async Task NegativeAfterNonApprovalAllowed()
{
    var r = CommitteeRuntime(committeeApproval: false);
    var result = await r.Service.DecideAsync(Cmd("REJECT"), FinalActor());
    Eq(200, result.HttpStatus); Eq("REJECTED", r.Store.CurrentIdea.State);
}

async Task CommitteeMemberSodDenied()
{
    var r = CommitteeRuntime();
    var actor = FinalActor("P-M1");
    var result = await r.Service.DecideAsync(Cmd("APPROVE"), actor);
    Eq(403, result.HttpStatus); Eq("SOD_G04_MEMBER_NOT_FINAL_AUTHORITY", result.Code); Pristine(r.Store);
}

async Task IndividualRouteSuccess()
{
    var r = IndividualRuntime();
    var result = await r.Service.DecideAsync(Cmd("APPROVE"), FinalActor());
    Eq(200, result.HttpStatus); Eq("APPROVED", r.Store.CurrentIdea.State);
}

async Task IndividualCommitteeContextDenied()
{
    var idea = Idea();
    var route = IndividualRoute();
    var store = new G04FinalDecisionTransactionalStore(idea, Plan(route), Assessment(route), CommitteeSnapshot(), CommitteeState(true, true));
    var service = new G04FinalDecisionService(store, new StaticG04FinalDecisionEvidenceProvider(GoodEvidence()));
    var result = await service.DecideAsync(Cmd("APPROVE"), FinalActor());
    Eq(409, result.HttpStatus); Eq("P1_G04_INDIVIDUAL_ROUTE_HAS_COMMITTEE_CONTEXT", result.Code); Pristine(store);
}

async Task ScoreGateApproveOnly()
{
    var low = GoodEvidence() with { GateScore = 50, PassThreshold = 65 };
    var approve = CommitteeRuntime(evidence: low);
    var denied = await approve.Service.DecideAsync(Cmd("APPROVE"), FinalActor());
    Eq(422, denied.HttpStatus); Eq("P1_G04_GATE_SCORE_BELOW_THRESHOLD", denied.Code); Pristine(approve.Store);

    var reject = CommitteeRuntime(committeeApproval: false, evidence: low);
    var accepted = await reject.Service.DecideAsync(Cmd("REJECT"), FinalActor());
    Eq(200, accepted.HttpStatus);
}

async Task SpecialistBlocksApprove()
{
    var r = CommitteeRuntime(evidence: GoodEvidence() with { SpecialistOutcomesNonBlocking = false });
    var result = await r.Service.DecideAsync(Cmd("APPROVE"), FinalActor());
    Eq(422, result.HttpStatus); Eq("P1_G04_SPECIALIST_OUTCOME_BLOCKS_APPROVAL", result.Code); Pristine(r.Store);
}

async Task EvidenceIntegrityDenied()
{
    var missing = CommitteeRuntime(provider: new NullEvidenceProvider());
    var r1 = await missing.Service.DecideAsync(Cmd("APPROVE"), FinalActor());
    Eq(503, r1.HttpStatus); Eq("P1_G04_DECISION_EVIDENCE_NOT_BOUND", r1.Code); Pristine(missing.Store);

    var corrupt = CommitteeRuntime(evidence: GoodEvidence() with { ProfileSnapshotSha256 = "bad" });
    var r2 = await corrupt.Service.DecideAsync(Cmd("APPROVE"), FinalActor());
    Eq(503, r2.HttpStatus); Eq("P1_G04_DECISION_EVIDENCE_NOT_BOUND", r2.Code); Pristine(corrupt.Store);
}

async Task EvaluatorConflictDenied()
{
    var r = CommitteeRuntime(evidence: GoodEvidence() with { EvaluatorConflictPolicy = "DENY" });
    var result = await r.Service.DecideAsync(Cmd("APPROVE"), FinalActor());
    Eq(403, result.HttpStatus); Eq("P1_G04_EVALUATOR_FINAL_DECIDER_CONFLICT", result.Code); Pristine(r.Store);
}

async Task EvaluatorConflictAuditedException()
{
    var r = CommitteeRuntime(evidence: GoodEvidence() with { EvaluatorConflictPolicy = "ALLOW_WITH_AUDIT" });
    var result = await r.Service.DecideAsync(Cmd("APPROVE"), FinalActor());
    Eq(200, result.HttpStatus);
    var ev = Single(r.Store.FinalDecisionEvidence);
    Eq("ALLOW_WITH_AUDIT", ev.SodResult);
    Eq("FROZEN_GOVERNANCE_ALLOW_WITH_AUDIT", ev.SodExceptionPolicy!);
}

async Task ExactReplay()
{
    var r = CommitteeRuntime();
    var command = Cmd("APPROVE", key: "K-REPLAY");
    var first = await r.Service.DecideAsync(command, FinalActor());
    var replay = await r.Service.DecideAsync(command, FinalActor());
    Eq(200, first.HttpStatus); Eq(200, replay.HttpStatus);
    True(replay.IdempotentReplay); False(replay.StateMutated);
    Eq(1, r.Store.FinalDecisionEvidence.Count);
    Eq(1, r.Store.FinalDecisionAuditLog.Count);
    Eq(1, r.Store.FinalDecisionOutbox.Count);
    Eq(1, r.Store.FinalDecisionIdempotencyRecords.Count);
}

async Task ChangedReplayConflict()
{
    var r = CommitteeRuntime();
    var command = Cmd("APPROVE", key: "K-CONFLICT");
    await r.Service.DecideAsync(command, FinalActor());
    var changed = command with { DecisionComment = "A different final decision comment" };
    var result = await r.Service.DecideAsync(changed, FinalActor());
    Eq(409, result.HttpStatus); Eq("P1_IDEMPOTENCY_CONFLICT", result.Code);
    Eq(1, r.Store.FinalDecisionEvidence.Count);
}

async Task AtomicRollback()
{
    foreach (var point in Enum.GetValues<G04FinalDecisionPersistenceFaultPoint>().Where(x => x != G04FinalDecisionPersistenceFaultPoint.None))
    {
        var r = CommitteeRuntime();
        r.Store.FaultPoint = point;
        var threw = false;
        try { await r.Service.DecideAsync(Cmd("APPROVE", key: $"K-{point}"), FinalActor()); }
        catch (PersistenceAtomicityException) { threw = true; }
        True(threw);
        Pristine(r.Store);
        Eq("UNDER_REVIEW", r.Store.CurrentIdea.State);
        Eq("PENDING", r.Store.CurrentAssessment.State);
    }
}

async Task EventMinimization()
{
    var profileJson = "{\"profile\":\"SECRET-DETAIL\"}";
    var evidence = GoodEvidence(profileJson);
    const string comment = "confidential decision explanation stays in evidence";
    var r = CommitteeRuntime(evidence: evidence);
    await r.Service.DecideAsync(Cmd("APPROVE") with { DecisionComment = comment }, FinalActor());
    var stored = Single(r.Store.FinalDecisionEvidence);
    var ev = Single(r.Store.FinalDecisionOutbox);
    Eq(comment, stored.DecisionComment);
    Eq(profileJson, stored.ProfileSnapshotJson);
    False(ev.Payload!.Values.Contains(comment));
    False(ev.Payload.Values.Contains(profileJson));
    False(ev.Payload.Keys.Any(x => x.Contains("comment", StringComparison.OrdinalIgnoreCase)));
    False(ev.Payload.Keys.Any(x => x.Contains("profileSnapshotJson", StringComparison.OrdinalIgnoreCase)));
}

async Task NoPortfolioCandidateCreation()
{
    var r = CommitteeRuntime();
    await r.Service.DecideAsync(Cmd("APPROVE"), FinalActor());
    var events = r.Store.FinalDecisionOutbox.Select(x => x.EventName).ToArray();
    Seq(new[] { "IdeaApprovedForPortfolio.v1" }, events);
    False(events.Any(x => x.Contains("PortfolioIntakeCandidate", StringComparison.OrdinalIgnoreCase)));
}

Task PersistenceBoundary()
{
    var r = CommitteeRuntime();
    True(r.Store.Contract.IsLogicalContractReady);
    False(r.Store.Contract.IsPhysicalOracleReady);
    return Task.CompletedTask;
}

Task SafetyBoundaries()
{
    var safety = wave.GetProperty("safety");
    True(B(safety, "g04FinalDecisionPromotedOnBranch"));
    False(B(safety, "mergedToMain"));
    False(B(safety, "p5CommandGatewayBound"));
    False(B(safety, "portfolioEligibilityTriggered"));
    False(B(safety, "physicalOracleReadyClaimed"));
    False(B(safety, "liveWindowsDomainReadyClaimed"));
    False(B(safety, "networkPilotReadyClaimed"));
    True(B(safety, "v6360Unchanged"));
    return Task.CompletedTask;
}

static TestRuntime CommitteeRuntime(
    bool stageCompleted = true,
    bool committeeApproval = true,
    G04DecisionProfileEvidence? evidence = null,
    IG04FinalDecisionEvidenceProvider? provider = null)
{
    var route = CommitteeRoute();
    var store = new G04FinalDecisionTransactionalStore(
        Idea(), Plan(route), Assessment(route), CommitteeSnapshot(), CommitteeState(stageCompleted, committeeApproval));
    var resolvedProvider = provider ?? new StaticG04FinalDecisionEvidenceProvider(evidence ?? GoodEvidence());
    return new TestRuntime(new G04FinalDecisionService(store, resolvedProvider), store);
}

static TestRuntime IndividualRuntime(G04DecisionProfileEvidence? evidence = null)
{
    var route = IndividualRoute();
    var store = new G04FinalDecisionTransactionalStore(Idea(), Plan(route), Assessment(route));
    return new TestRuntime(new G04FinalDecisionService(store, new StaticG04FinalDecisionEvidenceProvider(evidence ?? GoodEvidence())), store);
}

static AggregateSnapshot Idea() =>
    new("IDEA-1", "Idea", "UNDER_REVIEW", 7, "P-OWNER", "IDEA_OWNER", "UNIT:RND", null, null, 10);

static G04DecisionRouteMetadata CommitteeRoute() =>
    new("G04_COMMITTEE", "COMMITTEE", "MAJORITY", "GOV-1", "1.0");

static G04DecisionRouteMetadata IndividualRoute() =>
    new("UNIT_RND_DECISION", "INDIVIDUAL", "INDIVIDUAL_GOVERNANCE_DECISION", "GOV-I", "1.0");

static EvaluationPlanEnvelope Plan(G04DecisionRouteMetadata route) =>
    new("PLAN-1", "IDEA-1", 7, 3, "READY_FOR_G04_DECISION", DateTimeOffset.UtcNow.AddMinutes(-20), "CORR-P", DateTimeOffset.UtcNow.AddMinutes(-5),
        route.DecisionRoute, route.DecisionRouteKind, route.DecisionMethod, route.GovernanceProfileId, route.GovernanceProfileVersion);

static G04AssessmentEnvelope Assessment(G04DecisionRouteMetadata route) =>
    new("G04-1", "PLAN-1", "IDEA-1", 7, 3, "PENDING", "READY-SHA", DateTimeOffset.UtcNow.AddMinutes(-5), "CORR-A",
        route.DecisionRoute, route.DecisionRouteKind, route.DecisionMethod, route.GovernanceProfileId, route.GovernanceProfileVersion, 0);

static G04CommitteeSnapshotEnvelope CommitteeSnapshot() =>
    new("SNAP-1", "G04-1", "IDEA-1", 7, "UNIT:RND", "G04_COMMITTEE", "GOV-1", "1.0", 1, "MAJORITY",
        "P-M1", "IDEA_DECISION", "GOV-REF", new[]
        {
            new G04CommitteeMemberEnvelope("P-M1", "Member 1", new[] { "G04_COMMITTEE_MEMBER" }, new[] { "ASG-M1" })
        }, DateTimeOffset.UtcNow.AddMinutes(-4), "CORR-VOTE");

static G04CommitteeStateEnvelope CommitteeState(bool completed, bool approval) =>
    new("G04-1", "SNAP-1", 1, completed ? "COMPLETED" : "OPEN", true, approval, 1,
        approval ? 1 : 0, approval ? 0 : 1, DateTimeOffset.UtcNow.AddMinutes(-3), "CORR-VOTE",
        completed ? DateTimeOffset.UtcNow.AddMinutes(-3) : null);

static AuthorityActor FinalActor(string personId = "P-D1") =>
    new(personId, $"DOMAIN\\{personId.ToLowerInvariant()}", "WINDOWS_PRINCIPAL", "ASG-D1", new[] { "IDEA_DECISION" }, new[] { "UNIT:RND" });

static G04FinalDecisionCommand Cmd(string outcome, string key = "K-FINAL", DateTimeOffset? reviewDate = null)
{
    var reason = outcome == "APPROVE" ? "G04_READY" : outcome == "REJECT" ? "G04_TECH_FAIL" : "G04_EVIDENCE_INCOMPLETE";
    if (outcome == "HOLD" && reviewDate is null) reviewDate = DateTimeOffset.UtcNow.AddDays(5);
    return new("IDEA-1", 7, 10, "PLAN-1", 3, "G04-1", 0, outcome, reason,
        "Final decision rationale is sufficiently detailed.", reviewDate, key, $"CORR-{key}", "UNIT:RND");
}

static G04DecisionProfileEvidence GoodEvidence(string profileJson = "{\"profile\":\"G04-GENERAL-V1.0\"}") =>
    new("G04-GENERAL-V1.0", "1.0", profileJson, Sha256(profileJson), "G04-RS-1.0", "6161",
        75, 65, 90, 80, true, true, true, true,
        "ALL_REQUIRED_RULES_PASS", "IDEA_EVALUATOR=CONFIRMED;UNIT_OWNER_REVIEWER=CONFIRMED", "PASS");

static void Pristine(G04FinalDecisionTransactionalStore store)
{
    Eq(0, store.FinalDecisionEvidence.Count);
    Eq(0, store.FinalDecisionAuditLog.Count);
    Eq(0, store.FinalDecisionOutbox.Count);
    Eq(0, store.FinalDecisionIdempotencyRecords.Count);
}

sealed record TestRuntime(G04FinalDecisionService Service, G04FinalDecisionTransactionalStore Store);

sealed class NullEvidenceProvider : IG04FinalDecisionEvidenceProvider
{
    public ValueTask<G04DecisionProfileEvidence?> ResolveAsync(
        AggregateSnapshot idea, EvaluationPlanEnvelope plan, G04AssessmentEnvelope assessment,
        CancellationToken cancellationToken = default) => ValueTask.FromResult<G04DecisionProfileEvidence?>(null);
}

static string S(JsonElement e, string p) => e.GetProperty(p).GetString() ?? throw new Exception($"Missing string {p}");
static bool B(JsonElement e, string p) => e.GetProperty(p).GetBoolean();
static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
static T Single<T>(IEnumerable<T> items)
{
    var array = items.ToArray();
    if (array.Length != 1) throw new Exception($"Expected single item; count={array.Length}");
    return array[0];
}
static void Seq<T>(IEnumerable<T> expected, IEnumerable<T> actual)
{
    if (!expected.SequenceEqual(actual))
        throw new Exception($"Sequences differ: expected [{string.Join(",", expected)}], actual [{string.Join(",", actual)}]");
}
static void True(bool v) { if (!v) throw new Exception("Expected true"); }
static void False(bool v) { if (v) throw new Exception("Expected false"); }
static void Eq<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected '{expected}', actual '{actual}'");
}
