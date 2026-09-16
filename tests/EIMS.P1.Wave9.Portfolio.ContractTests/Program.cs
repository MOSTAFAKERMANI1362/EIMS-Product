using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("W9-01 approved Idea auto-creates exactly one candidate", EligibilityCreatesCandidate),
    ("W9-02 eligibility replay is idempotent", EligibilityReplayIsIdempotent),
    ("W9-03 hard readiness gaps block candidate", HardGapsBlock),
    ("W9-04 policy exclusion blocks candidate", PolicyExclusionBlocks),
    ("W9-05 eligibility requires authoritative approval event", InvalidEventBlocks),
    ("W9-06 non Portfolio Manager is denied", WrongRoleDenied),
    ("W9-07 requested scope must be server assignment scope", ScopeDenied),
    ("W9-08 assignment does not create membership", AssignmentIsNotMembership),
    ("W9-09 accepted membership is distinct state", AcceptedMembership),
    ("W9-10 rejected membership cannot generate recommendation", RejectedMembershipBlocksRecommendation),
    ("W9-11 deferred membership cannot generate recommendation", DeferredMembershipBlocksRecommendation),
    ("W9-12 recommendation requires accepted membership", RecommendationRequiresAcceptedMembership),
    ("W9-13 recommendation approval requires governance evidence", GovernanceReferenceRequired),
    ("W9-14 baseline binding requires approved baseline evidence", BaselineReferenceRequired),
    ("W9-15 handoff creates exactly one PLANNING Execution preserving thread", HandoffCreatesPlanningExecution),
    ("W9-16 exact handoff retry is replay-safe", HandoffReplaySafe),
    ("W9-17 conflicting idempotency replay is rejected", ConflictingReplayRejected),
    ("W9-18 stale expected version is rejected", VersionConflictRejected),
    ("W9-19 injected persistence fault publishes no partial mutation", FaultInjectionIsAtomic),
    ("W9-20 legacy grouped portfolio.assign-accept remains unbound", LegacyGroupedCommandUnbound),
    ("W9-21 integration payload stays identifier/status/evidence only", OutboxPayloadIsMinimal),
    ("W9-22 exact assignment role prevents role union", MultiRoleActorDenied)
};

var passed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        passed++;
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"RESULT {passed}/{tests.Count} PASS");
return passed == tests.Count ? 0 : 1;

async Task EligibilityCreatesCandidate()
{
    var (service, store) = Runtime();
    var r = await service.EvaluateEligibilityAsync(Trigger());
    True(r.Allowed); True(r.StateMutated); Eq(2, r.EmittedEvents.Count);
    var thread = await store.GetByIdeaVersionAsync("IDEA-1", 7);
    NotNull(thread); Eq("UNASSIGNED_CANDIDATE", thread!.Candidate.State); Eq(1L, thread.ThreadVersion);
    Eq(1, store.Threads.Count); Eq(1, store.PortfolioAuditLog.Count); Eq(2, store.PortfolioOutbox.Count);
}

async Task EligibilityReplayIsIdempotent()
{
    var (service, store) = Runtime();
    var first = await service.EvaluateEligibilityAsync(Trigger());
    var second = await service.EvaluateEligibilityAsync(Trigger("CORR-RETRY"));
    True(first.StateMutated); True(second.IdempotentReplay); False(second.StateMutated);
    Eq(1, store.Threads.Count); Eq(1, store.PortfolioAuditLog.Count); Eq(2, store.PortfolioOutbox.Count);
}

async Task HardGapsBlock()
{
    var (service, store) = Runtime();
    var r = await service.EvaluateEligibilityAsync(Trigger(hardGaps: 1));
    False(r.Allowed); Eq("P1_PORTFOLIO_HARD_READINESS_GAPS", r.Code); Eq(0, store.Threads.Count);
}

async Task PolicyExclusionBlocks()
{
    var (service, store) = Runtime();
    var r = await service.EvaluateEligibilityAsync(Trigger(policyExcluded: true));
    False(r.Allowed); Eq("P1_PORTFOLIO_POLICY_EXCLUDED", r.Code); Eq(0, store.Threads.Count);
}

async Task InvalidEventBlocks()
{
    var (service, store) = Runtime();
    var r = await service.EvaluateEligibilityAsync(Trigger() with { SourceEventName = "ManualButtonClicked.v1" });
    False(r.Allowed); Eq("P1_PORTFOLIO_ELIGIBILITY_EVENT_INVALID", r.Code); Eq(0, store.Threads.Count);
}

async Task WrongRoleDenied()
{
    var (service, store) = Runtime();
    var thread = await Candidate(service, store);
    var r = await service.ExecuteAsync(Assign(thread, "KEY-WRONG-ROLE"), Actor("IDEA_OWNER"));
    False(r.Allowed); Eq(403, r.HttpStatus); Eq(1L, (await Current(store, thread)).ThreadVersion);
}

