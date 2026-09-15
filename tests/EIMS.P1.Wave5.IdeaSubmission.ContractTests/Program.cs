using System.Text.Json;
using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;
using EIMS.PilotAssembly.Core;

if (args.Length != 2 || args.Any(x => !File.Exists(x)))
{
    Console.Error.WriteLine("Usage: EIMS.P1.Wave5.IdeaSubmission.ContractTests <ACR-P0-004-json> <P1-wave5-json>");
    return 2;
}

using var acrDoc = JsonDocument.Parse(File.ReadAllText(args[0]));
using var wave5Doc = JsonDocument.Parse(File.ReadAllText(args[1]));
var acr = acrDoc.RootElement;
var wave5 = wave5Doc.RootElement;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("P1W5-CT-01 ACR-P0-004 identity and frozen baseline are exact", AcrIdentity),
    ("P1W5-CT-02 Wave 5 artifact identity and source decision are exact", Wave5ArtifactIdentity),
    ("P1W5-CT-03 Wave 5 artifact preserves P5 and environment safety boundaries", Wave5Safety),
    ("P1W5-CT-04 historical Wave 4 catalog remains unchanged", HistoricalCatalogPreserved),
    ("P1W5-CT-05 cumulative Wave 5 catalog promotes exactly three mutations", Wave5CatalogPromotion),
    ("P1W5-CT-06 Idea submission policy is exact", IdeaPolicyExact),
    ("P1W5-CT-07 G04 committee voting remains fail closed", G04VoteStillClosed),
    ("P1W5-CT-08 G04 final decision remains fail closed", G04FinalStillClosed),
    ("P1W5-CT-09 P5 command gateway remains fail closed", P5GatewayStillClosed),
    ("P1W5-CT-10 DRAFT Idea submits successfully", DraftSubmit),
    ("P1W5-CT-11 RETURNED Idea resubmits successfully", ReturnedSubmit),
    ("P1W5-CT-12 Idea state and version mutate exactly", IdeaStateVersion),
    ("P1W5-CT-13 submission marker and timestamp are server stamped", SubmissionStamp),
    ("P1W5-CT-14 exactly one ACTIVE evaluation plan is bound to resulting Idea version", EvaluationPlanCreated),
    ("P1W5-CT-15 mandatory evaluator assignments are exact", MandatoryAssignments),
    ("P1W5-CT-16 all activated specialist assignments are created", SpecialistAssignments),
    ("P1W5-CT-17 inactive specialist roles are omitted", SpecialistOmission),
    ("P1W5-CT-18 plan and assignment identifiers are distinct server identities", ServerGeneratedIds),
    ("P1W5-CT-19 every assignment is bound to exact plan and Idea version", AssignmentVersionLink),
    ("P1W5-CT-20 every assignment scope equals authoritative Idea scope", AssignmentScope),
    ("P1W5-CT-21 aggregate routing does not grant a single evaluator role", NoRoutingGrant),
    ("P1W5-CT-22 exact post-freeze Domain Event is emitted", EventExact),
    ("P1W5-CT-23 event payload contains required minimized identifiers", EventPayloadRequired),
    ("P1W5-CT-24 event payload excludes forbidden dossier fields", EventPayloadMinimized),
    ("P1W5-CT-25 audit retains Idea Owner authority context", AuditContext),
    ("P1W5-CT-26 initial submission creates no G04 decision record", NoDecisionRecord),
    ("P1W5-CT-27 forged body cannot choose state plan assignment routing or event", ForgedBodyIgnored),
    ("P1W5-CT-28 wrong role is denied before mutation", WrongRoleDenied),
    ("P1W5-CT-29 actor outside authoritative Idea scope is denied", WrongScopeDenied),
    ("P1W5-CT-30 passport below 70 percent is denied", PassportDenied),
    ("P1W5-CT-31 inactive strategy link is denied", StrategyDenied),
    ("P1W5-CT-32 primary objective not ready is denied", ObjectiveDenied),
    ("P1W5-CT-33 invalid specialist profile flag is denied", ProfileFlagDenied),
    ("P1W5-CT-34 stale expected version is denied", StaleVersionDenied),
    ("P1W5-CT-35 exact replay creates no duplicate plan assignment or evidence", ReplayNoDuplicates),
    ("P1W5-CT-36 fault after plan staging rolls back complete transaction", () => RollbackAt(PersistenceFaultPoint.AfterEvaluationPlanStaged)),
    ("P1W5-CT-37 fault after assignment staging rolls back complete transaction", () => RollbackAt(PersistenceFaultPoint.AfterEvaluationAssignmentsStaged)),
    ("P1W5-CT-38 P2 logical contract explicitly supports plan assignment atomicity", PersistenceContractExtended)
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

