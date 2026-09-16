using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;

var tests = new List<(string Name, Action Run)>
{
    ("W11-01 catalog adds eight Benefit mutations", Catalog),
    ("W11-02 authoritative intake materializes pending obligation", IntakeMaterializes),
    ("W11-03 intake exact replay is idempotent", IntakeReplay),
    ("W11-04 intake conflicting thread fails closed", IntakeConflict),
    ("W11-05 accept requires exact Benefit owner identity", AcceptOwnerContext),
    ("W11-06 accept advances to baseline required", AcceptHappy),
    ("W11-07 baseline requires both evidence references", BaselineRefsRequired),
    ("W11-08 baseline and target cannot be same reference", BaselineDistinct),
    ("W11-09 baseline requires server validation evidence", BaselineEvidenceRequired),
    ("W11-10 non-financial Benefit baseline is first-class", NonFinancialBaseline),
    ("W11-11 measurement plan requires baseline and target", PlanRequiresBaseline),
    ("W11-12 measurement plan advances separately", PlanHappy),
    ("W11-13 Benefit owner can measure in pilot default", MeasureOwnerHappy),
    ("W11-14 data provider is denied without server policy", DataProviderDenied),
    ("W11-15 explicitly authorized data provider can measure", DataProviderAllowed),
    ("W11-16 verifier requires Execution owner evidence", VerifierEvidenceRequired),
    ("W11-17 Execution owner cannot verify own output", VerifierSod),
    ("W11-18 independent verifier advances to verified", VerifyHappy),
    ("W11-19 attribution is a distinct transition", AttributionDistinct),
    ("W11-20 realization requires dossier", RealizationDossierRequired),
    ("W11-21 realization advances to realized", RealizeHappy),
    ("W11-22 close requires published Knowledge evidence", CloseKnowledgeRequired),
    ("W11-23 non-published Knowledge cannot close", CloseKnowledgeNotPublished),
    ("W11-24 published Knowledge closes Benefit", CloseHappy),
    ("W11-25 exact retry is idempotent before mutable reads", ReplaySafety),
    ("W11-26 changed payload with same idempotency key conflicts", ReplayConflict),
    ("W11-27 stale expected version fails closed", StaleVersion),
    ("W11-28 wrong role fails closed", WrongRole),
    ("W11-29 requested scope cannot widen authority", ScopeCannotWiden),
    ("W11-30 Digital Thread is preserved across lifecycle", DigitalThread),
    ("W11-31 persistence fault rolls back state audit outbox idempotency", FaultRollback),
    ("W11-32 full Benefit lifecycle remains stepwise through closed", FullLifecycle)
};

var passed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"RESULT {passed}/{tests.Count} PASS");
return passed == tests.Count ? 0 : 1;

static void Catalog()
{
    var catalog = new RecoveredApiCommandCatalogWave11();
    Assert(catalog.All.Count == RecoveredApiCommandCatalogWave11.Wave11RecoveredMutationCommandCount, "catalog count");
    var names = new[] { "benefits.accept", "benefits.set-baseline", "benefits.approve-measurement-plan", "benefits.measure", "benefits.verify", "benefits.attribution", "benefits.realize", "benefits.close" };
    foreach (var name in names)
        Assert(catalog.TryGet(name, out var p) && p.MutationContractRecovered && p.EventContractRecovered, name);
}

static void IntakeMaterializes()
{
    var store = new BenefitTransactionalStoreWave11();
    var result = new BenefitObligationIntakeWave11(store).MaterializeAsync(Source()).AsTask().GetAwaiter().GetResult();
    Assert(result.Allowed && result.StateMutated && result.NewVersion == 1, "intake result");
    Assert(store.Benefits.Single().State == "OBLIGATION_PENDING_ACCEPTANCE", "state");
    Assert(store.BenefitAuditLog.Count == 1 && store.BenefitOutbox.Count == 0 && store.BenefitIdempotencyRecords.Count == 1, "intake evidence");
}