async Task ScopeDenied()
{
    var (service, store) = Runtime();
    var thread = await Candidate(service, store);
    var command = Assign(thread, "KEY-SCOPE") with { RequestedScope = "UNIT:B" };
    var r = await service.ExecuteAsync(command, Actor(scopes: new[] { "UNIT:A" }));
    False(r.Allowed); Eq("P1_SCOPE_DENIED", r.Code);
}

async Task AssignmentIsNotMembership()
{
    var (service, store) = Runtime();
    var thread = await Candidate(service, store);
    var r = await service.ExecuteAsync(Assign(thread, "KEY-ASSIGN"), Actor());
    True(r.Allowed);
    var after = await Current(store, thread);
    NotNull(after.Assignment); Null(after.Membership); Eq("PENDING_ASSIGNMENT", after.Candidate.State);
}

async Task AcceptedMembership()
{
    var (service, store) = Runtime();
    var thread = await Assigned(service, store);
    var r = await service.ExecuteAsync(Membership(thread, "ACCEPTED", "KEY-ACCEPT"), Actor());
    True(r.Allowed);
    var after = await Current(store, thread);
    NotNull(after.Membership); Eq("ACCEPTED", after.Membership!.Status); Eq("DECIDED", after.Assignment!.State);
}

async Task RejectedMembershipBlocksRecommendation()
{
    var (service, store) = Runtime();
    var thread = await Assigned(service, store);
    await service.ExecuteAsync(Membership(thread, "REJECTED", "KEY-REJECT"), Actor());
    var rejected = await Current(store, thread);
    var r = await service.ExecuteAsync(GenerateRecommendation(rejected, "KEY-GEN-R"), Actor());
    False(r.Allowed); Eq("P1_PORTFOLIO_ACCEPTED_MEMBERSHIP_REQUIRED", r.Code);
}

async Task DeferredMembershipBlocksRecommendation()
{
    var (service, store) = Runtime();
    var thread = await Assigned(service, store);
    await service.ExecuteAsync(Membership(thread, "DEFERRED", "KEY-DEFER"), Actor());
    var deferred = await Current(store, thread);
    var r = await service.ExecuteAsync(GenerateRecommendation(deferred, "KEY-GEN-D"), Actor());
    False(r.Allowed); Eq("P1_PORTFOLIO_ACCEPTED_MEMBERSHIP_REQUIRED", r.Code);
}

async Task RecommendationRequiresAcceptedMembership()
{
    var (service, store) = Runtime();
    var thread = await Assigned(service, store);
    var r = await service.ExecuteAsync(GenerateRecommendation(thread, "KEY-GEN-EARLY"), Actor());
    False(r.Allowed); Eq("P1_PORTFOLIO_ACCEPTED_MEMBERSHIP_REQUIRED", r.Code);
}

async Task GovernanceReferenceRequired()
{
    var (service, store) = Runtime();
    var thread = await Recommended(service, store);
    var command = new PortfolioCommandWave9(
        "portfolio.approve-execution-recommendation", thread.Candidate.CandidateId, thread.ThreadVersion,
        "KEY-APPROVE-NOREF", "CORR-APPROVE-NOREF", RecommendationId: thread.Recommendation!.RecommendationId);
    var r = await service.ExecuteAsync(command, Actor());
    False(r.Allowed); Eq("P1_PORTFOLIO_GOVERNANCE_DECISION_REF_REQUIRED", r.Code);
}

async Task BaselineReferenceRequired()
{
    var (service, store) = Runtime();
    var thread = await ApprovedRecommendation(service, store);
    var command = new PortfolioCommandWave9(
        "portfolio.bind-approved-baseline", thread.Candidate.CandidateId, thread.ThreadVersion,
        "KEY-BASE-NOREF", "CORR-BASE-NOREF", RecommendationId: thread.Recommendation!.RecommendationId);
    var r = await service.ExecuteAsync(command, Actor());
    False(r.Allowed); Eq("P1_PORTFOLIO_APPROVED_BASELINE_REF_REQUIRED", r.Code);
}

async Task HandoffCreatesPlanningExecution()
{
    var (service, store) = Runtime();
    var thread = await Baselined(service, store);
    var r = await service.ExecuteAsync(Handoff(thread, "KEY-HANDOFF"), Actor());
    True(r.Allowed); True(r.EmittedEvents.Contains("ExecutionHandoffRequested.v1"));
    True(r.EmittedEvents.Contains("ExecutionCreatedFromRecommendation.v1"));
    var after = await Current(store, thread);
    NotNull(after.Execution); Eq("PLANNING", after.Execution!.State);
    Eq(after.Candidate.IdeaId, after.Execution.IdeaId);
    Eq(after.Candidate.ApprovedIdeaVersion, after.Execution.ApprovedIdeaVersion);
    Eq(1, store.Threads.Count);
}

