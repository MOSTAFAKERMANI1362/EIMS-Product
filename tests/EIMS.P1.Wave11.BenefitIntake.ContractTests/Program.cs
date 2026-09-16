using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;

var tests = new List<(string Name, Action Run)>
{
    ("W11-I01 authoritative event materializes Benefit from server source provider", MaterializesFromProvider),
    ("W11-I02 exact replay resolves before source-provider reload", ReplayBeforeProviderReload),
    ("W11-I03 missing authoritative source evidence fails closed", SourceEvidenceRequired),
    ("W11-I04 event payload cannot contradict server source evidence", EventPayloadMismatch),
    ("W11-I05 changed event with same intake identity conflicts", ChangedReplayConflicts)
};

var passed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
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

static void MaterializesFromProvider()
{
    var store = new BenefitTransactionalStoreWave11();
    var handler = new BenefitObligationEventHandlerWave11(
        store,
        new StaticBenefitObligationSourceProviderWave11(Source()));

    var result = handler.HandleAsync(SourceEvent()).AsTask().GetAwaiter().GetResult();

    Assert(result.Allowed && result.StateMutated && result.NewVersion == 1, "materialize result");
    var benefit = store.Benefits.Single();
    Assert(benefit.BenefitId == "BEN-1" && benefit.ExecutionId == "EX-1" && benefit.IdeaId == "IDEA-1", "thread facts");
    Assert(benefit.State == "OBLIGATION_PENDING_ACCEPTANCE", "state");
    Assert(store.BenefitAuditLog.Count == 1 && store.BenefitOutbox.Count == 0 && store.BenefitIdempotencyRecords.Count == 1, "atomic evidence");
}

static void ReplayBeforeProviderReload()
{
    var store = new BenefitTransactionalStoreWave11();
    var sourceEvent = SourceEvent();
    var first = new BenefitObligationEventHandlerWave11(
        store,
        new StaticBenefitObligationSourceProviderWave11(Source()));
    Assert(first.HandleAsync(sourceEvent).AsTask().GetAwaiter().GetResult().Allowed, "first");

    var replayHandler = new BenefitObligationEventHandlerWave11(
        store,
        new StaticBenefitObligationSourceProviderWave11());
    var replay = replayHandler.HandleAsync(sourceEvent).AsTask().GetAwaiter().GetResult();

    Assert(replay.Allowed && replay.IdempotentReplay && !replay.StateMutated, "replay before source reload");
    Assert(store.Benefits.Count == 1 && store.BenefitAuditLog.Count == 1 && store.BenefitIdempotencyRecords.Count == 1, "no duplicate persistence");
}

static void SourceEvidenceRequired()
{
    var store = new BenefitTransactionalStoreWave11();
    var handler = new BenefitObligationEventHandlerWave11(
        store,
        new StaticBenefitObligationSourceProviderWave11());

    var result = handler.HandleAsync(SourceEvent()).AsTask().GetAwaiter().GetResult();

    Assert(!result.Allowed && result.Code == "P1_BENEFIT_INTAKE_SOURCE_EVIDENCE_REQUIRED", "source required");
    Assert(store.Benefits.Count == 0 && store.BenefitAuditLog.Count == 0 && store.BenefitIdempotencyRecords.Count == 0, "no mutation");
}

static void EventPayloadMismatch()
{
    var store = new BenefitTransactionalStoreWave11();
    var handler = new BenefitObligationEventHandlerWave11(
        store,
        new StaticBenefitObligationSourceProviderWave11(Source()));
    var bad = SourceEvent(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["benefitId"] = "BEN-1",
        ["executionId"] = "EX-OTHER",
        ["ideaId"] = "IDEA-1",
        ["approvedIdeaVersion"] = "7",
        ["state"] = "OBLIGATION_PENDING_ACCEPTANCE"
    });

    var result = handler.HandleAsync(bad).AsTask().GetAwaiter().GetResult();

    Assert(!result.Allowed && result.Code == "P1_BENEFIT_INTAKE_EVENT_SOURCE_MISMATCH", "payload mismatch");
    Assert(store.Benefits.Count == 0, "no materialization");
}

static void ChangedReplayConflicts()
{
    var store = new BenefitTransactionalStoreWave11();
    var handler = new BenefitObligationEventHandlerWave11(
        store,
        new StaticBenefitObligationSourceProviderWave11(Source()));
    var sourceEvent = SourceEvent();
    Assert(handler.HandleAsync(sourceEvent).AsTask().GetAwaiter().GetResult().Allowed, "first");

    var changed = sourceEvent with
    {
        Payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["benefitId"] = "BEN-1",
            ["executionId"] = "EX-OTHER",
            ["ideaId"] = "IDEA-1",
            ["approvedIdeaVersion"] = "7",
            ["state"] = "OBLIGATION_PENDING_ACCEPTANCE"
        }
    };
    var replay = handler.HandleAsync(changed).AsTask().GetAwaiter().GetResult();

    Assert(!replay.Allowed && replay.Code == "P1_IDEMPOTENCY_CONFLICT", "changed replay conflict");
    Assert(store.Benefits.Count == 1 && store.BenefitAuditLog.Count == 1, "original commit preserved");
}

static BenefitObligationSourceEvidenceWave11 Source() =>
    new(
        "BenefitObligationCreated.v1",
        "BEN-1",
        "EX-1",
        "IDEA-1",
        7,
        "OBLIGATION_PENDING_ACCEPTANCE",
        1,
        "CORR-BEN-1",
        DateTimeOffset.Parse("2026-09-16T00:00:00Z"),
        "W10-BENEFIT-HANDOFF-EVIDENCE",
        "1");

static OutboxEnvelope SourceEvent(IReadOnlyDictionary<string, string>? payload = null) =>
    new(
        "MSG-BEN-1",
        "BenefitObligationCreated.v1",
        "BEN-1",
        1,
        "CORR-BEN-1",
        DateTimeOffset.Parse("2026-09-16T00:00:00Z"),
        payload ?? new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["benefitId"] = "BEN-1",
            ["executionId"] = "EX-1",
            ["ideaId"] = "IDEA-1",
            ["approvedIdeaVersion"] = "7",
            ["state"] = "OBLIGATION_PENDING_ACCEPTANCE"
        });

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