Task AcrIdentity()
{
    Eq("ACR-P0-004", S(acr, "acrId"));
    Eq("APPROVED_FOR_P1_IMPLEMENTATION", S(acr, "status"));
    Eq("POST_FREEZE_ARCHITECTURE_DECISION", S(acr, "decisionClass"));
    Eq("EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html", S(acr.GetProperty("frozenProduct"), "file"));
    False(B(acr.GetProperty("frozenProduct"), "modifiedByThisDecision"));
    return Task.CompletedTask;
}

Task Wave5ArtifactIdentity()
{
    Eq("EIMS-P1-RECOVERY-WAVE5-IDEA-SUBMISSION-REBASELINE-1.0", S(wave5, "schema"));
    Eq("APPROVED_REBASELINE_FOR_P1_IDEA_SUBMISSION_BINDING", S(wave5, "status"));
    Eq("RECOVERY_REBASELINE_ACCEPTANCE", S(wave5, "decisionClass"));
    var source = wave5.GetProperty("sourceDecision");
    Eq("ACR-P0-004", S(source, "acrId"));
    False(B(source, "historicalOriginalTransitionCatalogRecovered"));
    False(B(source, "historicalOriginalEventIdentityRecovered"));
    return Task.CompletedTask;
}

Task Wave5Safety()
{
    var safety = wave5.GetProperty("runtimeSafety");
    True(B(safety, "historicalWave4CatalogPreserved"));
    False(B(safety, "p5CommandGatewayBound"));
    False(B(safety, "physicalOracleReadyClaimed"));
    False(B(safety, "liveWindowsDomainReadyClaimed"));
    False(B(safety, "networkPilotReadyClaimed"));
    True(B(safety, "v6360Unchanged"));
    return Task.CompletedTask;
}

Task HistoricalCatalogPreserved()
{
    var catalog = new RecoveredApiCommandCatalog();
    Eq(2, catalog.All.Count(x => x.MutationContractRecovered));
    False(catalog.All.Single(x => x.CommandName == "ideas.submit-g04").MutationContractRecovered);
    return Task.CompletedTask;
}

Task Wave5CatalogPromotion()
{
    var catalog = new RecoveredApiCommandCatalogWave5();
    Eq(21, catalog.All.Count);
    Eq(3, catalog.All.Count(x => x.MutationContractRecovered));
    Eq(3, RecoveredApiCommandCatalogWave5.Wave5RecoveredMutationCommandCount);
    True(catalog.All.Where(x => x.MutationContractRecovered).Select(x => x.CommandName).OrderBy(x => x).SequenceEqual(
        new[] { "ideas.submit-g04", "needs.g03-decision", "needs.submit-g03" }));
    return Task.CompletedTask;
}

Task IdeaPolicyExact()
{
    var catalog = new RecoveredApiCommandCatalogWave5();
    True(catalog.TryGet("ideas.submit-g04", out var policy));
    Seq(new[] { "IDEA_OWNER" }, policy.RequiredRoles);
    Seq(new[] { "DRAFT", "RETURNED" }, policy.AllowedStates);
    Eq("P1-IDEA-SUBMIT-G04-REBASELINE-1.0", policy.RuleSet);
    Eq("IdeaSubmittedForEvaluation.v1", policy.ResolveEventName()!);
    True(policy.StateContractRecovered && policy.RuleContractRecovered && policy.EventContractRecovered && policy.MutationContractRecovered);
    return Task.CompletedTask;
}

async Task G04VoteStillClosed()
{
    var (kernel, _) = Runtime(Idea());
    var result = await kernel.ExecuteAsync(new AuthorityCommand("g04.vote", "IDEA-1", 5, "VOTE-1", "CORR-VOTE", "{}"), Actor("G04_COMMITTEE_MEMBER"));
    Eq(503, result.HttpStatus); Eq("P1_STATE_CONTRACT_NOT_RECOVERED", result.Code); False(result.StateMutated);
}

