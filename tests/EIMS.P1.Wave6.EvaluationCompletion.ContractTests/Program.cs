using System.Text.Json;
using EIMS.Authority.Recovery;
using EIMS.Identity.Rbac;
using EIMS.Persistence.Recovery;
using EIMS.PilotAssembly.Core;

if (args.Length != 3 || args.Any(x => !File.Exists(x)))
{
    Console.Error.WriteLine("Usage: EIMS.P1.Wave6.EvaluationCompletion.ContractTests <schema-registry-json> <ACR-P0-005-json> <Wave6-json>");
    return 2;
}

var registryJson = File.ReadAllText(args[0]);
using var acrDoc = JsonDocument.Parse(File.ReadAllText(args[1]));
using var waveDoc = JsonDocument.Parse(File.ReadAllText(args[2]));
var acr = acrDoc.RootElement;
var wave = waveDoc.RootElement;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("P1W6-CT-01 ACR and Wave6 artifact identity are exact", ArtifactIdentity),
    ("P1W6-CT-02 cumulative catalog keeps 21 command identities and promotes exactly four mutations", CatalogExact),
    ("P1W6-CT-03 G04 vote and final decision remain fail closed", G04StillClosed),
    ("P1W6-CT-04 P5 gateway remains fail closed", P5StillClosed),
    ("P1W6-CT-05 recovered validator accepts IDEA_EVALUATOR schema", () => ValidateRole("IDEA_EVALUATOR")),
    ("P1W6-CT-06 recovered validator accepts TECHNICAL_ASSESSOR schema", () => ValidateRole("TECHNICAL_ASSESSOR")),
    ("P1W6-CT-07 recovered validator accepts UNIT_OWNER_REVIEWER schema", () => ValidateRole("UNIT_OWNER_REVIEWER")),
    ("P1W6-CT-08 recovered validator accepts HSE_ASSESSOR schema", () => ValidateRole("HSE_ASSESSOR")),
    ("P1W6-CT-09 recovered validator accepts FINANCIAL_ASSESSOR schema", () => ValidateRole("FINANCIAL_ASSESSOR")),
    ("P1W6-CT-10 recovered validator accepts IT_ASSESSOR schema", () => ValidateRole("IT_ASSESSOR")),
    ("P1W6-CT-11 unknown evaluator role fails closed", UnknownRoleClosed),
    ("P1W6-CT-12 malformed assessment JSON is rejected", MalformedJsonRejected),
    ("P1W6-CT-13 generic missing note is rejected", GenericInvalidRejected),
    ("P1W6-CT-14 structured out-of-range score is rejected", StructuredInvalidRejected),
    ("P1W6-CT-15 technical conditional decision requires conditions", TechnicalConditionRequired),
    ("P1W6-CT-16 P3 exact authority assignment resolves one evaluator role and scope", P3ExactAssignment),
    ("P1W6-CT-17 first required completion persists immutable assessment and keeps Plan ACTIVE", FirstCompletion),
    ("P1W6-CT-18 negative professional outcome can still complete workflow mission", NegativeOutcomeCompletes),
    ("P1W6-CT-19 wrong authority role is denied before completion", WrongRoleDenied),
    ("P1W6-CT-20 wrong authority scope is denied before completion", WrongScopeDenied),
    ("P1W6-CT-21 stale assignment version is denied", StaleAssignmentVersionDenied),
    ("P1W6-CT-22 stale Plan version is denied", StalePlanVersionDenied),
    ("P1W6-CT-23 exact replay is idempotent and creates no duplicate evidence", ExactReplay),
    ("P1W6-CT-24 changed replay conflicts and cannot overwrite completed mission", ChangedReplayConflict),
    ("P1W6-CT-25 final required completion makes Plan ready and creates one G04Assessment", FinalRequiredCompletion),
    ("P1W6-CT-26 final completion emits exactly two completion-boundary events", FinalEvents),
    ("P1W6-CT-27 integration events contain no assessment answer content", EventPayloadMinimized),
    ("P1W6-CT-28 Audit preserves Person and authority assignment context", AuditContext),
    ("P1W6-CT-29 fault after assignment completion staging rolls back everything", () => RollbackAt(PersistenceFaultPoint.AfterEvaluationAssignmentCompletionStaged)),
    ("P1W6-CT-30 fault after assessment snapshot staging rolls back everything", () => RollbackAt(PersistenceFaultPoint.AfterAssessmentSnapshotStaged)),
    ("P1W6-CT-31 fault after Plan readiness staging rolls back everything", () => RollbackAt(PersistenceFaultPoint.AfterEvaluationPlanReadinessStaged)),
    ("P1W6-CT-32 P2 logical contract explicitly supports evaluator completion atomicity", PersistenceContractExtended),
    ("P1W6-CT-33 Wave6 artifact preserves no-live-environment readiness claims", EnvironmentSafety),
    ("P1W6-CT-34 frozen v6.360 remains unchanged", FrozenBaseline),
    ("P1W6-CT-35 workflow completion outcome remains separate from professional result", OutcomeSeparation)
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
    Eq("ACR-P0-005", S(acr, "acrId"));
    Eq("EIMS-P1-WAVE6-EVALUATION-COMPLETION-1.0", S(wave, "schema"));
    Eq("POST_FREEZE_IMPLEMENTATION_REBASELINE_NOT_ORIGINAL", S(wave, "status"));
    return Task.CompletedTask;
}

