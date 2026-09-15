using System.Text.Json;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("FAIL ACR008-00 ACR JSON path is required.");
    return 2;
}

using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
var root = doc.RootElement;
var pass = 0;
var fail = 0;

void Check(string id, bool ok, string message)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {id} {message}");
    if (ok) pass++; else fail++;
}

string Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False && p.GetBoolean();
JsonElement Obj(JsonElement e, string name) => e.GetProperty(name);
IEnumerable<JsonElement> Arr(JsonElement e, string name) => e.GetProperty(name).EnumerateArray();
JsonElement? FindByCode(IEnumerable<JsonElement> xs, string code) => xs.FirstOrDefault(x => Str(x, "code") == code) is var v && v.ValueKind != JsonValueKind.Undefined ? v : null;
bool ContainsString(IEnumerable<JsonElement> xs, string value) => xs.Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == value);

Check("ACR008-01", Str(root, "schema") == "EIMS-ARCHITECTURE-CHANGE-RECORD-1.0", "schema identity");
Check("ACR008-02", Str(root, "acrId") == "ACR-P0-008", "ACR identity");
Check("ACR008-03", Str(root, "status") == "APPROVED_FOR_P1_IMPLEMENTATION", "implementation decision status");
Check("ACR008-04", Str(Obj(root, "sourceProduct"), "sha256") == "057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a", "frozen v6.360 hash preserved");
Check("ACR008-05", !Bool(Obj(root, "sourceProduct"), "modifiedByThisDecision"), "v6.360 is not modified by ACR");

var grouped = Arr(root, "supersedesGroupedCommandBindings").ToArray();
Check("ACR008-06", grouped.Any(x => Str(x, "legacyCommand") == "portfolio.assign-accept" && Str(x, "decision") == "DO_NOT_IMPLEMENT_AS_SINGLE_MUTATION"), "portfolio grouped mutation prohibited");
Check("ACR008-07", grouped.Any(x => Str(x, "legacyCommand") == "executions.prepare" && Str(x, "decision") == "DO_NOT_IMPLEMENT_AS_SINGLE_MUTATION"), "execution grouped mutation prohibited");

var authority = Obj(root, "globalAuthorityRules");
Check("ACR008-08", Str(authority, "identitySource") == "P3_SERVER_RESOLVED_WINDOWS_IDENTITY", "server-resolved identity required");
Check("ACR008-09", Bool(authority, "assignmentContextRequired"), "assignment context required");
Check("ACR008-10", !Bool(authority, "clientSuppliedRoleOrScopeTrusted"), "client role/scope never trusted");
Check("ACR008-11", Bool(authority, "expectedVersionRequired") && Bool(authority, "idempotencyKeyRequired"), "version and idempotency required");
Check("ACR008-12", Str(authority, "transactionBoundary") == "STATE_DECISION_AUDIT_OUTBOX_IDEMPOTENCY_ATOMIC", "atomic authority transaction preserved");
Check("ACR008-13", Bool(authority, "failClosedOnMissingAuthorityOrState") && Bool(authority, "digitalThreadPreserved"), "fail-closed + digital thread preserved");

var trigger = Arr(root, "systemTriggers").SingleOrDefault(x => Str(x, "code") == "portfolio.evaluate-eligibility-from-approved-idea");
Check("ACR008-14", trigger.ValueKind != JsonValueKind.Undefined && Str(trigger, "trigger") == "IdeaApprovedForPortfolio.v1", "automatic eligibility trigger bound to approved idea event");
Check("ACR008-15", trigger.ValueKind != JsonValueKind.Undefined && !Bool(trigger, "userInvocable") && !Bool(trigger, "ordinaryUiManualAction"), "ordinary users cannot run PortfolioEligibilityService manually");
Check("ACR008-16", trigger.ValueKind != JsonValueKind.Undefined && Str(trigger, "effectOnPass").Contains("exactly one PortfolioIntakeCandidate", StringComparison.Ordinal), "eligibility creates exactly one candidate");

var commands = Arr(root, "commands").ToArray();
var codes = commands.Select(x => Str(x, "code")).ToHashSet(StringComparer.Ordinal);
Check("ACR008-17", !codes.Contains("portfolio.assign-accept") && !codes.Contains("executions.prepare"), "legacy grouped commands absent from stabilized command list");

string[] requiredCommands =
[
    "portfolio.assign-candidate", "portfolio.membership-decision", "portfolio.generate-execution-recommendation",
    "portfolio.approve-execution-recommendation", "portfolio.bind-approved-baseline", "portfolio.request-execution-handoff",
    "executions.approve-charter", "executions.approve-plan-baseline", "executions.start", "executions.progress",
    "executions.submit-completion", "executions.completion-review", "executions.request-benefit-handoff",
    "executions.begin-closure", "executions.close", "benefits.accept", "benefits.set-baseline",
    "benefits.approve-measurement-plan", "benefits.measure", "benefits.verify", "benefits.attribution",
    "benefits.realize", "benefits.close", "knowledge.create-draft", "knowledge.validate", "knowledge.publish"
];
Check("ACR008-18", requiredCommands.All(codes.Contains), "all required post-G04 commands stabilized");
Check("ACR008-19", commands.Where(x => Str(x, "code").StartsWith("portfolio.") && Str(x, "code") != "portfolio.evaluate-eligibility-from-approved-idea").All(x => Str(x, "authority") == "PORTFOLIO_MANAGER"), "Portfolio command authority is Portfolio Manager");