static void IntakeReplay()
{
    var store = new BenefitTransactionalStoreWave11();
    var intake = new BenefitObligationIntakeWave11(store);
    var source = Source();
    _ = intake.MaterializeAsync(source).AsTask().GetAwaiter().GetResult();
    var replay = intake.MaterializeAsync(source).AsTask().GetAwaiter().GetResult();
    Assert(replay.Allowed && replay.IdempotentReplay && !replay.StateMutated, "replay");
    Assert(store.Benefits.Count == 1 && store.BenefitAuditLog.Count == 1, "no duplicate mutation");
}

static void IntakeConflict()
{
    var store = new BenefitTransactionalStoreWave11();
    var intake = new BenefitObligationIntakeWave11(store);
    _ = intake.MaterializeAsync(Source()).AsTask().GetAwaiter().GetResult();
    var conflict = intake.MaterializeAsync(Source() with { ExecutionId = "EX-OTHER" }).AsTask().GetAwaiter().GetResult();
    Assert(!conflict.Allowed && conflict.Code == "P1_BENEFIT_INTAKE_CONFLICT", "conflict");
}

static void AcceptOwnerContext()
{
    var ctx = Context(NewBenefit("OBLIGATION_PENDING_ACCEPTANCE"));
    var wrong = Actor("P-OTHER", "A-BEN", "BENEFIT_OWNER");
    var result = ctx.Service.ExecuteAsync(Cmd("benefits.accept", 1), wrong).AsTask().GetAwaiter().GetResult();
    Assert(!result.Allowed && result.Code == "P1_BENEFIT_OWNER_CONTEXT_MISMATCH", "owner mismatch");
}

static void AcceptHappy()
{
    var ctx = Context(NewBenefit("OBLIGATION_PENDING_ACCEPTANCE"));
    var result = Exec(ctx, Cmd("benefits.accept", 1), Owner());
    Assert(result.Allowed && Current(ctx).State == "BASELINE_REQUIRED", "accept");
}

static void BaselineRefsRequired()
{
    var ctx = Context(NewBenefit("BASELINE_REQUIRED"));
    var result = Exec(ctx, Cmd("benefits.set-baseline", 1), Owner());
    Assert(!result.Allowed && result.Code == "P1_BENEFIT_BASELINE_TARGET_REQUIRED", "refs required");
}

static void BaselineDistinct()
{
    var ctx = Context(NewBenefit("BASELINE_REQUIRED"));
    var result = Exec(ctx, Cmd("benefits.set-baseline", 1) with { BaselineEvidenceRef = "SAME", TargetEvidenceRef = "SAME" }, Owner());
    Assert(!result.Allowed && result.Code == "P1_BENEFIT_BASELINE_TARGET_NOT_DISTINCT", "distinct");
}

static void BaselineEvidenceRequired()
{
    var ctx = Context(NewBenefit("BASELINE_REQUIRED"), baselineEvidence: Array.Empty<BenefitBaselineTargetEvidenceWave11>());
    var result = Exec(ctx, BaselineCmd(1), Owner());
    Assert(!result.Allowed && result.Code == "P1_BENEFIT_BASELINE_TARGET_EVIDENCE_INVALID", "provider required");
}

static void NonFinancialBaseline()
{
    var ctx = Context(NewBenefit("BASELINE_REQUIRED"));
    var result = Exec(ctx, BaselineCmd(1), Owner());
    Assert(result.Allowed, "baseline allowed");
    var current = Current(ctx);
    Assert(current.State == "PLAN_REQUIRED" && current.BaselineEvidenceRef == "BASE-REF" && current.TargetEvidenceRef == "TARGET-REF", "refs retained");
    Assert(ctx.Store.BenefitOutbox.Single().Payload!["benefitClass"] == "NON_FINANCIAL", "non-financial class");
}

static void PlanRequiresBaseline()
{
    var ctx = Context(NewBenefit("PLAN_REQUIRED"));
    var result = Exec(ctx, Cmd("benefits.approve-measurement-plan", 1) with { MeasurementPlanRef = "PLAN-1" }, Owner());
    Assert(!result.Allowed && result.Code == "P1_BENEFIT_PLAN_STATE_INVALID", "baseline needed");
}