Task CatalogExact()
{
    var c5 = new RecoveredApiCommandCatalogWave5();
    var c6 = new RecoveredApiCommandCatalogWave6();
    Eq(21, c6.All.Count);
    Eq(3, c5.All.Count(x => x.MutationContractRecovered));
    Eq(4, c6.All.Count(x => x.MutationContractRecovered));
    Eq(4, RecoveredApiCommandCatalogWave6.Wave6RecoveredMutationCommandCount);
    var p = c6.All.Single(x => x.CommandName == "evaluation-assignments.complete");
    True(p.StateContractRecovered && p.RuleContractRecovered && p.EventContractRecovered && p.MutationContractRecovered);
    Eq("EvaluationAssignmentCompleted.v1", p.ResolveEventName()!);
    return Task.CompletedTask;
}

async Task G04StillClosed()
{
    var idea = Idea(state: "UNDER_REVIEW", version: 6);
    var store = new TransactionalAuthorityStore(PersistenceContractDescriptor.RecoveryBaseline(), idea);
    var kernel = new AuthorityKernel(new RecoveredApiCommandCatalogWave6(), store, new RecoveredWave5RuleEvaluator(), new BaselineSodEvaluator(), new RecoveredWave5MutationPlanner());
    var vote = await kernel.ExecuteAsync(new("g04.vote", "IDEA-1", 6, "V1", "C1", "{}"), Actor("G04_COMMITTEE_MEMBER"));
    var final = await kernel.ExecuteAsync(new("g04.final-decision", "IDEA-1", 6, "F1", "C2", "{}"), Actor("IDEA_DECISION"));
    Eq(503, vote.HttpStatus); False(vote.StateMutated);
    Eq(503, final.HttpStatus); False(final.StateMutated);
}

async Task P5StillClosed()
{
    ICommandGateway gateway = new FailClosedCommandGateway();
    var result = await gateway.ExecuteAsync(new CommandAttempt("evaluation-assignments.complete", "DOMAIN\\evaluator", "P5-CORR", 1, "P5-IDEM", "{}"));
    Eq(503, result.HttpStatus); False(result.StateMutated);
}

Task ValidateRole(string role)
{
    var validator = new RecoveredEvaluatorAssessmentValidator(registryJson);
    var r = validator.Validate(role, Body(role));
    True(r.Passed); True(!string.IsNullOrWhiteSpace(r.SchemaId)); True(!string.IsNullOrWhiteSpace(r.NormalizedAssessmentJson));
    return Task.CompletedTask;
}

Task UnknownRoleClosed()
{
    var r = new RecoveredEvaluatorAssessmentValidator(registryJson).Validate("UNKNOWN_ROLE", "{}");
    False(r.Passed); Eq("P1_EVALUATION_SCHEMA_NOT_BOUND", r.Code); return Task.CompletedTask;
}

Task MalformedJsonRejected()
{
    var r = new RecoveredEvaluatorAssessmentValidator(registryJson).Validate("IDEA_EVALUATOR", "{");
    False(r.Passed); Eq("P1_EVALUATION_ASSESSMENT_JSON_INVALID", r.Code); return Task.CompletedTask;
}

Task GenericInvalidRejected()
{
    var bad = "{\"answers\":{\"needFit\":{\"answer\":\"CONFIRMED\",\"note\":\"\"}},\"outcome\":\"PASS\",\"score\":4,\"evidenceRef\":\"REF\",\"comment\":\"12345678901234567890\"}";
    var r = new RecoveredEvaluatorAssessmentValidator(registryJson).Validate("UNIT_OWNER_REVIEWER", bad);
    False(r.Passed); return Task.CompletedTask;
}