async Task HandoffReplaySafe()
{
    var (service, store) = Runtime();
    var before = await Baselined(service, store);
    var command = Handoff(before, "KEY-HANDOFF-REPLAY");
    var first = await service.ExecuteAsync(command, Actor());
    var second = await service.ExecuteAsync(command with { CorrelationId = "CORR-HANDOFF-RETRY" }, Actor());
    True(first.StateMutated); True(second.IdempotentReplay); False(second.StateMutated);
    var after = await Current(store, before);
    NotNull(after.Execution); Eq(1, store.Threads.Count);
    Eq(1, store.PortfolioOutbox.Count(x => x.EventName == "ExecutionCreatedFromRecommendation.v1"));
}

async Task ConflictingReplayRejected()
{
    var (service, store) = Runtime();
    var thread = await Candidate(service, store);
    var first = Assign(thread, "KEY-CONFLICT");
    var r1 = await service.ExecuteAsync(first, Actor());
    True(r1.Allowed);
    var changed = first with { PortfolioId = "PORT-OTHER", CorrelationId = "CORR-CONFLICT-2" };
    var r2 = await service.ExecuteAsync(changed, Actor());
    False(r2.Allowed); Eq("P1_IDEMPOTENCY_CONFLICT", r2.Code);
}

async Task VersionConflictRejected()
{
    var (service, store) = Runtime();
    var thread = await Candidate(service, store);
    var r = await service.ExecuteAsync(Assign(thread, "KEY-VERSION") with { ExpectedThreadVersion = 99 }, Actor());
    False(r.Allowed); Eq("P1_PORTFOLIO_VERSION_CONFLICT", r.Code);
}

async Task FaultInjectionIsAtomic()
{
    var (service, store) = Runtime();
    var thread = await Candidate(service, store);
    var audits = store.PortfolioAuditLog.Count;
    var outbox = store.PortfolioOutbox.Count;
    var idem = store.PortfolioIdempotencyRecords.Count;
    store.FaultPoint = PortfolioWave9PersistenceFaultPoint.AfterOutboxStaged;
    var threw = false;
    try { await service.ExecuteAsync(Assign(thread, "KEY-FAULT"), Actor()); }
    catch (PersistenceAtomicityException) { threw = true; }
    True(threw);
    var after = await Current(store, thread);
    Eq(thread.ThreadVersion, after.ThreadVersion); Null(after.Assignment);
    Eq(audits, store.PortfolioAuditLog.Count); Eq(outbox, store.PortfolioOutbox.Count); Eq(idem, store.PortfolioIdempotencyRecords.Count);
}

Task LegacyGroupedCommandUnbound()
{
    var catalog = new RecoveredApiCommandCatalogWave9();
    True(catalog.TryGet("portfolio.assign-accept", out var legacy));
    False(legacy.MutationContractRecovered);
    True(catalog.TryGet("portfolio.assign-candidate", out var split));
    True(split.MutationContractRecovered);
    return Task.CompletedTask;
}

async Task OutboxPayloadIsMinimal()
{
    var (service, store) = Runtime();
    var thread = await Baselined(service, store);
    await service.ExecuteAsync(Handoff(thread, "KEY-PAYLOAD"), Actor());
    var prohibited = new[] { "password", "token", "connection", "note", "assessment", "answer", "salary", "national" };
    foreach (var message in store.PortfolioOutbox)
    {
        if (message.Payload is null) continue;
        foreach (var pair in message.Payload)
        {
            False(prohibited.Any(x => pair.Key.Contains(x, StringComparison.OrdinalIgnoreCase)));
            False(prohibited.Any(x => pair.Value.Contains(x, StringComparison.OrdinalIgnoreCase)));
        }
    }
}

async Task MultiRoleActorDenied()
{
    var (service, store) = Runtime();
    var thread = await Candidate(service, store);
    var actor = Actor() with { Roles = new[] { "PORTFOLIO_MANAGER", "IDEA_DECISION" } };
    var r = await service.ExecuteAsync(Assign(thread, "KEY-MULTIROLE"), actor);
    False(r.Allowed); Eq("P1_PORTFOLIO_MANAGER_AUTHORITY_REQUIRED", r.Code);
}

(PortfolioServiceWave9 Service, PortfolioTransactionalStoreWave9 Store) Runtime()
{
    var store = new PortfolioTransactionalStoreWave9();
    return (new PortfolioServiceWave9(store), store);
}

PortfolioEligibilityTrigger Trigger(string correlation = "CORR-ELIG", int hardGaps = 0, bool policyExcluded = false) =>
    new("IdeaApprovedForPortfolio.v1", "IDEA-1", 7, "APPROVED", hardGaps, policyExcluded, correlation);