static void PlanHappy()
{
    var ctx = Context(NewBenefit("PLAN_REQUIRED", baseline: "BASE-REF", target: "TARGET-REF"));
    var result = Exec(ctx, Cmd("benefits.approve-measurement-plan", 1) with { MeasurementPlanRef = "PLAN-1" }, Owner());
    Assert(result.Allowed && Current(ctx).State == "MEASUREMENT_PENDING", "plan");
}

static void MeasureOwnerHappy()
{
    var ctx = Context(NewBenefit("MEASUREMENT_PENDING", baseline: "BASE-REF", target: "TARGET-REF", plan: "PLAN-1"));
    var result = Exec(ctx, Cmd("benefits.measure", 1) with { MeasurementDossierRef = "MEASURE-1" }, Owner());
    Assert(result.Allowed && Current(ctx).State == "MEASURED" && Current(ctx).MeasuredByPersonId == "P-BEN", "measure");
}

static void DataProviderDenied()
{
    var ctx = Context(NewBenefit("MEASUREMENT_PENDING", baseline: "BASE-REF", target: "TARGET-REF", plan: "PLAN-1"));
    var result = Exec(ctx, Cmd("benefits.measure", 1) with { MeasurementDossierRef = "MEASURE-1" }, DataProvider());
    Assert(!result.Allowed && result.Code == "P1_BENEFIT_DATA_PROVIDER_POLICY_REQUIRED", "provider denied");
}

static void DataProviderAllowed()
{
    var authority = new StaticBenefitMeasurementAuthorityProviderWave11(
        new BenefitMeasurementAuthorityEvidenceWave11("BEN-1", "P-DATA", "A-DATA", "UNIT:RND", true, "MEASURE-AUTH-EV", "1"));
    var ctx = Context(NewBenefit("MEASUREMENT_PENDING", baseline: "BASE-REF", target: "TARGET-REF", plan: "PLAN-1"), measurementAuthority: authority);
    var result = Exec(ctx, Cmd("benefits.measure", 1) with { MeasurementDossierRef = "MEASURE-1" }, DataProvider());
    Assert(result.Allowed && Current(ctx).MeasuredByPersonId == "P-DATA", "provider allowed");
}

static void VerifierEvidenceRequired()
{
    var ctx = Context(NewBenefit("MEASURED", measurement: "MEASURE-1"), executionEvidence: Array.Empty<BenefitExecutionOwnerEvidenceWave11>());
    var result = Exec(ctx, Cmd("benefits.verify", 1) with { VerificationDossierRef = "VERIFY-1" }, Verifier());
    Assert(!result.Allowed && result.Code == "P1_BENEFIT_EXECUTION_OWNER_EVIDENCE_REQUIRED", "execution owner evidence");
}

static void VerifierSod()
{
    var evidence = new[] { new BenefitExecutionOwnerEvidenceWave11("BEN-1", "EX-1", "P-VER", "EX-OWNER-EV", "1") };
    var ctx = Context(NewBenefit("MEASURED", measurement: "MEASURE-1"), executionEvidence: evidence);
    var result = Exec(ctx, Cmd("benefits.verify", 1) with { VerificationDossierRef = "VERIFY-1" }, Verifier());
    Assert(!result.Allowed && result.Code == "SOD_BENEFIT_VERIFIER_EXECUTION_OWNER", "sod");
}

static void VerifyHappy()
{
    var ctx = Context(NewBenefit("MEASURED", measurement: "MEASURE-1"));
    var result = Exec(ctx, Cmd("benefits.verify", 1) with { VerificationDossierRef = "VERIFY-1" }, Verifier());
    Assert(result.Allowed && Current(ctx).State == "VERIFIED" && Current(ctx).VerifiedByPersonId == "P-VER", "verify");
}

static void AttributionDistinct()
{
    var ctx = Context(NewBenefit("VERIFIED", measurement: "MEASURE-1", verification: "VERIFY-1"));
    var result = Exec(ctx, Cmd("benefits.attribution", 1) with { AttributionDossierRef = "ATTR-1" }, Verifier());
    Assert(result.Allowed && Current(ctx).State == "VALIDATED", "attribution");
    Assert(ctx.Store.BenefitOutbox.Single().EventName == "BenefitAttributionValidated.v1", "event");
}