Task StructuredInvalidRejected()
{
    var bad = IdeaBody().Replace("\"quality\":{\"score\":4}", "\"quality\":{\"score\":9}");
    var r = new RecoveredEvaluatorAssessmentValidator(registryJson).Validate("IDEA_EVALUATOR", bad);
    False(r.Passed); Eq("P1_EVALUATION_CRITERION_SCORE_INVALID", r.Code); return Task.CompletedTask;
}

Task TechnicalConditionRequired()
{
    var bad = TechnicalBody("CONDITIONAL_PASS").Replace(",\"conditions\":\"required action\"", "");
    var r = new RecoveredEvaluatorAssessmentValidator(registryJson).Validate("TECHNICAL_ASSESSOR", bad);
    False(r.Passed); Eq("P1_EVALUATION_CONDITIONS_REQUIRED", r.Code); return Task.CompletedTask;
}

async Task P3ExactAssignment()
{
    var now = DateTimeOffset.UtcNow;
    var directory = new InMemoryIdentityDirectoryStore(
        [new PersonDirectoryEntry("P-EVAL", "DOMAIN\\eval", DirectoryPersonStatus.Active)],
        [new RoleAssignmentEntry("AUTH-EVAL", "P-EVAL", "IDEA_EVALUATOR", ["UNIT:RND"], now.AddDays(-1), null, false)]);
    var result = await new WindowsIdentityRbacResolver(directory).ResolveAsync(new("DOMAIN\\eval", "AUTH-EVAL", "UNIT:RND", now));
    True(result.Allowed); Eq("IDEA_EVALUATOR", result.Actor!.Roles.Single()); Eq("AUTH-EVAL", result.Actor.AssignmentId);
}

async Task FirstCompletion()
{
    var ctx = await SeedAllRoles();
    var mission = Mission(ctx.Store, "UNIT_OWNER_REVIEWER");
    var result = await Complete(ctx, mission, GenericBody("UNIT_OWNER_REVIEWER", "RETURN"), "C-UNIT-1");
    Eq(200, result.HttpStatus); True(result.StateMutated);
    var after = (await ctx.Store.GetEvaluationAssignmentAsync(mission.AssignmentId))!;
    Eq("COMPLETED", after.State); Eq(2, after.AssignmentVersion); Eq("RETURN", after.AssessmentOutcome!);
    Eq(1, ctx.Store.AssessmentSnapshots.Count);
    Eq("ACTIVE", (await ctx.Store.GetEvaluationPlanAsync(mission.PlanId))!.State);
    Eq(0, ctx.Store.G04Assessments.Count);
}

async Task NegativeOutcomeCompletes()
{
    var ctx = await SeedAllRoles();
    var mission = Mission(ctx.Store, "TECHNICAL_ASSESSOR");
    var result = await Complete(ctx, mission, TechnicalBody("FAIL"), "C-TECH-FAIL");
    Eq(200, result.HttpStatus);
    var after = (await ctx.Store.GetEvaluationAssignmentAsync(mission.AssignmentId))!;
    Eq("COMPLETED", after.State); Eq("FAIL", after.AssessmentOutcome!);
}

async Task WrongRoleDenied()
{
    var ctx = await SeedAllRoles();
    var mission = Mission(ctx.Store, "IDEA_EVALUATOR");
    var bad = Actor("FINANCIAL_ASSESSOR");
    var command = Command(ctx.Store, mission, IdeaBody(), "WRONG-ROLE");
    var result = await ctx.Service.CompleteAsync(command, bad);
    Eq(403, result.HttpStatus); Eq("P1_EVALUATION_AUTHORITY_ROLE_MISMATCH", result.Code);
    Eq("PENDING", (await ctx.Store.GetEvaluationAssignmentAsync(mission.AssignmentId))!.State);
}

async Task WrongScopeDenied()
{
    var ctx = await SeedAllRoles();
    var mission = Mission(ctx.Store, "IDEA_EVALUATOR");
    var bad = new AuthorityActor("P-IDEA_EVALUATOR", "DOMAIN\\idea_evaluator", "WINDOWS_PRINCIPAL", "AUTH-IDEA_EVALUATOR", ["IDEA_EVALUATOR"], ["UNIT:FIN"]);
    var result = await ctx.Service.CompleteAsync(Command(ctx.Store, mission, IdeaBody(), "WRONG-SCOPE"), bad);
    Eq(403, result.HttpStatus); Eq("P1_EVALUATION_AUTHORITY_SCOPE_MISMATCH", result.Code);
}