var membership = FindByCode(commands, "portfolio.membership-decision");
Check("ACR008-20", membership is { } m && ContainsString(Arr(m, "decisions"), "ACCEPTED") && ContainsString(Arr(m, "decisions"), "REJECTED") && ContainsString(Arr(m, "decisions"), "DEFERRED"), "membership decisions remain distinct");
Check("ACR008-21", membership is { } m2 && Str(Obj(m2, "eventsByDecision"), "ACCEPTED") == "PortfolioMembershipAccepted.v1" && Str(Obj(m2, "eventsByDecision"), "REJECTED") == "PortfolioMembershipRejected.v1" && Str(Obj(m2, "eventsByDecision"), "DEFERRED") == "PortfolioMembershipDeferred.v1", "authoritative membership events preserved");

var handoff = FindByCode(commands, "portfolio.request-execution-handoff");
Check("ACR008-22", handoff is { } h && ContainsString(Arr(h, "preconditions"), "recommendation.status=APPROVED") && ContainsString(Arr(h, "preconditions"), "baselineApproved=true"), "handoff requires approval + baseline");
Check("ACR008-23", handoff is { } h2 && ContainsString(Arr(h2, "events"), "ExecutionHandoffRequested.v1") && ContainsString(Arr(h2, "events"), "ExecutionCreatedFromRecommendation.v1"), "handoff events preserved");

var completion = FindByCode(commands, "executions.completion-review");
Check("ACR008-24", completion is { } c && Str(c, "authority") == "EXECUTION_COMPLETION_REVIEWER", "completion review authority is independent reviewer");
Check("ACR008-25", completion is { } c2 && ContainsString(Arr(c2, "sod"), "Execution Owner cannot approve own Completion"), "completion SoD preserved");

var progress = FindByCode(commands, "executions.progress");
Check("ACR008-26", progress is { } p && ContainsString(Arr(p, "constraints"), "progress=100 does not imply completion"), "100 percent is not completion");

var stateMachines = Obj(root, "stateMachines");
var executionStates = Arr(stateMachines, "execution").Select(x => x.GetString()).ToArray();
var benefitStates = Arr(stateMachines, "benefit").Select(x => x.GetString()).ToArray();
Check("ACR008-27", executionStates.SequenceEqual(new[] { "PLANNING", "ACTIVE", "COMPLETION_REVIEW", "COMPLETED", "CLOSURE_IN_PROGRESS", "CLOSED" }), "execution state sequence exact");
Check("ACR008-28", benefitStates.SequenceEqual(new[] { "OBLIGATION_PENDING_ACCEPTANCE", "BASELINE_REQUIRED", "PLAN_REQUIRED", "MEASUREMENT_PENDING", "MEASURED", "VERIFIED", "VALIDATED", "REALIZED", "CLOSED" }), "benefit state sequence exact");

var benefitClose = FindByCode(commands, "benefits.close");
Check("ACR008-29", benefitClose is { } bc && ContainsString(Arr(bc, "preconditions"), "Knowledge.status=PUBLISHED") && ContainsString(Arr(bc, "preconditions"), "knowledge.publicationDossier present"), "Benefit close requires published Knowledge evidence");

var knowledgeCreate = FindByCode(commands, "knowledge.create-draft");
var authorPolicy = knowledgeCreate is { } kc ? Obj(kc, "authorPolicy") : default;
Check("ACR008-30", knowledgeCreate is { } && Str(knowledgeCreate.Value, "authority") == "RESOLVED_KNOWLEDGE_AUTHOR_POLICY", "Knowledge author resolved by server policy");
Check("ACR008-31", authorPolicy.ValueKind == JsonValueKind.Object && Str(authorPolicy, "default") == "NEED_OWNER_OR_DOMAIN_EXPERT" && Bool(authorPolicy, "benefitOwnerAloneDoesNotGrantAuthorAuthority"), "legacy Benefit Owner author authority not promoted");

var validate = FindByCode(commands, "knowledge.validate");
var publish = FindByCode(commands, "knowledge.publish");
Check("ACR008-32", validate is { } kv && publish is { } kp && Str(kv, "authority") == "KNOWLEDGE_STEWARD" && Str(kp, "authority") == "KNOWLEDGE_PUBLISHER", "Knowledge validation/publication authorities separated");

var invariants = Arr(root, "invariants").ToArray();
Check("ACR008-33", ContainsString(invariants, "CANDIDATE_ASSIGNMENT_MEMBERSHIP_RECOMMENDATION_HANDOFF_DISTINCT"), "Portfolio concepts remain distinct");
Check("ACR008-34", ContainsString(invariants, "BENEFIT_MEASURE_VERIFY_ATTRIBUTION_REALIZE_DISTINCT") && ContainsString(invariants, "NON_FINANCIAL_BENEFIT_FIRST_CLASS"), "Benefit lifecycle and non-financial benefit preserved");
Check("ACR008-35", ContainsString(invariants, "LEGACY_BENEFIT_OWNER_IS_NOT_PRIMARY_KNOWLEDGE_AUTHOR_AUTHORITY"), "legacy author conflict explicitly blocked");

var runtimeGate = Obj(root, "runtimeGate");
Check("ACR008-36", Bool(runtimeGate, "p1ImplementationAllowedAfterAcrCiPass"), "P1 implementation gated by ACR CI");
Check("ACR008-37", Bool(runtimeGate, "networkPilotFinalPassRequiresRealOp04PilotEvidence"), "Network Pilot still requires real OP-04 evidence");
Check("ACR008-38", Bool(runtimeGate, "op05FinalPassCannotUseTestOnlyShortcut"), "OP-05 final PASS cannot use test-only shortcut");

Console.WriteLine($"RESULT {pass}/{pass + fail} PASS");
return fail == 0 ? 0 : 1;
