using System.Text.Json;
using EIMS.Authority.Recovery;

if (args.Length != 3 || args.Any(x => !File.Exists(x)))
{
    Console.Error.WriteLine("Usage: EIMS.P0P1.Wave5RecoveryGate.ContractTests <P0-json> <ACR-P0-004-json> <P1-wave5-json>");
    return 2;
}

using var p0Doc = JsonDocument.Parse(File.ReadAllText(args[0]));
using var acrDoc = JsonDocument.Parse(File.ReadAllText(args[1]));
using var wave5Doc = JsonDocument.Parse(File.ReadAllText(args[2]));
var p0 = p0Doc.RootElement;
var acr = acrDoc.RootElement;
var wave5 = wave5Doc.RootElement;
var p0Commands = p0.GetProperty("commandCatalog").GetProperty("commands").EnumerateArray()
    .ToDictionary(x => S(x, "id"), StringComparer.OrdinalIgnoreCase);
var historical = new RecoveredApiCommandCatalog();
var runtime = new RecoveredApiCommandCatalogWave5();
var results = new List<(string Id, string Name, bool Pass)>();
void Add(string id, string name, bool pass) => results.Add((id, name, pass));

Add("P0P1W5-CT-01", "P0 recovered catalog still contains exactly 21 known command identities",
    p0Commands.Count == 21 && p0.GetProperty("commandCatalog").GetProperty("unidentifiedOriginalCommandCount").GetInt32() == 7);
Add("P0P1W5-CT-02", "P0 retains ideas.submit-g04 as partial non-executable evidence",
    p0Commands.TryGetValue("ideas.submit-g04", out var p0Idea)
    && S(p0Idea, "requiredRole") == "IDEA_OWNER"
    && S(p0Idea, "recoveryStatus") == "PARTIAL_EVIDENCE"
    && !B(p0Idea, "executable"));
Add("P0P1W5-CT-03", "ACR-P0-004 explicitly authorizes P1 implementation but not historical-original recovery",
    S(acr, "acrId") == "ACR-P0-004"
    && S(acr, "status") == "APPROVED_FOR_P1_IMPLEMENTATION"
    && S(acr, "decisionClass") == "POST_FREEZE_ARCHITECTURE_DECISION"
    && B(acr.GetProperty("provenance"), "mustNotBeRepresentedAsRecoveredOriginal"));
Add("P0P1W5-CT-04", "Wave 5 artifact consumes ACR-P0-004 without claiming original recovery",
    S(wave5, "status") == "APPROVED_REBASELINE_FOR_P1_IDEA_SUBMISSION_BINDING"
    && S(wave5.GetProperty("sourceDecision"), "acrId") == "ACR-P0-004"
    && !B(wave5.GetProperty("sourceDecision"), "historicalOriginalTransitionCatalogRecovered")
    && !B(wave5.GetProperty("sourceDecision"), "historicalOriginalEventIdentityRecovered"));
Add("P0P1W5-CT-05", "historical Wave 4 runtime evidence remains exactly two mutation commands",
    historical.All.Count(x => x.MutationContractRecovered) == 2
    && !historical.All.Single(x => x.CommandName == "ideas.submit-g04").MutationContractRecovered);
Add("P0P1W5-CT-06", "Wave 5 cumulative runtime keeps exact 21 known command identities",
    runtime.All.Count == 21
    && runtime.All.Select(x => x.CommandName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
        .SequenceEqual(p0Commands.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase));
Add("P0P1W5-CT-07", "P0 and Wave 5 runtime required roles remain aligned command by command",
    runtime.All.All(policy => p0Commands.TryGetValue(policy.CommandName, out var item)
        && policy.RequiredRoles.Count == 1
        && string.Equals(policy.RequiredRoles.Single(), S(item, "requiredRole"), StringComparison.OrdinalIgnoreCase)));
Add("P0P1W5-CT-08", "Wave 5 promotes only G03 plus Idea submission mutation contracts",
    runtime.All.Where(x => x.MutationContractRecovered).Select(x => x.CommandName).OrderBy(x => x)
        .SequenceEqual(new[] { "ideas.submit-g04", "needs.g03-decision", "needs.submit-g03" }));
var idea = runtime.All.Single(x => x.CommandName == "ideas.submit-g04");
Add("P0P1W5-CT-09", "Idea submission cumulative gates are all physically bound",
    idea.StateContractRecovered && idea.RuleContractRecovered && idea.EventContractRecovered && idea.MutationContractRecovered
    && idea.ResolveEventName() == "IdeaSubmittedForEvaluation.v1");
Add("P0P1W5-CT-10", "G04 voting and final decision are not promoted by Wave 5",
    runtime.All.Single(x => x.CommandName == "g04.vote").MutationContractRecovered == false
    && runtime.All.Single(x => x.CommandName == "g04.final-decision").MutationContractRecovered == false);
Add("P0P1W5-CT-11", "frozen v6.360 remains unchanged in both decision and rebaseline",
    S(acr.GetProperty("frozenProduct"), "file") == "EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html"
    && !B(acr.GetProperty("frozenProduct"), "modifiedByThisDecision")
    && S(wave5.GetProperty("frozenProduct"), "file") == "EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html"
    && !B(wave5.GetProperty("frozenProduct"), "modifiedByThisRebaseline"));

var dependencies = new MustNotBeTouchedDependencies();
var kernel = new AuthorityKernel(runtime, dependencies, dependencies, dependencies, dependencies);
var actor = new AuthorityActor(
    "P-TEST", "DOMAIN\\test.user", "WINDOWS_PRINCIPAL", "ASG-TEST",
    runtime.All.SelectMany(x => x.RequiredRoles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
    new[] { "GLOBAL" });
var index = 1;
var allClosedBeforeDependencies = true;
foreach (var policy in runtime.All.Where(x => !x.MutationContractRecovered))
{
    var result = await kernel.ExecuteAsync(
        new AuthorityCommand(policy.CommandName, $"AGG-{index}", 1, $"KEY-{index}", $"CORR-{index}", "{}"),
        actor);
    var expected = !policy.StateContractRecovered ? "P1_STATE_CONTRACT_NOT_RECOVERED"
        : !policy.RuleContractRecovered ? "P1_RULE_CONTRACT_NOT_RECOVERED"
        : !policy.EventContractRecovered ? "P1_EVENT_CONTRACT_NOT_RECOVERED"
        : "P1_MUTATION_CONTRACT_NOT_RECOVERED";
    allClosedBeforeDependencies &= result.HttpStatus == 503 && result.Code == expected && !result.StateMutated;
    index++;
}
Add("P0P1W5-CT-12", "every non-promoted Product command remains fail closed before downstream dependencies",
    allClosedBeforeDependencies && dependencies.TouchCount == 0);

foreach (var r in results)
    Console.WriteLine($"{(r.Pass ? "PASS" : "FAIL")} {r.Id} {r.Name}");
var passed = results.Count(x => x.Pass);
Console.WriteLine($"RESULT {passed}/{results.Count} PASS");
return passed == results.Count ? 0 : 1;

static string S(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
static bool B(JsonElement e, string p) => e.GetProperty(p).GetBoolean();

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