async Task StaleAssignmentVersionDenied()
{
    var ctx = await SeedAllRoles(); var m = Mission(ctx.Store, "IDEA_EVALUATOR");
    var c = Command(ctx.Store, m, IdeaBody(), "STALE-A") with { ExpectedAssignmentVersion = 0 };
    var r = await ctx.Service.CompleteAsync(c, Actor(m.Role)); Eq(409, r.HttpStatus); Eq("P1_EVALUATION_ASSIGNMENT_VERSION_CONFLICT", r.Code);
}

async Task StalePlanVersionDenied()
{
    var ctx = await SeedAllRoles(); var m = Mission(ctx.Store, "IDEA_EVALUATOR");
    var c = Command(ctx.Store, m, IdeaBody(), "STALE-P") with { ExpectedPlanVersion = 0 };
    var r = await ctx.Service.CompleteAsync(c, Actor(m.Role)); Eq(409, r.HttpStatus); Eq("P1_EVALUATION_PLAN_VERSION_CONFLICT", r.Code);
}

async Task ExactReplay()
{
    var ctx = await SeedAllRoles(); var m = Mission(ctx.Store, "UNIT_OWNER_REVIEWER");
    var c = Command(ctx.Store, m, GenericBody(m.Role, "PASS"), "REPLAY-1");
    var first = await ctx.Service.CompleteAsync(c, Actor(m.Role));
    var replay = await ctx.Service.CompleteAsync(c, Actor(m.Role));
    True(first.StateMutated); True(replay.IdempotentReplay); False(replay.StateMutated);
    Eq(1, ctx.Store.AssessmentSnapshots.Count);
}

async Task ChangedReplayConflict()
{
    var ctx = await SeedAllRoles(); var m = Mission(ctx.Store, "UNIT_OWNER_REVIEWER");
    var c = Command(ctx.Store, m, GenericBody(m.Role, "PASS"), "REPLAY-2");
    await ctx.Service.CompleteAsync(c, Actor(m.Role));
    var changed = c with { RawAssessmentJson = GenericBody(m.Role, "FAIL") };
    var r = await ctx.Service.CompleteAsync(changed, Actor(m.Role));
    Eq(409, r.HttpStatus); Eq("P1_IDEMPOTENCY_CONFLICT", r.Code); Eq(1, ctx.Store.AssessmentSnapshots.Count);
}

async Task FinalRequiredCompletion()
{
    var ctx = await CompleteFirstFive();
    var remaining = ctx.Store.EvaluationAssignments.Single(x => x.State == "PENDING");
    var result = await Complete(ctx, remaining, Body(remaining.Role), "FINAL-1");
    Eq(200, result.HttpStatus);
    var plan = (await ctx.Store.GetEvaluationPlanAsync(remaining.PlanId))!;
    Eq("READY_FOR_G04_DECISION", plan.State); True(plan.ReadyAt is not null);
    Eq(1, ctx.Store.G04Assessments.Count); Eq("PENDING", ctx.Store.G04Assessments.Single().State);
    Eq(6, ctx.Store.AssessmentSnapshots.Count);
}

async Task FinalEvents()
{
    var ctx = await CompleteFirstFive(); var remaining = ctx.Store.EvaluationAssignments.Single(x => x.State == "PENDING");
    var r = await Complete(ctx, remaining, Body(remaining.Role), "FINAL-EVENTS");
    Eq(2, r.EmittedEvents.Count); True(r.EmittedEvents.Contains("EvaluationAssignmentCompleted.v1")); True(r.EmittedEvents.Contains("G04DecisionAssessmentCreated.v1"));
}

async Task EventPayloadMinimized()
{
    var ctx = await SeedAllRoles(); var m = Mission(ctx.Store, "UNIT_OWNER_REVIEWER");
    await Complete(ctx, m, GenericBody(m.Role, "PASS"), "MIN-EVENT");
    var e = ctx.Store.Outbox.Last(); var joined = string.Join("|", e.Payload!.Keys);
    False(joined.Contains("answer", StringComparison.OrdinalIgnoreCase)); False(joined.Contains("comment", StringComparison.OrdinalIgnoreCase)); False(joined.Contains("assessment", StringComparison.OrdinalIgnoreCase));
}