async Task G04FinalStillClosed()
{
    var underReview = Idea(state: "UNDER_REVIEW");
    var (kernel, _) = Runtime(underReview);
    var result = await kernel.ExecuteAsync(new AuthorityCommand("g04.final-decision", "IDEA-1", 5, "FINAL-1", "CORR-FINAL", "{}"), Actor("IDEA_DECISION"));
    Eq(503, result.HttpStatus); Eq("P1_RULE_CONTRACT_NOT_RECOVERED", result.Code); False(result.StateMutated);
}

async Task P5GatewayStillClosed()
{
    ICommandGateway gateway = new FailClosedCommandGateway();
    var result = await gateway.ExecuteAsync(new CommandAttempt("ideas.submit-g04", "DOMAIN\\idea.owner", "CORR-P5", 5, "P5-1", "{}"));
    Eq(503, result.HttpStatus); Eq("P5_COMMAND_GATEWAY_NOT_BOUND", result.Code); False(result.StateMutated);
}

async Task DraftSubmit()
{
    var (kernel, store) = Runtime(Idea());
    var result = await kernel.ExecuteAsync(Submit(), Actor());
    Eq(200, result.HttpStatus); Eq("P2_ATOMIC_COMMIT", result.Code); True(result.StateMutated);
    Eq(1, store.EvaluationPlans.Count); Eq(2, store.EvaluationAssignments.Count);
}

async Task ReturnedSubmit()
{
    var (kernel, store) = Runtime(Idea(state: "RETURNED"));
    var result = await kernel.ExecuteAsync(Submit(), Actor());
    Eq(200, result.HttpStatus); Eq("UNDER_REVIEW", (await store.GetAggregateAsync("IDEA-1"))!.State);
}

async Task IdeaStateVersion()
{
    var (kernel, store) = Runtime(Idea());
    await kernel.ExecuteAsync(Submit(), Actor());
    var after = (await store.GetAggregateAsync("IDEA-1"))!;
    Eq("UNDER_REVIEW", after.State); Eq(6L, after.Version);
}

async Task SubmissionStamp()
{
    var (kernel, store) = Runtime(Idea());
    await kernel.ExecuteAsync(Submit(), Actor());
    var after = (await store.GetAggregateAsync("IDEA-1"))!;
    Eq("UNDER_REVIEW", Fact(after, "g04SubmissionStatus"));
    Eq("ACTIVE", Fact(after, "evaluationPlanStatus"));
    True(DateTimeOffset.TryParse(Fact(after, "g04SubmittedAtUtc"), out _));
}

async Task EvaluationPlanCreated()
{
    var (kernel, store) = Runtime(Idea());
    await kernel.ExecuteAsync(Submit(), Actor());
    var plan = store.EvaluationPlans.Single();
    Eq("IDEA-1", plan.IdeaId); Eq(6L, plan.IdeaVersion); Eq(1, plan.PlanVersion); Eq("ACTIVE", plan.State);
}

async Task MandatoryAssignments()
{
    var (kernel, store) = Runtime(Idea());
    await kernel.ExecuteAsync(Submit(), Actor());
    var roles = store.EvaluationAssignments.Select(x => x.Role).OrderBy(x => x).ToArray();
    Seq(new[] { "IDEA_EVALUATOR", "UNIT_OWNER_REVIEWER" }, roles);
    True(store.EvaluationAssignments.All(x => x.Required && x.State == "PENDING"));
}

async Task SpecialistAssignments()
{
    var facts = GoodFacts(technical: true, hse: true, financial: true, it: true);
    var (kernel, store) = Runtime(Idea(facts: facts));
    await kernel.ExecuteAsync(Submit(), Actor());
    Eq(6, store.EvaluationAssignments.Count);
    var roles = store.EvaluationAssignments.Select(x => x.Role).ToHashSet(StringComparer.OrdinalIgnoreCase);
    True(new[] { "IDEA_EVALUATOR", "UNIT_OWNER_REVIEWER", "TECHNICAL_ASSESSOR", "HSE_ASSESSOR", "FINANCIAL_ASSESSOR", "IT_ASSESSOR" }.All(roles.Contains));
}