static void RealizationDossierRequired()
{
    var ctx = Context(NewBenefit("VALIDATED", attribution: "ATTR-1"));
    var result = Exec(ctx, Cmd("benefits.realize", 1), Owner());
    Assert(!result.Allowed && result.Code == "P1_BENEFIT_REALIZATION_DOSSIER_REQUIRED", "dossier");
}

static void RealizeHappy()
{
    var ctx = Context(NewBenefit("VALIDATED", attribution: "ATTR-1"));
    var result = Exec(ctx, Cmd("benefits.realize", 1) with { RealizationDossierRef = "REAL-1" }, Owner());
    Assert(result.Allowed && Current(ctx).State == "REALIZED", "realize");
}

static void CloseKnowledgeRequired()
{
    var ctx = Context(NewBenefit("REALIZED", realization: "REAL-1"), knowledgeEvidence: Array.Empty<BenefitKnowledgePublicationEvidenceWave11>());
    var result = Exec(ctx, Cmd("benefits.close", 1), Owner());
    Assert(!result.Allowed && result.Code == "P1_BENEFIT_PUBLISHED_KNOWLEDGE_REQUIRED", "knowledge required");
}

static void CloseKnowledgeNotPublished()
{
    var knowledge = new[] { new BenefitKnowledgePublicationEvidenceWave11("BEN-1", "KN-1", true, "VALIDATED", "PUB-DOS", "KN-EV", "1") };
    var ctx = Context(NewBenefit("REALIZED", realization: "REAL-1"), knowledgeEvidence: knowledge);
    var result = Exec(ctx, Cmd("benefits.close", 1), Owner());
    Assert(!result.Allowed && result.Code == "P1_BENEFIT_PUBLISHED_KNOWLEDGE_REQUIRED", "published required");
}

static void CloseHappy()
{
    var ctx = Context(NewBenefit("REALIZED", realization: "REAL-1"));
    var result = Exec(ctx, Cmd("benefits.close", 1), Owner());
    Assert(result.Allowed && Current(ctx).State == "CLOSED" && Current(ctx).KnowledgeId == "KN-1", "close");
}

static void ReplaySafety()
{
    var ctx = Context(NewBenefit("OBLIGATION_PENDING_ACCEPTANCE"));
    var command = Cmd("benefits.accept", 1, "IDEM-SAME");
    var first = Exec(ctx, command, Owner());
    Assert(first.Allowed, "first");
    var replayService = new BenefitServiceWave11(
        ctx.Store,
        new StaticBenefitOwnershipEvidenceProviderWave11(),
        new StaticBenefitBaselineTargetEvidenceProviderWave11(),
        new StaticBenefitExecutionOwnerEvidenceProviderWave11(),
        new BenefitOwnerOnlyMeasurementAuthorityProviderWave11(),
        new StaticBenefitKnowledgePublicationEvidenceProviderWave11());
    var replay = replayService.ExecuteAsync(command, Owner()).AsTask().GetAwaiter().GetResult();
    Assert(replay.Allowed && replay.IdempotentReplay && !replay.StateMutated, "replay before mutable evidence read");
}

static void ReplayConflict()
{
    var ctx = Context(NewBenefit("PLAN_REQUIRED", baseline: "BASE-REF", target: "TARGET-REF"));
    var command = Cmd("benefits.approve-measurement-plan", 1, "IDEM-X") with { MeasurementPlanRef = "PLAN-1" };
    Assert(Exec(ctx, command, Owner()).Allowed, "first");
    var changed = command with { MeasurementPlanRef = "PLAN-2" };
    var replay = Exec(ctx, changed, Owner());
    Assert(!replay.Allowed && replay.Code == "P1_IDEMPOTENCY_CONFLICT", "conflict");
}

static void StaleVersion()
{
    var ctx = Context(NewBenefit("OBLIGATION_PENDING_ACCEPTANCE", version: 2));
    var result = Exec(ctx, Cmd("benefits.accept", 1), Owner());
    Assert(!result.Allowed && result.Code == "P1_BENEFIT_VERSION_CONFLICT", "stale");
}

static void WrongRole()
{
    var ctx = Context(NewBenefit("OBLIGATION_PENDING_ACCEPTANCE"));
    var result = Exec(ctx, Cmd("benefits.accept", 1), Actor("P-BEN", "A-BEN", "EXECUTION_OWNER"));
    Assert(!result.Allowed && result.Code == "P1_BENEFIT_COMMAND_ROLE_REQUIRED", "role");
}