async Task AuditContext()
{
    var ctx = await SeedAllRoles(); var m = Mission(ctx.Store, "HSE_ASSESSOR");
    await Complete(ctx, m, Body(m.Role), "AUDIT-1");
    var a = ctx.Store.AuditLog.Last(); Eq("P-HSE_ASSESSOR", a.PersonId); Eq("AUTH-HSE_ASSESSOR", a.Assignment); Eq("evaluation-assignments.complete", a.CommandName);
}

async Task RollbackAt(PersistenceFaultPoint point)
{
    var ctx = await SeedAllRoles(); var m = Mission(ctx.Store, "UNIT_OWNER_REVIEWER");
    var baselineAudit = ctx.Store.AuditLog.Count; var baselineOutbox = ctx.Store.Outbox.Count; var baselineIdem = ctx.Store.IdempotencyRecords.Count;
    ctx.Store.FaultPoint = point; var threw = false;
    try { await Complete(ctx, m, Body(m.Role), $"RB-{point}"); } catch (PersistenceAtomicityException) { threw = true; }
    True(threw); Eq("PENDING", (await ctx.Store.GetEvaluationAssignmentAsync(m.AssignmentId))!.State);
    Eq(0, ctx.Store.AssessmentSnapshots.Count); Eq(0, ctx.Store.G04Assessments.Count);
    Eq(baselineAudit, ctx.Store.AuditLog.Count); Eq(baselineOutbox, ctx.Store.Outbox.Count); Eq(baselineIdem, ctx.Store.IdempotencyRecords.Count);
    Eq("ACTIVE", (await ctx.Store.GetEvaluationPlanAsync(m.PlanId))!.State);
}

Task PersistenceContractExtended()
{
    var p = PersistenceContractDescriptor.RecoveryBaseline(); True(p.AtomicEvaluationCompletionSupported); True(p.AppendOnlyAssessmentSnapshotRequired); True(p.IsLogicalContractReady); False(p.IsPhysicalOracleReady); return Task.CompletedTask;
}

Task EnvironmentSafety()
{
    var env = wave.GetProperty("environmentClaims"); False(B(env, "physicalOracleReady")); False(B(env, "liveWindowsDomainReady")); False(B(env, "networkPilotReady")); return Task.CompletedTask;
}

Task FrozenBaseline()
{
    var f = wave.GetProperty("frozenProduct"); Eq("057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a", S(f, "sha256")); False(B(f, "modified")); return Task.CompletedTask;
}

Task OutcomeSeparation()
{
    var c = wave.GetProperty("completionBoundary"); True(B(c, "professionalOutcomeSeparateFromWorkflowCompletion")); return Task.CompletedTask;
}

async Task<Context> SeedAllRoles()
{
    var store = new TransactionalAuthorityStore(PersistenceContractDescriptor.RecoveryBaseline(), Idea(facts: GoodFacts(true, true, true, true)));
    var kernel = new AuthorityKernel(new RecoveredApiCommandCatalogWave5(), store, new RecoveredWave5RuleEvaluator(), new BaselineSodEvaluator(), new RecoveredWave5MutationPlanner());
    var submit = await kernel.ExecuteAsync(new AuthorityCommand("ideas.submit-g04", "IDEA-1", 5, "SEED", "SEED-CORR", "{}", "UNIT:RND"), Actor("IDEA_OWNER"));
    Eq(200, submit.HttpStatus); Eq(6, store.EvaluationAssignments.Count); Eq(1, store.EvaluationPlans.Count);
    return new Context(store, new EvaluationCompletionService(store, new RecoveredEvaluatorAssessmentValidator(registryJson)));
}

async Task<Context> CompleteFirstFive()
{
    var ctx = await SeedAllRoles();
    foreach (var mission in ctx.Store.EvaluationAssignments.Take(5).ToArray())
        Eq(200, (await Complete(ctx, mission, Body(mission.Role), $"PRE-{mission.Role}")).HttpStatus);
    Eq(1, ctx.Store.EvaluationAssignments.Count(x => x.State == "PENDING")); Eq(0, ctx.Store.G04Assessments.Count);
    return ctx;
}

async Task<AuthorityResult> Complete(Context ctx, EvaluationAssignmentEnvelope mission, string body, string key) =>
    await ctx.Service.CompleteAsync(Command(ctx.Store, mission, body, key), Actor(mission.Role));