async Task SpecialistOmission()
{
    var facts = GoodFacts(technical: true, hse: false, financial: false, it: true);
    var (kernel, store) = Runtime(Idea(facts: facts));
    await kernel.ExecuteAsync(Submit(), Actor());
    var roles = store.EvaluationAssignments.Select(x => x.Role).ToHashSet(StringComparer.OrdinalIgnoreCase);
    True(roles.Contains("TECHNICAL_ASSESSOR") && roles.Contains("IT_ASSESSOR"));
    False(roles.Contains("HSE_ASSESSOR")); False(roles.Contains("FINANCIAL_ASSESSOR"));
}

async Task ServerGeneratedIds()
{
    var (kernel, store) = Runtime(Idea(facts: GoodFacts(technical: true)));
    await kernel.ExecuteAsync(Submit(), Actor());
    var plan = store.EvaluationPlans.Single();
    True(plan.PlanId.StartsWith("EPLAN-", StringComparison.Ordinal));
    var ids = store.EvaluationAssignments.Select(x => x.AssignmentId).ToArray();
    True(ids.All(x => x.StartsWith("EASG-", StringComparison.Ordinal)));
    Eq(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
}

async Task AssignmentVersionLink()
{
    var (kernel, store) = Runtime(Idea(facts: GoodFacts(technical: true)));
    await kernel.ExecuteAsync(Submit(), Actor());
    var plan = store.EvaluationPlans.Single();
    True(store.EvaluationAssignments.All(x => x.PlanId == plan.PlanId && x.IdeaId == "IDEA-1" && x.IdeaVersion == 6));
}

async Task AssignmentScope()
{
    var (kernel, store) = Runtime(Idea());
    await kernel.ExecuteAsync(Submit(), Actor());
    True(store.EvaluationAssignments.All(x => x.Scope == "UNIT:RND"));
}

async Task NoRoutingGrant()
{
    var (kernel, store) = Runtime(Idea());
    await kernel.ExecuteAsync(Submit(), Actor());
    var after = (await store.GetAggregateAsync("IDEA-1"))!;
    True(after.WorkRoutingRole is null);
    Eq("IDEA_OWNER", after.OwnerRole!);
}

async Task EventExact()
{
    var (kernel, store) = Runtime(Idea());
    var result = await kernel.ExecuteAsync(Submit(), Actor());
    Seq(new[] { "IdeaSubmittedForEvaluation.v1" }, result.EmittedEvents);
    Eq("IdeaSubmittedForEvaluation.v1", store.Outbox.Single().EventName);
}

async Task EventPayloadRequired()
{
    var (kernel, store) = Runtime(Idea());
    await kernel.ExecuteAsync(Submit(), Actor());
    var payload = store.Outbox.Single().Payload!;
    var required = Strings(acr.GetProperty("eventContract"), "requiredPayloadFields");
    True(required.All(payload.ContainsKey));
    Eq("IDEA-1", payload["ideaId"]); Eq("6", payload["ideaVersion"]);
    True(payload["evaluationPlanId"].StartsWith("EPLAN-", StringComparison.Ordinal));
    Eq("1", payload["evaluationPlanVersion"]);
    Eq(2, payload["requiredAssignmentIds"].Split('|').Length);
    Eq(2, payload["requiredAssignmentRoles"].Split('|').Length);
}

async Task EventPayloadMinimized()
{
    var (kernel, store) = Runtime(Idea());
    await kernel.ExecuteAsync(Submit(), Actor());
    var payload = store.Outbox.Single().Payload!;
    var forbidden = Strings(acr.GetProperty("eventContract"), "forbiddenPayloadFields");
    True(forbidden.All(x => !payload.ContainsKey(x)));
}

async Task AuditContext()
{
    var (kernel, store) = Runtime(Idea());
    await kernel.ExecuteAsync(Submit(), Actor());
    var audit = store.AuditLog.Single();
    Eq("P-IDEA", audit.PersonId); Eq("DOMAIN\\idea.owner", audit.NetworkIdentity); Eq("ASG-IDEA", audit.Assignment);
    True(audit.Roles.Contains("IDEA_OWNER")); Eq("P1-IDEA-SUBMIT-G04-REBASELINE-1.0", audit.RuleSet); Eq(6L, audit.EntityVersion);
}

async Task NoDecisionRecord()
{
    var (kernel, store) = Runtime(Idea());
    await kernel.ExecuteAsync(Submit(), Actor());
    Eq(0, store.DomainDecisions.Count);
}

async Task ForgedBodyIgnored()
{
    var body = "{\"state\":\"APPROVED\",\"evaluationPlanId\":\"EVIL-PLAN\",\"assignmentId\":\"EVIL-ASG\",\"role\":\"ADMIN\",\"scope\":\"GLOBAL\",\"eventType\":\"Evil.v1\",\"passportCompletionPercent\":0}";
    var (kernel, store) = Runtime(Idea());
    var result = await kernel.ExecuteAsync(Submit(body: body), Actor());
    Eq(200, result.HttpStatus);
    var after = (await store.GetAggregateAsync("IDEA-1"))!;
    Eq("UNDER_REVIEW", after.State); Eq("UNIT:RND", after.Scope!);
    True(store.EvaluationPlans.Single().PlanId != "EVIL-PLAN");
    True(store.EvaluationAssignments.All(x => x.AssignmentId != "EVIL-ASG" && x.Role != "ADMIN"));
    Eq("IdeaSubmittedForEvaluation.v1", store.Outbox.Single().EventName);
}

async Task WrongRoleDenied()
{
    var (kernel, store) = Runtime(Idea());
    var result = await kernel.ExecuteAsync(Submit(), Actor("FINANCIAL_ASSESSOR"));
    Eq(403, result.HttpStatus); Eq("P1_ROLE_SCOPE_DENIED", result.Code); await Pristine(store);
}

async Task WrongScopeDenied()
{
    var (kernel, store) = Runtime(Idea());
    var actor = new AuthorityActor("P-IDEA", "DOMAIN\\idea.owner", "WINDOWS_PRINCIPAL", "ASG-IDEA", new[] { "IDEA_OWNER" }, new[] { "UNIT:FIN" });
    var result = await kernel.ExecuteAsync(Submit(), actor);
    Eq(403, result.HttpStatus); Eq("P1_ROLE_SCOPE_DENIED", result.Code); await Pristine(store);
}

async Task PassportDenied()
{
    var facts = GoodFacts(); facts["passportCompletionPercent"] = "69.9";
    var (kernel, store) = Runtime(Idea(facts: facts));
    var result = await kernel.ExecuteAsync(Submit(), Actor());
    Eq(422, result.HttpStatus); Eq("IDEA_SUBMIT_PASSPORT_BELOW_70", result.Code); await Pristine(store);
}

async Task StrategyDenied()
{
    var facts = GoodFacts(); facts["strategyLinkActive"] = "NO";
    var (kernel, store) = Runtime(Idea(facts: facts));
    var result = await kernel.ExecuteAsync(Submit(), Actor());
    Eq(422, result.HttpStatus); Eq("IDEA_SUBMIT_STRATEGY_LINK_INACTIVE", result.Code); await Pristine(store);
}

async Task ObjectiveDenied()
{
    var facts = GoodFacts(); facts["primaryObjectiveReady"] = "NO";
    var (kernel, store) = Runtime(Idea(facts: facts));
    var result = await kernel.ExecuteAsync(Submit(), Actor());
    Eq(422, result.HttpStatus); Eq("IDEA_SUBMIT_PRIMARY_OBJECTIVE_NOT_READY", result.Code); await Pristine(store);
}

async Task ProfileFlagDenied()
{
    var facts = GoodFacts(); facts["requiresFinancialEvaluation"] = "MAYBE";
    var (kernel, store) = Runtime(Idea(facts: facts));
    var result = await kernel.ExecuteAsync(Submit(), Actor());
    Eq(422, result.HttpStatus); Eq("IDEA_SUBMIT_PROFILE_FACT_INVALID", result.Code); await Pristine(store);
}

async Task StaleVersionDenied()
{
    var (kernel, store) = Runtime(Idea());
    var result = await kernel.ExecuteAsync(Submit(expected: 4), Actor());
    Eq(409, result.HttpStatus); Eq("P1_VERSION_CONFLICT", result.Code); await Pristine(store);
}

async Task ReplayNoDuplicates()
{
    var (kernel, store) = Runtime(Idea());
    var command = Submit();
    var first = await kernel.ExecuteAsync(command, Actor());
    var replay = await kernel.ExecuteAsync(command, Actor());
    True(first.StateMutated); True(replay.IdempotentReplay); False(replay.StateMutated);
    Eq(1, store.EvaluationPlans.Count); Eq(2, store.EvaluationAssignments.Count); Eq(1, store.AuditLog.Count); Eq(1, store.Outbox.Count); Eq(1, store.IdempotencyRecords.Count);
}

async Task RollbackAt(PersistenceFaultPoint point)
{
    var (kernel, store) = Runtime(Idea());
    store.FaultPoint = point;
    var threw = false;
    try { await kernel.ExecuteAsync(Submit(), Actor()); }
    catch (PersistenceAtomicityException) { threw = true; }
    True(threw); await Pristine(store);
}

Task PersistenceContractExtended()
{
    var contract = PersistenceContractDescriptor.RecoveryBaseline();
    True(contract.AtomicEvaluationPlanAssignmentsSupported);
    True(contract.IsLogicalContractReady);
    False(contract.IsPhysicalOracleReady);
    return Task.CompletedTask;
}

static AuthorityActor Actor(string role = "IDEA_OWNER") =>
    new("P-IDEA", "DOMAIN\\idea.owner", "WINDOWS_PRINCIPAL", "ASG-IDEA", new[] { role }, new[] { "UNIT:RND" });

static AuthorityCommand Submit(long expected = 5, string key = "IDEA-SUBMIT-1", string body = "{}") =>
    new("ideas.submit-g04", "IDEA-1", expected, key, "CORR-IDEA-1", body, "UNIT:RND");

static Dictionary<string, string> GoodFacts(bool technical = false, bool hse = false, bool financial = false, bool it = false) =>
    new(StringComparer.OrdinalIgnoreCase)
    {
        ["passportCompletionPercent"] = "80",
        ["strategyLinkActive"] = "YES",
        ["primaryObjectiveReady"] = "YES",
        ["requiresTechnicalEvaluation"] = technical ? "YES" : "NO",
        ["requiresHseEvaluation"] = hse ? "YES" : "NO",
        ["requiresFinancialEvaluation"] = financial ? "YES" : "NO",
        ["requiresItEvaluation"] = it ? "YES" : "NO"
    };

static AggregateSnapshot Idea(string state = "DRAFT", long version = 5, IReadOnlyDictionary<string, string>? facts = null) =>
    new("IDEA-1", "Idea", state, version, "P-IDEA", "IDEA_OWNER", "UNIT:RND", facts ?? GoodFacts(), "IDEA_OWNER");

static (AuthorityKernel Kernel, TransactionalAuthorityStore Store) Runtime(AggregateSnapshot idea)
{
    var store = new TransactionalAuthorityStore(PersistenceContractDescriptor.RecoveryBaseline(), idea);
    var kernel = new AuthorityKernel(
        new RecoveredApiCommandCatalogWave5(),
        store,
        new RecoveredWave5RuleEvaluator(),
        new BaselineSodEvaluator(),
        new RecoveredWave5MutationPlanner());
    return (kernel, store);
}

static async Task Pristine(TransactionalAuthorityStore store)
{
    var idea = (await store.GetAggregateAsync("IDEA-1"))!;
    Eq(5L, idea.Version); Eq(0, store.EvaluationPlans.Count); Eq(0, store.EvaluationAssignments.Count);
    Eq(0, store.DomainDecisions.Count); Eq(0, store.AuditLog.Count); Eq(0, store.Outbox.Count); Eq(0, store.IdempotencyRecords.Count);
}

static string Fact(AggregateSnapshot aggregate, string key) =>
    aggregate.RuleFacts!.First(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

static string S(JsonElement e, string p) =>
    e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
static bool B(JsonElement e, string p) => e.GetProperty(p).GetBoolean();
static HashSet<string> Strings(JsonElement e, string p) =>
    e.GetProperty(p).EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToHashSet(StringComparer.Ordinal);
static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
static void Eq<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
}
static void Seq<T>(IEnumerable<T> expected, IEnumerable<T> actual)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException("Sequences differ.");
}
