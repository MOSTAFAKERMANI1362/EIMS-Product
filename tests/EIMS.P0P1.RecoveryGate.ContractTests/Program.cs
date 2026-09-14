using System.Text.Json;
using EIMS.Authority.Recovery;
using EIMS.P0.MachineRecovery;

if (args.Length != 5 || args.Any(x => !File.Exists(x)))
{
    Console.Error.WriteLine("Usage: EIMS.P0P1.RecoveryGate.ContractTests <P0> <P1-wave1> <P1-wave2> <P1-wave3> <P1-wave4>");
    return 2;
}

using var p0Document = JsonDocument.Parse(File.ReadAllText(args[0]));
using var wave1Document = JsonDocument.Parse(File.ReadAllText(args[1]));
using var wave2Document = JsonDocument.Parse(File.ReadAllText(args[2]));
using var wave3Document = JsonDocument.Parse(File.ReadAllText(args[3]));
using var wave4Document = JsonDocument.Parse(File.ReadAllText(args[4]));

var p0Root = p0Document.RootElement;
var p0Catalog = p0Root.GetProperty("commandCatalog");
var p0Commands = p0Catalog.GetProperty("commands").EnumerateArray()
    .ToDictionary(x => x.GetProperty("id").GetString()!, StringComparer.OrdinalIgnoreCase);
var wave1 = wave1Document.RootElement;
var acceptedStates = wave1.GetProperty("acceptedStateContracts").EnumerateArray()
    .ToDictionary(x => x.GetProperty("command").GetString()!, StringComparer.OrdinalIgnoreCase);
var wave2 = wave2Document.RootElement;
var acceptedRules = wave2.GetProperty("ruleContracts").EnumerateArray()
    .ToDictionary(x => x.GetProperty("command").GetString()!, StringComparer.OrdinalIgnoreCase);
var wave3 = wave3Document.RootElement;
var acceptedEvents = wave3.GetProperty("acceptedEventContracts").EnumerateArray()
    .ToDictionary(x => x.GetProperty("command").GetString()!, StringComparer.OrdinalIgnoreCase);
var wave4 = wave4Document.RootElement;
var acceptedMutations = wave4.GetProperty("acceptedMutationContracts").EnumerateArray().ToArray();

var runtimeCatalog = new RecoveredApiCommandCatalog();
var results = new List<(string Id, string Name, bool Pass)>();
void Add(string id, string name, bool pass) => results.Add((id, name, pass));
string S(JsonElement e, string p) => e.GetProperty(p).GetString() ?? string.Empty;
bool B(JsonElement e, string p) => e.GetProperty(p).GetBoolean();

Add("P0P1-CT-01", "P0 machine recovery contract is internally valid", P0MachineRecoveryValidator.IsValid(File.ReadAllText(args[0])));
Add("P0P1-CT-02", "runtime P1 recovered catalog contains exactly 21 commands", runtimeCatalog.All.Count == 21 && RecoveredApiCommandCatalog.RecoveredApiCommandCount == 21);
Add("P0P1-CT-03", "runtime P1 retains historical declared count 28", RecoveredApiCommandCatalog.CompletionReviewDeclaredCommandCount == 28 && !runtimeCatalog.IsCatalogComplete);
Add("P0P1-CT-04", "P0 retains explicit 28/21/7 discrepancy", p0Catalog.GetProperty("originalP1CompletionReviewCount").GetInt32() == 28 && p0Catalog.GetProperty("recoveredP0ApiCommandCount").GetInt32() == 21 && p0Catalog.GetProperty("unidentifiedOriginalCommandCount").GetInt32() == 7);
Add("P0P1-CT-05", "the seven unidentified original command identities remain null", p0Catalog.GetProperty("unidentifiedOriginalCommandIds").ValueKind == JsonValueKind.Null && p0Catalog.GetProperty("mustNotInferMissingCommandIdentities").GetBoolean());