static EvaluationCompletionCommand Command(TransactionalAuthorityStore store, EvaluationAssignmentEnvelope mission, string body, string key)
{
    var plan = store.EvaluationPlans.Single(x => x.PlanId == mission.PlanId);
    var current = store.EvaluationAssignments.Single(x => x.AssignmentId == mission.AssignmentId);
    return new("IDEA-1", 6, plan.PlanId, plan.PlanVersion, current.AssignmentId, current.AssignmentVersion, key, $"CORR-{key}", body, "UNIT:RND");
}

static EvaluationAssignmentEnvelope Mission(TransactionalAuthorityStore store, string role) => store.EvaluationAssignments.Single(x => x.Role == role);

static AuthorityActor Actor(string role) => new($"P-{role}", $"DOMAIN\\{role.ToLowerInvariant()}", "WINDOWS_PRINCIPAL", $"AUTH-{role}", [role], ["UNIT:RND"]);

static Dictionary<string, string> GoodFacts(bool technical, bool hse, bool financial, bool it) => new(StringComparer.OrdinalIgnoreCase)
{
    ["passportCompletionPercent"]="80", ["strategyLinkActive"]="YES", ["primaryObjectiveReady"]="YES",
    ["requiresTechnicalEvaluation"]=technical?"YES":"NO", ["requiresHseEvaluation"]=hse?"YES":"NO",
    ["requiresFinancialEvaluation"]=financial?"YES":"NO", ["requiresItEvaluation"]=it?"YES":"NO"
};

static AggregateSnapshot Idea(string state="DRAFT", long version=5, IReadOnlyDictionary<string,string>? facts=null) =>
    new("IDEA-1", "Idea", state, version, "P-OWNER", "IDEA_OWNER", "UNIT:RND", facts ?? GoodFacts(false,false,false,false), "IDEA_OWNER");

static string Body(string role) => role switch
{
    "IDEA_EVALUATOR" => IdeaBody(),
    "TECHNICAL_ASSESSOR" => TechnicalBody("PASS"),
    "UNIT_OWNER_REVIEWER" or "HSE_ASSESSOR" or "FINANCIAL_ASSESSOR" or "IT_ASSESSOR" => GenericBody(role, "PASS"),
    _ => "{}"
};

static string IdeaBody() => "{\"criteria\":{\"quality\":{\"score\":4},\"cost\":{\"score\":4},\"technical\":{\"score\":4},\"productionRisk\":{\"score\":4},\"timeReturn\":{\"score\":4},\"strategy\":{\"score\":4}},\"recommendation\":\"GO\",\"evaluatorNote\":\"evaluation note is sufficient\"}";

static string TechnicalBody(string decision) => "{\"criteria\":{\"maturity\":{\"score\":4},\"integration\":{\"score\":4},\"infrastructure\":{\"score\":4},\"maintainability\":{\"score\":4},\"testability\":{\"score\":4},\"vendor\":{\"score\":4},\"skills\":{\"score\":4},\"standards\":{\"score\":4}},\"decision\":\""+decision+"\",\"pilotRecommendation\":\"YES\",\"evidenceRef\":\"R\",\"summary\":\"S\""+(decision=="CONDITIONAL_PASS"?",\"conditions\":\"required action\"":"")+"}";

static string GenericBody(string role, string outcome)
{
    var keys = role switch
    {
        "UNIT_OWNER_REVIEWER" => new[] {"needFit","operationFit","resources","ownership"},
        "HSE_ASSESSOR" => new[] {"hazards","legal","controls","residualRisk"},
        "FINANCIAL_ASSESSOR" => new[] {"costBasis","lifeCycle","benefitBasis","funding"},
        "IT_ASSESSOR" => new[] {"architecture","security","integration","continuity"},
        _ => Array.Empty<string>()
    };
    var answers = string.Join(",", keys.Select(k => $"\"{k}\":{{\"answer\":\"CONFIRMED\",\"note\":\"ok evidence\"}}"));
    return $"{{\"answers\":{{{answers}}},\"outcome\":\"{outcome}\",\"score\":4,\"evidenceRef\":\"REF-1\",\"comment\":\"sufficient assessment comment\"}}";
}

static string S(JsonElement e,string p)=>e.TryGetProperty(p,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??string.Empty:string.Empty;
static bool B(JsonElement e,string p)=>e.GetProperty(p).GetBoolean();
static void True(bool v){if(!v)throw new InvalidOperationException("Expected true.");}
static void False(bool v){if(v)throw new InvalidOperationException("Expected false.");}
static void Eq<T>(T expected,T actual) where T:notnull{if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");}

sealed record Context(TransactionalAuthorityStore Store, EvaluationCompletionService Service);