static void ScopeCannotWiden()
{
    var ctx = Context(NewBenefit("OBLIGATION_PENDING_ACCEPTANCE"));
    var actor = new AuthorityActor("P-BEN", "DOMAIN\\ben", "WINDOWS", "A-BEN", new[] { "BENEFIT_OWNER" }, new[] { "GLOBAL" });
    var result = Exec(ctx, Cmd("benefits.accept", 1) with { RequestedScope = "GLOBAL" }, actor);
    Assert(!result.Allowed && result.Code == "P1_BENEFIT_OWNERSHIP_SCOPE_DENIED", "scope");
}

static void DigitalThread()
{
    var before = NewBenefit("VALIDATED", attribution: "ATTR-1");
    var ctx = Context(before);
    Assert(Exec(ctx, Cmd("benefits.realize", 1) with { RealizationDossierRef = "REAL-1" }, Owner()).Allowed, "realize");
    var after = Current(ctx);
    Assert(after.ExecutionId == before.ExecutionId && after.IdeaId == before.IdeaId && after.ApprovedIdeaVersion == before.ApprovedIdeaVersion, "thread");
}

static void FaultRollback()
{
    var ctx = Context(NewBenefit("OBLIGATION_PENDING_ACCEPTANCE"));
    ctx.Store.FaultPoint = BenefitWave11PersistenceFaultPoint.BeforeCommitPublish;
    try
    {
        _ = Exec(ctx, Cmd("benefits.accept", 1), Owner());
        throw new Exception("fault not thrown");
    }
    catch (PersistenceAtomicityException) { }
    Assert(Current(ctx).State == "OBLIGATION_PENDING_ACCEPTANCE", "state rollback");
    Assert(ctx.Store.BenefitAuditLog.Count == 0 && ctx.Store.BenefitOutbox.Count == 0 && ctx.Store.BenefitIdempotencyRecords.Count == 0, "evidence rollback");
}

static void FullLifecycle()
{
    var ctx = Context(NewBenefit("OBLIGATION_PENDING_ACCEPTANCE"));
    Assert(Exec(ctx, Cmd("benefits.accept", 1, "I1"), Owner()).Allowed, "accept");
    Assert(Exec(ctx, BaselineCmd(2, "I2"), Owner()).Allowed, "baseline");
    Assert(Exec(ctx, Cmd("benefits.approve-measurement-plan", 3, "I3") with { MeasurementPlanRef = "PLAN-1" }, Owner()).Allowed, "plan");
    Assert(Exec(ctx, Cmd("benefits.measure", 4, "I4") with { MeasurementDossierRef = "MEASURE-1" }, Owner()).Allowed, "measure");
    Assert(Exec(ctx, Cmd("benefits.verify", 5, "I5") with { VerificationDossierRef = "VERIFY-1" }, Verifier()).Allowed, "verify");
    Assert(Exec(ctx, Cmd("benefits.attribution", 6, "I6") with { AttributionDossierRef = "ATTR-1" }, Verifier()).Allowed, "attribution");
    Assert(Exec(ctx, Cmd("benefits.realize", 7, "I7") with { RealizationDossierRef = "REAL-1" }, Owner()).Allowed, "realize");
    Assert(Exec(ctx, Cmd("benefits.close", 8, "I8"), Owner()).Allowed, "close");
    Assert(Current(ctx).State == "CLOSED" && Current(ctx).Version == 9, "terminal");
    var expected = new[] { "BenefitObligationAccepted.v1", "BenefitBaselineDefined.v1", "BenefitMeasurementPlanApproved.v1", "BenefitMeasured.v1", "BenefitVerified.v1", "BenefitAttributionValidated.v1", "BenefitRealized.v1", "BenefitClosed.v1" };
    Assert(ctx.Store.BenefitOutbox.Select(x => x.EventName).SequenceEqual(expected), "event sequence");
}