AuthorityActor Actor(string role = "PORTFOLIO_MANAGER", string[]? scopes = null) =>
    new("PERSON-PM", "EXAMPLE\\pm", "WINDOWS", "AUTH-ASG-PM", new[] { role }, scopes ?? new[] { "UNIT:A", "ENTERPRISE" });

async Task<PortfolioThreadEnvelope> Candidate(PortfolioServiceWave9 service, PortfolioTransactionalStoreWave9 store)
{
    var r = await service.EvaluateEligibilityAsync(Trigger());
    True(r.Allowed);
    return (await store.GetByIdeaVersionAsync("IDEA-1", 7))!;
}

async Task<PortfolioThreadEnvelope> Assigned(PortfolioServiceWave9 service, PortfolioTransactionalStoreWave9 store)
{
    var thread = await Candidate(service, store);
    var r = await service.ExecuteAsync(Assign(thread, "KEY-ASSIGN-FLOW"), Actor());
    True(r.Allowed);
    return await Current(store, thread);
}

async Task<PortfolioThreadEnvelope> Accepted(PortfolioServiceWave9 service, PortfolioTransactionalStoreWave9 store)
{
    var thread = await Assigned(service, store);
    var r = await service.ExecuteAsync(Membership(thread, "ACCEPTED", "KEY-MEMBER-FLOW"), Actor());
    True(r.Allowed);
    return await Current(store, thread);
}

async Task<PortfolioThreadEnvelope> Recommended(PortfolioServiceWave9 service, PortfolioTransactionalStoreWave9 store)
{
    var thread = await Accepted(service, store);
    var r = await service.ExecuteAsync(GenerateRecommendation(thread, "KEY-GEN-FLOW"), Actor());
    True(r.Allowed);
    return await Current(store, thread);
}

async Task<PortfolioThreadEnvelope> ApprovedRecommendation(PortfolioServiceWave9 service, PortfolioTransactionalStoreWave9 store)
{
    var thread = await Recommended(service, store);
    var command = new PortfolioCommandWave9(
        "portfolio.approve-execution-recommendation", thread.Candidate.CandidateId, thread.ThreadVersion,
        "KEY-APPROVE-FLOW", "CORR-APPROVE-FLOW", RecommendationId: thread.Recommendation!.RecommendationId,
        GovernanceDecisionRef: "GOV-DEC-001");
    var r = await service.ExecuteAsync(command, Actor());
    True(r.Allowed);
    return await Current(store, thread);
}

async Task<PortfolioThreadEnvelope> Baselined(PortfolioServiceWave9 service, PortfolioTransactionalStoreWave9 store)
{
    var thread = await ApprovedRecommendation(service, store);
    var command = new PortfolioCommandWave9(
        "portfolio.bind-approved-baseline", thread.Candidate.CandidateId, thread.ThreadVersion,
        "KEY-BASE-FLOW", "CORR-BASE-FLOW", RecommendationId: thread.Recommendation!.RecommendationId,
        ApprovedBaselineRef: "BASELINE-APPROVED-001");
    var r = await service.ExecuteAsync(command, Actor());
    True(r.Allowed);
    return await Current(store, thread);
}

PortfolioCommandWave9 Assign(PortfolioThreadEnvelope thread, string key) =>
    new("portfolio.assign-candidate", thread.Candidate.CandidateId, thread.ThreadVersion, key,
        "CORR-" + key, PortfolioId: "PORT-RND", RequestedScope: "UNIT:A");

PortfolioCommandWave9 Membership(PortfolioThreadEnvelope thread, string decision, string key) =>
    new("portfolio.membership-decision", thread.Candidate.CandidateId, thread.ThreadVersion, key,
        "CORR-" + key, MembershipDecision: decision, RequestedScope: "UNIT:A");

PortfolioCommandWave9 GenerateRecommendation(PortfolioThreadEnvelope thread, string key) =>
    new("portfolio.generate-execution-recommendation", thread.Candidate.CandidateId, thread.ThreadVersion, key,
        "CORR-" + key, RequestedScope: "UNIT:A");

PortfolioCommandWave9 Handoff(PortfolioThreadEnvelope thread, string key) =>
    new("portfolio.request-execution-handoff", thread.Candidate.CandidateId, thread.ThreadVersion, key,
        "CORR-" + key, RecommendationId: thread.Recommendation!.RecommendationId, RequestedScope: "UNIT:A");

async Task<PortfolioThreadEnvelope> Current(PortfolioTransactionalStoreWave9 store, PortfolioThreadEnvelope any) =>
    (await store.GetByCandidateAsync(any.Candidate.CandidateId))!;

void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
void Null(object? value) { if (value is not null) throw new InvalidOperationException("Expected null."); }
void NotNull(object? value) { if (value is null) throw new InvalidOperationException("Expected non-null."); }
void Eq<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
}