var runtimeIds = runtimeCatalog.All.Select(x => x.CommandName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
var p0Ids = p0Commands.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
Add("P0P1-CT-06", "P0 and P1 command IDs match exactly", runtimeIds.SequenceEqual(p0Ids, StringComparer.OrdinalIgnoreCase));

var rolesMatch = runtimeCatalog.All.All(policy =>
    p0Commands.TryGetValue(policy.CommandName, out var p0)
    && policy.RequiredRoles.Count == 1
    && string.Equals(policy.RequiredRoles.Single(), p0.GetProperty("requiredRole").GetString(), StringComparison.OrdinalIgnoreCase));
Add("P0P1-CT-07", "P0 and P1 required roles match command-by-command", rolesMatch);

var p0AllFailClosed = p0Commands.Values.All(x =>
    x.GetProperty("recoveryStatus").GetString() == "PARTIAL_EVIDENCE"
    && !x.GetProperty("completeStateContract").GetBoolean()
    && !x.GetProperty("completeRuleContract").GetBoolean()
    && !x.GetProperty("completeEventContract").GetBoolean()
    && !x.GetProperty("executable").GetBoolean());
Add("P0P1-CT-08", "historical P0 recovery remains partial/non-executable", p0AllFailClosed);

Add("P0P1-CT-09", "Wave 1 rebaseline acceptance is explicit and non-mutating",
    S(wave1,"status") == "APPROVED_REBASELINE_FOR_P1_STATE_BINDING"
    && S(wave1,"decisionClass") == "RECOVERY_REBASELINE_ACCEPTANCE"
    && wave1.GetProperty("safety").GetProperty("doesNotEnableProductMutation").GetBoolean());
Add("P0P1-CT-10", "exactly six Wave 1 state contracts are accepted", acceptedStates.Count == 6 && acceptedStates.Count == RecoveredApiCommandCatalog.Wave1StateBoundCommandCount);

var stateBindingMatches = runtimeCatalog.All.All(policy =>
{
    if (!acceptedStates.TryGetValue(policy.CommandName, out var item))
        return !policy.StateContractRecovered && policy.AllowedStates.Count == 0;
    var expected = item.GetProperty("allowedStates").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    return policy.StateContractRecovered && policy.AllowedStates.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).SequenceEqual(expected, StringComparer.OrdinalIgnoreCase);
});
Add("P0P1-CT-11", "runtime state binding matches Wave 1 exactly", stateBindingMatches);

var wave2Safety = wave2.GetProperty("runtimeSafety");
Add("P0P1-CT-12", "Wave 2 G03 rule acceptance remains provenance-safe and historically non-mutating",
    S(wave2,"status") == "APPROVED_REBASELINE_FOR_P1_RULE_BINDING"
    && S(wave2,"decisionClass") == "RECOVERY_REBASELINE_ACCEPTANCE"
    && !B(wave2Safety,"eventContractsRecovered")
    && !B(wave2Safety,"mutationContractsRecovered")
    && !B(wave2Safety,"productMutationEnabled")
    && B(wave2Safety,"ruleEvaluationMustUseAuthoritativeAggregateFacts")
    && B(wave2Safety,"requestBodyMayNotOverridePersistedNeedDefinition"));
Add("P0P1-CT-13", "exactly two G03 RuleSets are accepted", acceptedRules.Count == 2 && acceptedRules.Count == RecoveredApiCommandCatalog.Wave2RuleBoundCommandCount);
var ruleBindingMatches = runtimeCatalog.All.All(policy =>
{
    if (!acceptedRules.TryGetValue(policy.CommandName, out var item)) return !policy.RuleContractRecovered;
    return policy.RuleContractRecovered
        && string.Equals(policy.RuleSet, item.GetProperty("ruleSet").GetString(), StringComparison.Ordinal)
        && policy.StateContractRecovered;
});
Add("P0P1-CT-14", "runtime rule binding matches Wave 2 exactly", ruleBindingMatches);

var wave3Safety = wave3.GetProperty("safety");
Add("P0P1-CT-15", "Wave 3 event acceptance remains provenance-safe and historically non-mutating",
    S(wave3,"status") == "APPROVED_REBASELINE_FOR_P1_EVENT_BINDING"
    && S(wave3,"decisionClass") == "RECOVERY_REBASELINE_ACCEPTANCE"
    && S(wave3.GetProperty("sourceDecision"),"acrId") == "ACR-P0-002"
    && S(wave3.GetProperty("sourceDecision"),"decisionClass") == "POST_FREEZE_ARCHITECTURE_DECISION"
    && B(wave3Safety,"eventContractRecoveredForAcceptedCommands")
    && B(wave3Safety,"mutationContractsRemainUnrecovered")
    && B(wave3Safety,"doesNotEnableProductMutation")
    && B(wave3Safety,"doesNotEmitEventsByRebaselineAlone")
    && B(wave3Safety,"outcomeAwareDecisionRequiresExplicitOutcome")
    && B(wave3Safety,"noFallbackEventForUnknownOutcome")
    && B(wave3Safety,"noHistoricalOriginalClaim")
    && B(wave3Safety,"v6360Unchanged"));