static TestContext Context(
    BenefitEnvelopeWave11 benefit,
    BenefitBaselineTargetEvidenceWave11[]? baselineEvidence = null,
    BenefitExecutionOwnerEvidenceWave11[]? executionEvidence = null,
    IBenefitMeasurementAuthorityProviderWave11? measurementAuthority = null,
    BenefitKnowledgePublicationEvidenceWave11[]? knowledgeEvidence = null)
{
    var store = new BenefitTransactionalStoreWave11(benefit);
    var owner = new StaticBenefitOwnershipEvidenceProviderWave11(
        new BenefitOwnershipEvidenceWave11("BEN-1", "P-BEN", "A-BEN", "UNIT:RND", "BEN-OWNER-EV", "1"));
    var baseline = new StaticBenefitBaselineTargetEvidenceProviderWave11(
        baselineEvidence ?? new[] { new BenefitBaselineTargetEvidenceWave11("BEN-1", "BASE-REF", "TARGET-REF", true, true, true, "NON_FINANCIAL", "BASE-TARGET-EV", "1") });
    var execOwner = new StaticBenefitExecutionOwnerEvidenceProviderWave11(
        executionEvidence ?? new[] { new BenefitExecutionOwnerEvidenceWave11("BEN-1", "EX-1", "P-EXEC", "EX-OWNER-EV", "1") });
    var measureAuth = measurementAuthority ?? new BenefitOwnerOnlyMeasurementAuthorityProviderWave11();
    var knowledge = new StaticBenefitKnowledgePublicationEvidenceProviderWave11(
        knowledgeEvidence ?? new[] { new BenefitKnowledgePublicationEvidenceWave11("BEN-1", "KN-1", true, "PUBLISHED", "PUB-DOS", "KN-EV", "1") });
    return new TestContext(store, new BenefitServiceWave11(store, owner, baseline, execOwner, measureAuth, knowledge));
}

static BenefitEnvelopeWave11 Current(TestContext ctx) => ctx.Store.Benefits.Single();
static AuthorityResult Exec(TestContext ctx, BenefitCommandWave11 command, AuthorityActor actor) =>
    ctx.Service.ExecuteAsync(command, actor).AsTask().GetAwaiter().GetResult();

static BenefitEnvelopeWave11 NewBenefit(
    string state,
    long version = 1,
    string? baseline = null,
    string? target = null,
    string? plan = null,
    string? measurement = null,
    string? verification = null,
    string? attribution = null,
    string? realization = null) =>
    new(
        "BEN-1", "EX-1", "IDEA-1", 7, state, version,
        baseline, target, plan, measurement, verification, attribution, realization,
        null, null, null, null, null,
        DateTimeOffset.Parse("2026-09-16T00:00:00Z"),
        DateTimeOffset.Parse("2026-09-16T00:00:00Z"),
        "CORR-BASE");

static BenefitObligationSourceEvidenceWave11 Source() =>
    new("BenefitObligationCreated.v1", "BEN-1", "EX-1", "IDEA-1", 7, "OBLIGATION_PENDING_ACCEPTANCE", 1,
        "CORR-IN", DateTimeOffset.Parse("2026-09-16T00:00:00Z"), "W10-OUTBOX-REF", "1");

static BenefitCommandWave11 Cmd(string name, long version, string? idem = null) =>
    new(name, "BEN-1", version, idem ?? "IDEM-" + name, "CORR-" + name, "UNIT:RND");

static BenefitCommandWave11 BaselineCmd(long version, string? idem = null) =>
    Cmd("benefits.set-baseline", version, idem) with { BaselineEvidenceRef = "BASE-REF", TargetEvidenceRef = "TARGET-REF" };

static AuthorityActor Owner() => Actor("P-BEN", "A-BEN", "BENEFIT_OWNER");
static AuthorityActor Verifier() => Actor("P-VER", "A-VER", "BENEFIT_VERIFIER");
static AuthorityActor DataProvider() => Actor("P-DATA", "A-DATA", "AUTHORIZED_DATA_PROVIDER");
static AuthorityActor Actor(string person, string assignment, string role) =>
    new(person, "DOMAIN\\" + person, "WINDOWS", assignment, new[] { role }, new[] { "UNIT:RND" });

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed record TestContext(BenefitTransactionalStoreWave11 Store, BenefitServiceWave11 Service);
