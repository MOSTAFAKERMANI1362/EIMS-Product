using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;

internal static class Program
{
    private static JsonElement _acr;
    private static JsonElement _wave;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || args.Any(x => !File.Exists(x)))
        {
            Console.Error.WriteLine("Usage: EIMS.P1.Wave8.G04FinalDecision.ContractTests <ACR-P0-007-json> <P1-wave8-json>");
            return 2;
        }

        using var acrDoc = JsonDocument.Parse(File.ReadAllText(args[0]));
        using var waveDoc = JsonDocument.Parse(File.ReadAllText(args[1]));
        _acr = acrDoc.RootElement.Clone();
        _wave = waveDoc.RootElement.Clone();

        var tests = new List<(string Name, Func<Task> Run)>
        {
            ("W8F-01 ACR and Wave8 identities are exact", ArtifactIdentity),
            ("W8F-02 cumulative catalog promotes final G04 decision", CatalogPromotion),
            ("W8F-03 APPROVE commits without changing business revision", ApproveSuccess),
            ("W8F-04 RETURN increments business revision and supersedes Plan", ReturnSuccess),
            ("W8F-05 HOLD freezes review date", HoldSuccess),
            ("W8F-06 REJECT closes assessment without business revision change", RejectSuccess),
            ("W8F-07 final input validation is fail-closed", InputValidation),
            ("W8F-08 exact role and scope are enforced", AuthorityValidation),
            ("W8F-09 stale business and technical versions are denied", VersionValidation),
            ("W8F-10 Plan and Assessment route mismatch is denied", RouteMismatchDenied),
            ("W8F-11 committee stage must complete for every final outcome", CommitteeStageRequired),
            ("W8F-12 committee APPROVE requires positive committee result", CommitteePositiveRequired),
            ("W8F-13 negative outcome may follow completed non-approval", NegativeAfterNonApprovalAllowed),
            ("W8F-14 committee member cannot be final authority", CommitteeMemberSodDenied),
            ("W8F-15 individual route finalizes without committee", IndividualRouteSuccess),
            ("W8F-16 individual route rejects committee context", IndividualCommitteeContextDenied),
            ("W8F-17 approval gates block APPROVE only", ApprovalGatesArePositiveOnly),
            ("W8F-18 frozen evidence integrity is mandatory", EvidenceIntegrityDenied),
            ("W8F-19 evaluator conflict policy is enforced", EvaluatorConflictPolicy),
            ("W8F-20 exact replay is idempotent", ExactReplay),
            ("W8F-21 changed replay conflicts", ChangedReplayConflict),
            ("W8F-22 all persistence fault points roll back atomically", AtomicRollback),
            ("W8F-23 outbox is minimized and evidence-linked", EventMinimization),
            ("W8F-24 no PortfolioIntakeCandidate is created in G04 transaction", NoPortfolioCandidateCreation),
            ("W8F-25 reference persistence remains Oracle-unbound", PersistenceBoundary),
            ("W8F-26 Wave8 safety claims remain bounded", SafetyBoundaries)
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
    }

    private static Task ArtifactIdentity()
    {
        Eq("ACR-P0-007", S(_acr, "acrId"));
        Eq("APPROVED_FOR_P1_IMPLEMENTATION", S(_acr, "status"));
        Eq("EIMS-P1-RECOVERY-WAVE8-G04-FINAL-DECISION-REBASELINE-1.0", S(_wave, "schema"));
        Eq("IMPLEMENTED_PENDING_CONTRACT_AND_REGRESSION_GATES", S(_wave, "status"));
        return Task.CompletedTask;
    }

    private static Task CatalogPromotion()
    {
        var catalog = new RecoveredApiCommandCatalogWave8();
        Eq(6, catalog.All.Count(x => x.MutationContractRecovered));
        Eq(6, RecoveredApiCommandCatalogWave8.Wave8RecoveredMutationCommandCount);
        True(catalog.TryGet("g04.final-decision", out var policy));
        True(policy.MutationContractRecovered);
        Eq("IDEA_DECISION", Single(policy.RequiredRoles));
        Eq("IdeaApprovedForPortfolio.v1", policy.ResolveEventName("APPROVE")!);
        Eq("IdeaReturnedFromG04.v1", policy.ResolveEventName("RETURN")!);
        Eq("IdeaHeldAtG04.v1", policy.ResolveEventName("HOLD")!);
        Eq("IdeaRejectedAtG04.v1", policy.ResolveEventName("REJECT")!);
        return Task.CompletedTask;
    }

    private static async Task ApproveSuccess()
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

    private static async Task ReturnSuccess()
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

    private static async Task HoldSuccess()
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

    private static async Task RejectSuccess()
    {
        var r = CommitteeRuntime(committeeApproval: false);
        var result = await r.Service.DecideAsync(Cmd("REJECT"), FinalActor());
        Eq(200, result.HttpStatus);
        Eq("REJECTED", r.Store.CurrentIdea.State);
        Eq(7L, r.Store.CurrentIdea.Version);
        Eq("DECIDED", r.Store.CurrentAssessment.State);
        Eq("IdeaRejectedAtG04.v1", Single(r.Store.FinalDecisionOutbox).EventName);
    }

    private static async Task InputValidation()
    {
        var r1 = CommitteeRuntime();
        var shortComment = await r1.Service.DecideAsync(Cmd("APPROVE") with { DecisionComment = "too short" }, FinalActor());
        Eq("P1_G04_FINAL_COMMENT_REQUIRED", shortComment.Code); Pristine(r1.Store);

        var r2 = CommitteeRuntime();
        var approveReason = await r2.Service.DecideAsync(Cmd("APPROVE") with { ReasonCode = "OTHER" }, FinalActor());
        Eq("P1_G04_APPROVE_REASON_MUST_BE_READY", approveReason.Code); Pristine(r2.Store);

        var r3 = CommitteeRuntime(committeeApproval: false);
        var negativeReady = await r3.Service.DecideAsync(Cmd("RETURN") with { ReasonCode = "G04_READY" }, FinalActor());
        Eq("P1_G04_NEGATIVE_REASON_CANNOT_BE_READY", negativeReady.Code); Pristine(r3.Store);

        var r4 = CommitteeRuntime(committeeApproval: false);
        var holdNoDate = await r4.Service.DecideAsync(Cmd("HOLD") with { ReviewDate = null }, FinalActor());
        Eq("P1_G04_HOLD_REVIEW_DATE_REQUIRED", holdNoDate.Code); Pristine(r4.Store);
    }

    private static async Task AuthorityValidation()
    {
        var r1 = CommitteeRuntime();
        var wrongRole = await r1.Service.DecideAsync(Cmd("APPROVE"), FinalActor() with { Roles = new[] { "G04_COMMITTEE_MEMBER" } });
        Eq(403, wrongRole.HttpStatus); Eq("P1_G04_FINAL_AUTHORITY_REQUIRED", wrongRole.Code); Pristine(r1.Store);

        var r2 = CommitteeRuntime();
        var wrongScope = await r2.Service.DecideAsync(Cmd("APPROVE"), FinalActor() with { Scopes = new[] { "UNIT:FIN" } });
        Eq(403, wrongScope.HttpStatus); Eq("P1_G04_FINAL_SCOPE_DENIED", wrongScope.Code); Pristine(r2.Store);
    }

    private static async Task VersionValidation()
    {
        var r1 = CommitteeRuntime();
        var staleBusiness = await r1.Service.DecideAsync(Cmd("APPROVE") with { ExpectedIdeaRevision = 6 }, FinalActor());
        Eq("P1_G04_IDEA_REVISION_CONFLICT", staleBusiness.Code); Pristine(r1.Store);

        var r2 = CommitteeRuntime();
        var staleTechnical = await r2.Service.DecideAsync(Cmd("APPROVE") with { ExpectedStateMutationVersion = 9 }, FinalActor());
        Eq("P1_G04_STATE_MUTATION_VERSION_CONFLICT", staleTechnical.Code); Pristine(r2.Store);
    }

    private static async Task RouteMismatchDenied()
    {
        var idea = Idea();
        var plan = Plan(CommitteeRoute());
        var assessment = Assessment(new G04DecisionRouteMetadata("G04_COMMITTEE", "COMMITTEE", "CONSENSUS", "GOV-1", "1.0"));
        var store = new G04FinalDecisionTransactionalStore(idea, plan, assessment, CommitteeSnapshot(), CommitteeState(true, true));
        var service = new G04FinalDecisionService(store, new StaticG04FinalDecisionEvidenceProvider(GoodEvidence()));
        var result = await service.DecideAsync(Cmd("APPROVE"), FinalActor());
        Eq(409, result.HttpStatus); Eq("P1_G04_FINAL_ROUTE_MISMATCH", result.Code); Pristine(store);
    }

    private static async Task CommitteeStageRequired()
    {
        var r = CommitteeRuntime(stageCompleted: false, committeeApproval: false);
        var result = await r.Service.DecideAsync(Cmd("RETURN"), FinalActor());
        Eq(409, result.HttpStatus); Eq("P1_G04_COMMITTEE_STAGE_NOT_COMPLETED", result.Code); Pristine(r.Store);
    }

    private static async Task CommitteePositiveRequired()
    {
        var r = CommitteeRuntime(committeeApproval: false);
        var result = await r.Service.DecideAsync(Cmd("APPROVE"), FinalActor());
        Eq(422, result.HttpStatus); Eq("P1_G04_COMMITTEE_APPROVAL_NOT_SATISFIED", result.Code); Pristine(r.Store);
    }

    private static async Task NegativeAfterNonApprovalAllowed()
    {
        var r = CommitteeRuntime(committeeApproval: false);
        var result = await r.Service.DecideAsync(Cmd("REJECT"), FinalActor());
        Eq(200, result.HttpStatus); Eq("REJECTED", r.Store.CurrentIdea.State);
    }

    private static async Task CommitteeMemberSodDenied()
    {
        var r = CommitteeRuntime();
        var result = await r.Service.DecideAsync(Cmd("APPROVE"), FinalActor("P-M1"));
        Eq(403, result.HttpStatus); Eq("SOD_G04_MEMBER_NOT_FINAL_AUTHORITY", result.Code); Pristine(r.Store);
    }

    private static async Task IndividualRouteSuccess()
    {
        var r = IndividualRuntime();
        var result = await r.Service.DecideAsync(Cmd("APPROVE"), FinalActor());
        Eq(200, result.HttpStatus); Eq("APPROVED", r.Store.CurrentIdea.State);
    }

    private static async Task IndividualCommitteeContextDenied()
    {
        var route = IndividualRoute();
        var store = new G04FinalDecisionTransactionalStore(Idea(), Plan(route), Assessment(route), CommitteeSnapshot(), CommitteeState(true, true));
        var service = new G04FinalDecisionService(store, new StaticG04FinalDecisionEvidenceProvider(GoodEvidence()));
        var result = await service.DecideAsync(Cmd("APPROVE"), FinalActor());
        Eq(409, result.HttpStatus); Eq("P1_G04_INDIVIDUAL_ROUTE_HAS_COMMITTEE_CONTEXT", result.Code); Pristine(store);
    }

    private static async Task ApprovalGatesArePositiveOnly()
    {
        var lowScore = GoodEvidence() with { GateScore = 50, PassThreshold = 65 };
        var approve = CommitteeRuntime(evidence: lowScore);
        var denied = await approve.Service.DecideAsync(Cmd("APPROVE"), FinalActor());
        Eq(422, denied.HttpStatus); Eq("P1_G04_GATE_SCORE_BELOW_THRESHOLD", denied.Code); Pristine(approve.Store);

        var reject = CommitteeRuntime(committeeApproval: false, evidence: lowScore with { SpecialistOutcomesNonBlocking = false });
        var accepted = await reject.Service.DecideAsync(Cmd("REJECT"), FinalActor());
        Eq(200, accepted.HttpStatus);
    }

    private static async Task EvidenceIntegrityDenied()
    {
        var missing = CommitteeRuntime(provider: new NullEvidenceProvider());
        var result1 = await missing.Service.DecideAsync(Cmd("APPROVE"), FinalActor());
        Eq(503, result1.HttpStatus); Eq("P1_G04_DECISION_EVIDENCE_NOT_BOUND", result1.Code); Pristine(missing.Store);

        var corrupt = CommitteeRuntime(evidence: GoodEvidence() with { ProfileSnapshotSha256 = "bad" });
        var result2 = await corrupt.Service.DecideAsync(Cmd("APPROVE"), FinalActor());
        Eq(503, result2.HttpStatus); Eq("P1_G04_DECISION_EVIDENCE_NOT_BOUND", result2.Code); Pristine(corrupt.Store);
    }

    private static async Task EvaluatorConflictPolicy()
    {
        var denied = CommitteeRuntime(evidence: GoodEvidence() with { EvaluatorConflictPolicy = "DENY" });
        var d = await denied.Service.DecideAsync(Cmd("APPROVE"), FinalActor());
        Eq(403, d.HttpStatus); Eq("P1_G04_EVALUATOR_FINAL_DECIDER_CONFLICT", d.Code); Pristine(denied.Store);

        var allowed = CommitteeRuntime(evidence: GoodEvidence() with { EvaluatorConflictPolicy = "ALLOW_WITH_AUDIT" });
        var a = await allowed.Service.DecideAsync(Cmd("APPROVE"), FinalActor());
        Eq(200, a.HttpStatus);
        var evidence = Single(allowed.Store.FinalDecisionEvidence);
        Eq("ALLOW_WITH_AUDIT", evidence.SodResult);
        Eq("FROZEN_GOVERNANCE_ALLOW_WITH_AUDIT", evidence.SodExceptionPolicy!);
    }

    private static async Task ExactReplay()
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

    private static async Task ChangedReplayConflict()
    {
        var r = CommitteeRuntime();
        var command = Cmd("APPROVE", key: "K-CONFLICT");
        await r.Service.DecideAsync(command, FinalActor());
        var result = await r.Service.DecideAsync(command with { DecisionComment = "A different final decision comment" }, FinalActor());
        Eq(409, result.HttpStatus); Eq("P1_IDEMPOTENCY_CONFLICT", result.Code);
        Eq(1, r.Store.FinalDecisionEvidence.Count);
    }

    private static async Task AtomicRollback()
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

    private static async Task EventMinimization()
    {
        var profileJson = "{\"profile\":\"SECRET-DETAIL\"}";
        const string comment = "confidential decision explanation stays in evidence";
        var r = CommitteeRuntime(evidence: GoodEvidence(profileJson));
        await r.Service.DecideAsync(Cmd("APPROVE") with { DecisionComment = comment }, FinalActor());
        var evidence = Single(r.Store.FinalDecisionEvidence);
        var ev = Single(r.Store.FinalDecisionOutbox);
        Eq(comment, evidence.DecisionComment);
        Eq(profileJson, evidence.ProfileSnapshotJson);
        False(ev.Payload!.Values.Contains(comment));
        False(ev.Payload.Values.Contains(profileJson));
        False(ev.Payload.Keys.Any(x => x.Contains("comment", StringComparison.OrdinalIgnoreCase)));
        False(ev.Payload.Keys.Any(x => x.Contains("profileSnapshotJson", StringComparison.OrdinalIgnoreCase)));
        Eq(evidence.DecisionId, ev.Payload["decisionEvidenceId"]);
    }

    private static async Task NoPortfolioCandidateCreation()
    {
        var r = CommitteeRuntime();
        await r.Service.DecideAsync(Cmd("APPROVE"), FinalActor());
        var events = r.Store.FinalDecisionOutbox.Select(x => x.EventName).ToArray();
        Seq(new[] { "IdeaApprovedForPortfolio.v1" }, events);
        False(events.Any(x => x.Contains("PortfolioIntakeCandidate", StringComparison.OrdinalIgnoreCase)));
    }

    private static Task PersistenceBoundary()
    {
        var r = CommitteeRuntime();
        True(r.Store.Contract.IsLogicalContractReady);
        False(r.Store.Contract.IsPhysicalOracleReady);
        return Task.CompletedTask;
    }

    private static Task SafetyBoundaries()
    {
        var safety = _wave.GetProperty("safety");
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

    private static TestRuntime CommitteeRuntime(
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

    private static TestRuntime IndividualRuntime(G04DecisionProfileEvidence? evidence = null)
    {
        var route = IndividualRoute();
        var store = new G04FinalDecisionTransactionalStore(Idea(), Plan(route), Assessment(route));
        return new TestRuntime(new G04FinalDecisionService(store, new StaticG04FinalDecisionEvidenceProvider(evidence ?? GoodEvidence())), store);
    }

    private static AggregateSnapshot Idea() =>
        new("IDEA-1", "Idea", "UNDER_REVIEW", 7, "P-OWNER", "IDEA_OWNER", "UNIT:RND", null, null, 10);

    private static G04DecisionRouteMetadata CommitteeRoute() =>
        new("G04_COMMITTEE", "COMMITTEE", "MAJORITY", "GOV-1", "1.0");

    private static G04DecisionRouteMetadata IndividualRoute() =>
        new("UNIT_RND_DECISION", "INDIVIDUAL", "INDIVIDUAL_GOVERNANCE_DECISION", "GOV-I", "1.0");

    private static EvaluationPlanEnvelope Plan(G04DecisionRouteMetadata route) =>
        new("PLAN-1", "IDEA-1", 7, 3, "READY_FOR_G04_DECISION", DateTimeOffset.UtcNow.AddMinutes(-20), "CORR-P", DateTimeOffset.UtcNow.AddMinutes(-5),
            route.DecisionRoute, route.DecisionRouteKind, route.DecisionMethod, route.GovernanceProfileId, route.GovernanceProfileVersion);

    private static G04AssessmentEnvelope Assessment(G04DecisionRouteMetadata route) =>
        new("G04-1", "PLAN-1", "IDEA-1", 7, 3, "PENDING", "READY-SHA", DateTimeOffset.UtcNow.AddMinutes(-5), "CORR-A",
            route.DecisionRoute, route.DecisionRouteKind, route.DecisionMethod, route.GovernanceProfileId, route.GovernanceProfileVersion, 0);

    private static G04CommitteeSnapshotEnvelope CommitteeSnapshot() =>
        new("SNAP-1", "G04-1", "IDEA-1", 7, "UNIT:RND", "G04_COMMITTEE", "GOV-1", "1.0", 1, "MAJORITY",
            "P-M1", "IDEA_DECISION", "GOV-REF", new[]
            {
                new G04CommitteeMemberEnvelope("P-M1", "Member 1", new[] { "G04_COMMITTEE_MEMBER" }, new[] { "ASG-M1" })
            }, DateTimeOffset.UtcNow.AddMinutes(-4), "CORR-VOTE");

    private static G04CommitteeStateEnvelope CommitteeState(bool completed, bool approval) =>
        new("G04-1", "SNAP-1", 1, completed ? "COMPLETED" : "OPEN", true, approval, 1,
            approval ? 1 : 0, approval ? 0 : 1, DateTimeOffset.UtcNow.AddMinutes(-3), "CORR-VOTE",
            completed ? DateTimeOffset.UtcNow.AddMinutes(-3) : null);

    private static AuthorityActor FinalActor(string personId = "P-D1") =>
        new(personId, $"DOMAIN\\{personId.ToLowerInvariant()}", "WINDOWS_PRINCIPAL", "ASG-D1", new[] { "IDEA_DECISION" }, new[] { "UNIT:RND" });

    private static G04FinalDecisionCommand Cmd(string outcome, string key = "K-FINAL", DateTimeOffset? reviewDate = null)
    {
        var reason = outcome == "APPROVE" ? "G04_READY" : outcome == "REJECT" ? "G04_TECH_FAIL" : "G04_EVIDENCE_INCOMPLETE";
        if (outcome == "HOLD" && reviewDate is null) reviewDate = DateTimeOffset.UtcNow.AddDays(5);
        return new("IDEA-1", 7, 10, "PLAN-1", 3, "G04-1", 0, outcome, reason,
            "Final decision rationale is sufficiently detailed.", reviewDate, key, $"CORR-{key}", "UNIT:RND");
    }

    private static G04DecisionProfileEvidence GoodEvidence(string profileJson = "{\"profile\":\"G04-GENERAL-V1.0\"}") =>
        new("G04-GENERAL-V1.0", "1.0", profileJson, Sha256(profileJson), "G04-RS-1.0", "6161",
            75, 65, 90, 80, true, true, true, true,
            "ALL_REQUIRED_RULES_PASS", "IDEA_EVALUATOR=CONFIRMED;UNIT_OWNER_REVIEWER=CONFIRMED", "PASS");

    private static void Pristine(G04FinalDecisionTransactionalStore store)
    {
        Eq(0, store.FinalDecisionEvidence.Count);
        Eq(0, store.FinalDecisionAuditLog.Count);
        Eq(0, store.FinalDecisionOutbox.Count);
        Eq(0, store.FinalDecisionIdempotencyRecords.Count);
    }

    private static string S(JsonElement e, string p) => e.GetProperty(p).GetString() ?? throw new Exception($"Missing string {p}");
    private static bool B(JsonElement e, string p) => e.GetProperty(p).GetBoolean();
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static T Single<T>(IEnumerable<T> items)
    {
        var array = items.ToArray();
        if (array.Length != 1) throw new Exception($"Expected single item; count={array.Length}");
        return array[0];
    }
    private static void Seq<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new Exception($"Sequences differ: expected [{string.Join(",", expected)}], actual [{string.Join(",", actual)}]");
    }
    private static void True(bool value) { if (!value) throw new Exception("Expected true"); }
    private static void False(bool value) { if (value) throw new Exception("Expected false"); }
    private static void Eq<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected '{expected}', actual '{actual}'");
    }

    private sealed record TestRuntime(G04FinalDecisionService Service, G04FinalDecisionTransactionalStore Store);

    private sealed class NullEvidenceProvider : IG04FinalDecisionEvidenceProvider
    {
        public ValueTask<G04DecisionProfileEvidence?> ResolveAsync(
            AggregateSnapshot idea,
            EvaluationPlanEnvelope plan,
            G04AssessmentEnvelope assessment,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<G04DecisionProfileEvidence?>(null);
    }
}