Add("P0P1-CT-16", "exactly two G03 event contracts are accepted", acceptedEvents.Count == 2 && acceptedEvents.Count == RecoveredApiCommandCatalog.Wave3EventBoundCommandCount);
var eventBindingMatches = runtimeCatalog.All.All(policy =>
{
    if (!acceptedEvents.TryGetValue(policy.CommandName, out var item))
        return !policy.EventContractRecovered && policy.EventBinding is null;

    if (!policy.EventContractRecovered || policy.EventBinding is null || !policy.EventBinding.IsValid)
        return false;

    var kind = item.GetProperty("bindingKind").GetString();
    if (string.Equals(kind, "STATIC", StringComparison.Ordinal))
    {
        var expectedEvent = item.GetProperty("eventType").GetString();
        return string.Equals(policy.EventBinding.Kind, "STATIC", StringComparison.OrdinalIgnoreCase)
            && string.Equals(policy.ResolveEventName(), expectedEvent, StringComparison.Ordinal);
    }

    if (!string.Equals(kind, "OUTCOME", StringComparison.Ordinal)
        || !string.Equals(policy.EventBinding.Kind, "OUTCOME", StringComparison.OrdinalIgnoreCase)
        || policy.EventBinding.OutcomeEventNames is null)
        return false;

    var expectedOutcomes = item.GetProperty("outcomeEvents").EnumerateObject()
        .ToDictionary(x => x.Name, x => x.Value.GetString()!, StringComparer.OrdinalIgnoreCase);

    return expectedOutcomes.Count == policy.EventBinding.OutcomeEventNames.Count
        && expectedOutcomes.All(x => string.Equals(policy.ResolveEventName(x.Key), x.Value, StringComparison.Ordinal))
        && policy.ResolveEventName() is null
        && policy.ResolveEventName("UNKNOWN") is null;
});
Add("P0P1-CT-17", "runtime event binding matches Wave 3 exactly", eventBindingMatches);

var wave4Safety = wave4.GetProperty("runtimeSafety");
Add("P0P1-CT-18", "Wave 4 mutation acceptance is explicit post-freeze rebaseline",
    S(wave4,"schema") == "EIMS-P1-RECOVERY-WAVE4-G03-MUTATION-REBASELINE-1.0"
    && S(wave4,"status") == "APPROVED_REBASELINE_FOR_P1_MUTATION_BINDING"
    && S(wave4,"decisionClass") == "RECOVERY_REBASELINE_ACCEPTANCE"
    && S(wave4.GetProperty("sourceDecision"),"acrId") == "ACR-P0-003"
    && !B(wave4.GetProperty("sourceDecision"),"historicalOriginalMutationCatalogRecovered"));
Add("P0P1-CT-19", "Wave 4 preserves frozen v6.360", S(wave4.GetProperty("frozenProduct"),"file") == "EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html" && !B(wave4.GetProperty("frozenProduct"),"modifiedByThisRebaseline"));
Add("P0P1-CT-20", "Wave 4 mutation outcomes are exactly submit approve return", acceptedMutations.Length == 3 && acceptedMutations.Count(x => S(x,"command") == "needs.submit-g03") == 1 && acceptedMutations.Count(x => S(x,"command") == "needs.g03-decision") == 2);
Add("P0P1-CT-21", "runtime promotes exactly two G03 command mutation contracts", runtimeCatalog.All.Count(x => x.MutationContractRecovered) == RecoveredApiCommandCatalog.Wave4MutationBoundCommandCount && runtimeCatalog.All.Where(x => x.MutationContractRecovered).Select(x => x.CommandName).OrderBy(x => x).SequenceEqual(new[]{"needs.g03-decision","needs.submit-g03"}));
Add("P0P1-CT-22", "Wave 4 keeps routing non-authoritative for RBAC and P5 closed", !B(wave4Safety,"workRoutingRoleHasAuthorizationEffect") && !B(wave4Safety,"p5CommandGatewayBound") && !B(wave4Safety,"networkPilotReadyClaimed"));

var dependencies = new MustNotBeTouchedDependencies();
var kernel = new AuthorityKernel(runtimeCatalog, dependencies, dependencies, dependencies, dependencies);
var actor = new AuthorityActor(
    "P-TEST", "DOMAIN\\test.user", "WINDOWS_PRINCIPAL", "A-TEST",
    new[] { "MATCH_ASSIGNMENT_ROLE", "INTAKE_STEWARD", "CASE_REVIEWER", "NEED_OWNER", "NEED_REVIEWER", "IDEA_OWNER", "G04_COMMITTEE_MEMBER", "IDEA_DECISION", "PORTFOLIO_MANAGER", "EXECUTION_OWNER", "EXECUTION_COMPLETION_REVIEWER", "BENEFIT_OWNER", "BENEFIT_OWNER_OR_AUTHORIZED_DATA_PROVIDER", "BENEFIT_VERIFIER", "KNOWLEDGE_STEWARD", "KNOWLEDGE_PUBLISHER", "REWARD_COMMITTEE" },
    new[] { "GLOBAL" });

