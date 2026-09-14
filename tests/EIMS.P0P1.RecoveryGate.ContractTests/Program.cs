using System.Text.Json;
using EIMS.Authority.Recovery;
using EIMS.P0.MachineRecovery;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: EIMS.P0P1.RecoveryGate.ContractTests <P0-machine-recovery-json>");
    return 2;
}

var p0Json = File.ReadAllText(args[0]);
using var p0Document = JsonDocument.Parse(p0Json);
var root = p0Document.RootElement;
var p0Catalog = root.GetProperty("commandCatalog");
var p0Commands = p0Catalog.GetProperty("commands").EnumerateArray()
    .ToDictionary(x => x.GetProperty("id").GetString()!, StringComparer.OrdinalIgnoreCase);

var runtimeCatalog = new RecoveredApiCommandCatalog();
var results = new List<(string Id, string Name, bool Pass)>();
void Add(string id, string name, bool pass) => results.Add((id, name, pass));

Add("P0P1-CT-01", "P0 machine recovery contract is internally valid", P0MachineRecoveryValidator.IsValid(p0Json));
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
Add("P0P1-CT-08", "P0 partial evidence cannot represent an executable command", p0AllFailClosed);

var runtimeAllFailClosed = runtimeCatalog.All.All(x =>
    !x.StateContractRecovered
    && !x.RuleContractRecovered
    && string.Equals(x.RuleSet, "UNRECOVERED_RULESET", StringComparison.Ordinal)
    && string.Equals(x.EventName, "UNRECOVERED_EVENT_IDENTITY", StringComparison.Ordinal));
Add("P0P1-CT-09", "P1 runtime policies retain unrecovered state/rule/event sentinels", runtimeAllFailClosed);

var dependencies = new MustNotBeTouchedDependencies();
var kernel = new AuthorityKernel(runtimeCatalog, dependencies, dependencies, dependencies, dependencies);
var actor = new AuthorityActor(
    "P-TEST",
    "DOMAIN\\test.user",
    "WINDOWS_PRINCIPAL",
    "A-TEST",
    new[] { "MATCH_ASSIGNMENT_ROLE", "INTAKE_STEWARD", "CASE_REVIEWER", "NEED_OWNER", "NEED_REVIEWER", "IDEA_OWNER", "G04_COMMITTEE_MEMBER", "IDEA_DECISION", "PORTFOLIO_MANAGER", "EXECUTION_OWNER", "EXECUTION_COMPLETION_REVIEWER", "BENEFIT_OWNER", "BENEFIT_OWNER_OR_AUTHORIZED_DATA_PROVIDER", "BENEFIT_VERIFIER", "KNOWLEDGE_STEWARD", "KNOWLEDGE_PUBLISHER", "REWARD_COMMITTEE" },
    new[] { "GLOBAL" });

var sequence = 1;
foreach (var policy in runtimeCatalog.All.OrderBy(x => x.CommandName, StringComparer.OrdinalIgnoreCase))
{
    var command = new AuthorityCommand(
        policy.CommandName,
        $"AGG-{sequence:00}",
        1,
        $"IDEMP-{sequence:00}",
        $"CORR-{sequence:00}",
        "{}",
        null);

    AuthorityResult? result = null;
    var threw = false;
    try
    {
        result = await kernel.ExecuteAsync(command, actor);
    }
    catch
    {
        threw = true;
    }

    Add($"P0P1-CT-10-{sequence:00}",
        $"{policy.CommandName} fails closed before store/rules/SoD/planner",
        !threw
        && result is not null
        && result.HttpStatus == 503
        && result.Code == "P1_STATE_CONTRACT_NOT_RECOVERED"
        && !result.Allowed
        && !result.StateMutated);
    sequence++;
}

var unknownResult = await kernel.ExecuteAsync(
    new AuthorityCommand("unidentified.original.command.placeholder", "AGG-X", 1, "IDEMP-X", "CORR-X", "{}"),
    actor);
Add("P0P1-CT-31", "unidentified command placeholder remains unknown to P1", unknownResult.HttpStatus == 404 && unknownResult.Code == "P1_COMMAND_UNKNOWN" && !unknownResult.Allowed && !unknownResult.StateMutated);
Add("P0P1-CT-32", "fail-closed short-circuit touched no downstream dependency", dependencies.TouchCount == 0);

foreach (var result in results)
    Console.WriteLine($"{(result.Pass ? "PASS" : "FAIL")} {result.Id} {result.Name}");

var passed = results.Count(x => x.Pass);
Console.WriteLine($"RESULT {passed}/{results.Count} PASS");
return passed == results.Count ? 0 : 1;

sealed class MustNotBeTouchedDependencies : IAuthorityStore, IRuleEvaluator, ISodEvaluator, ICommandMutationPlanner
{
    public int TouchCount { get; private set; }

    private Exception Touched(string member)
    {
        TouchCount++;
        return new InvalidOperationException($"Fail-closed boundary was bypassed: {member}");
    }

    public ValueTask<AggregateSnapshot?> GetAggregateAsync(string aggregateId, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<AggregateSnapshot?>(Touched(nameof(GetAggregateAsync)));

    public ValueTask<IdempotencyRecord?> GetIdempotencyAsync(string commandName, string aggregateId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IdempotencyRecord?>(Touched(nameof(GetIdempotencyAsync)));

    public ValueTask<AuthorityResult> CommitAsync(MutationRequest request, MutationCommit commit, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<AuthorityResult>(Touched(nameof(CommitAsync)));

    public ValueTask<RuleEvaluation> EvaluateAsync(AuthorityCommand command, AuthorityActor actor, AggregateSnapshot aggregate, CommandPolicy policy, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<RuleEvaluation>(Touched(nameof(IRuleEvaluator.EvaluateAsync)));

    ValueTask<SodEvaluation> ISodEvaluator.EvaluateAsync(AuthorityCommand command, AuthorityActor actor, AggregateSnapshot aggregate, CommandPolicy policy, CancellationToken cancellationToken) =>
        ValueTask.FromException<SodEvaluation>(Touched(nameof(ISodEvaluator.EvaluateAsync)));

    public ValueTask<MutationPlan?> PlanAsync(AuthorityCommand command, AuthorityActor actor, AggregateSnapshot aggregate, CommandPolicy policy, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MutationPlan?>(Touched(nameof(PlanAsync)));
}