var index = 1;
foreach (var policy in runtimeCatalog.All.Where(x => !x.MutationContractRecovered).OrderBy(x => x.CommandName, StringComparer.OrdinalIgnoreCase))
{
    var command = new AuthorityCommand(policy.CommandName,$"AGG-{index:00}",1,$"IDEMP-{index:00}",$"CORR-{index:00}","{}",null);
    AuthorityResult? result = null;
    var threw = false;
    try { result = await kernel.ExecuteAsync(command, actor); } catch { threw = true; }
    var expectedCode = !policy.StateContractRecovered ? "P1_STATE_CONTRACT_NOT_RECOVERED"
        : !policy.RuleContractRecovered ? "P1_RULE_CONTRACT_NOT_RECOVERED"
        : !policy.EventContractRecovered ? "P1_EVENT_CONTRACT_NOT_RECOVERED"
        : "P1_MUTATION_CONTRACT_NOT_RECOVERED";
    Add($"P0P1-CT-FC-{index:00}", $"{policy.CommandName} remains fail closed before dependencies", !threw && result is not null && result.HttpStatus == 503 && result.Code == expectedCode && !result.StateMutated);
    index++;
}

var unknownResult = await kernel.ExecuteAsync(new AuthorityCommand("unidentified.original.command.placeholder","AGG-X",1,"IDEMP-X","CORR-X","{}"),actor);
Add("P0P1-CT-50", "unidentified command placeholder remains unknown to P1", unknownResult.HttpStatus == 404 && unknownResult.Code == "P1_COMMAND_UNKNOWN" && !unknownResult.StateMutated);
Add("P0P1-CT-51", "all still-closed recovery gates stop before downstream dependencies", dependencies.TouchCount == 0);

foreach (var r in results) Console.WriteLine($"{(r.Pass ? "PASS" : "FAIL")} {r.Id} {r.Name}");
var passed = results.Count(x => x.Pass);
Console.WriteLine($"RESULT {passed}/{results.Count} PASS");
return passed == results.Count ? 0 : 1;

sealed class MustNotBeTouchedDependencies : IAuthorityStore, IRuleEvaluator, ISodEvaluator, ICommandMutationPlanner
{
    public int TouchCount { get; private set; }
    private Exception Touched(string member) { TouchCount++; return new InvalidOperationException($"Fail-closed boundary bypassed: {member}"); }
    public ValueTask<AggregateSnapshot?> GetAggregateAsync(string aggregateId, CancellationToken cancellationToken = default) => ValueTask.FromException<AggregateSnapshot?>(Touched(nameof(GetAggregateAsync)));
    public ValueTask<IdempotencyRecord?> GetIdempotencyAsync(string commandName, string aggregateId, string idempotencyKey, CancellationToken cancellationToken = default) => ValueTask.FromException<IdempotencyRecord?>(Touched(nameof(GetIdempotencyAsync)));
    public ValueTask<AuthorityResult> CommitAsync(MutationRequest request, MutationCommit commit, CancellationToken cancellationToken = default) => ValueTask.FromException<AuthorityResult>(Touched(nameof(CommitAsync)));
    public ValueTask<RuleEvaluation> EvaluateAsync(AuthorityCommand command, AuthorityActor actor, AggregateSnapshot aggregate, CommandPolicy policy, CancellationToken cancellationToken = default) => ValueTask.FromException<RuleEvaluation>(Touched(nameof(IRuleEvaluator.EvaluateAsync)));
    ValueTask<SodEvaluation> ISodEvaluator.EvaluateAsync(AuthorityCommand command, AuthorityActor actor, AggregateSnapshot aggregate, CommandPolicy policy, CancellationToken cancellationToken) => ValueTask.FromException<SodEvaluation>(Touched(nameof(ISodEvaluator.EvaluateAsync)));
    public ValueTask<MutationPlan?> PlanAsync(AuthorityCommand command, AuthorityActor actor, AggregateSnapshot aggregate, CommandPolicy policy, CancellationToken cancellationToken = default) => ValueTask.FromException<MutationPlan?>(Touched(nameof(PlanAsync)));
}
