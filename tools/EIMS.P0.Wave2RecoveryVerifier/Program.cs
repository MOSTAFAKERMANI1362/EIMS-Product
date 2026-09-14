using System.Text.Json;

const string expectedSchema = "EIMS-P0-MACHINE-CONTRACT-RECOVERY-WAVE2-1.0";
const string expectedSha = "057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a";
const string defaultPath = "recovery/p0-wave2/P0_PORTFOLIO_KNOWLEDGE_RECOVERED_CONTRACT_v1.0.json";

var path = args.Length > 0 ? args[0] : defaultPath;
if (!File.Exists(path))
{
    Console.Error.WriteLine($"FAIL P0W2-00 recovery contract not found: {path}");
    return 2;
}

using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
var root = doc.RootElement;
var pass = new List<string>();
var fail = new List<string>();

void Check(string id, bool ok, string message)
{
    (ok ? pass : fail).Add($"{(ok ? "PASS" : "FAIL")} {id} {message}");
}

string S(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? p.GetString() ?? string.Empty : string.Empty;
JsonElement ById(JsonElement array, string id) => array.EnumerateArray().FirstOrDefault(x => S(x, "id") == id);
JsonElement ByCode(JsonElement array, string code) => array.EnumerateArray().FirstOrDefault(x => S(x, "code") == code);

Check("P0W2-01", S(root, "schema") == expectedSchema, "Wave 2 schema identity");
Check("P0W2-02", S(root, "recoveryStatus") == "RECOVERED_FROM_FROZEN_EVIDENCE_NOT_ORIGINAL_P0_ARTIFACT", "recovery does not impersonate original P0 artifact");
Check("P0W2-03", root.GetProperty("sourceProduct").GetProperty("sha256").GetString() == expectedSha, "frozen v6.360 SHA bound");
Check("P0W2-04", S(root, "bindingStatus").Contains("NOT_YET_BOUND", StringComparison.Ordinal), "recovery cannot self-promote to P1 authority");

var commands = root.GetProperty("p0ApiCommands");
Check("P0W2-05", commands.GetArrayLength() == 12, "12 P0 API commands recovered for Portfolio through Knowledge");
var completion = ByCode(commands, "executions.completion-review");
Check("P0W2-06", completion.ValueKind != JsonValueKind.Undefined && S(completion, "requiredRole") == "EXECUTION_COMPLETION_REVIEWER", "independent completion reviewer authority preserved");
var validateK = ByCode(commands, "knowledge.validate");
var publishK = ByCode(commands, "knowledge.publish");
Check("P0W2-07", S(validateK, "requiredRole") == "KNOWLEDGE_STEWARD" && S(publishK, "requiredRole") == "KNOWLEDGE_PUBLISHER", "Knowledge validation/publication authority separated");

var portfolio = root.GetProperty("portfolio");
var pBehaviors = portfolio.GetProperty("behaviors");
Check("P0W2-08", portfolio.GetProperty("invariants").EnumerateArray().Any(x => S(x, "code") == "CANDIDATE_MEMBERSHIP_HANDOFF_DISTINCT"), "candidate, membership and handoff remain distinct");
var assign = ById(pBehaviors, "PORTFOLIO-ASSIGN-CANDIDATE");
Check("P0W2-09", S(assign, "to") == "PENDING_ASSIGNMENT" && S(assign, "effect").Contains("membership is not created", StringComparison.OrdinalIgnoreCase), "assignment does not create membership");
var rec = ById(pBehaviors, "EXECUTION-RECOMMENDATION-GENERATE");
Check("P0W2-10", rec.GetProperty("preconditions").EnumerateArray().Any(x => x.GetString() == "membership.status=ACCEPTED"), "accepted membership required for recommendation");
var handoff = ById(pBehaviors, "EXECUTION-HANDOFF");
var handoffPre = handoff.GetProperty("preconditions").EnumerateArray().Select(x => x.GetString()).ToHashSet();
Check("P0W2-11", handoffPre.Contains("recommendation.status=APPROVED") && handoffPre.Contains("recommendation.baselineApproved=true"), "recommendation and baseline precede execution handoff");
Check("P0W2-12", handoff.GetProperty("events").EnumerateArray().Select(x => x.GetString()).ToHashSet().SetEquals(new[] { "ExecutionHandoffRequested.v1", "ExecutionCreatedFromRecommendation.v1" }), "frozen execution handoff events preserved");

var execution = root.GetProperty("execution");
var eBehaviors = execution.GetProperty("behaviors");
var start = ById(eBehaviors, "EXECUTION-START");
var startPre = start.GetProperty("preconditions").EnumerateArray().Select(x => x.GetString()).ToHashSet();
Check("P0W2-13", S(start, "from") == "PLANNING" && S(start, "to") == "ACTIVE" && startPre.Contains("charterApproved=true") && startPre.Contains("planApproved=true"), "Execution start requires Charter and Plan");
var progress = ById(eBehaviors, "EXECUTION-PROGRESS");
Check("P0W2-14", progress.GetProperty("constraints").EnumerateArray().Any(x => x.GetString() == "progress=100 does not imply completion"), "100 percent progress is not completion");
var submit = ById(eBehaviors, "EXECUTION-SUBMIT-COMPLETION");
Check("P0W2-15", S(submit, "from") == "ACTIVE" && S(submit, "to") == "COMPLETION_REVIEW", "completion submission creates review state");
var review = ById(eBehaviors, "EXECUTION-COMPLETION-REVIEW");
Check("P0W2-16", S(review, "actor") == "EXECUTION_COMPLETION_REVIEWER" && S(review.GetProperty("legacyConflict"), "classification") == "LEGACY_SUPERSEDED", "legacy Execution Owner self-approval cannot become server authority");
Check("P0W2-17", S(execution, "terminalState") == "CLOSED" && execution.GetProperty("states").EnumerateArray().Any(x => x.GetString() == "COMPLETED"), "COMPLETED remains non-terminal; CLOSED is terminal");
var benefitHandoff = ById(eBehaviors, "EXECUTION-BENEFIT-HANDOFF");
Check("P0W2-18", S(benefitHandoff, "from") == "COMPLETED" && S(benefitHandoff, "event") == "BenefitHandoffRequested.v1", "Benefit handoff occurs after independent completion");

var benefit = root.GetProperty("benefit");
var expectedBenefitStates = new[] { "OBLIGATION_PENDING_ACCEPTANCE", "BASELINE_REQUIRED", "PLAN_REQUIRED", "MEASUREMENT_PENDING", "MEASURED", "VERIFIED", "VALIDATED", "REALIZED", "CLOSED" };
Check("P0W2-19", benefit.GetProperty("stateSequence").EnumerateArray().Select(x => x.GetString()).SequenceEqual(expectedBenefitStates), "Benefit sequential state machine preserved");
var bBehaviors = benefit.GetProperty("behaviors");
Check("P0W2-20", S(ById(bBehaviors, "BENEFIT-VERIFY"), "actor") == "BENEFIT_VERIFIER" && S(ById(bBehaviors, "BENEFIT-ATTRIBUTION"), "actor") == "BENEFIT_VERIFIER" && S(ById(bBehaviors, "BENEFIT-REALIZE"), "actor") == "BENEFIT_OWNER", "verification, attribution and realization authorities remain distinct");
var bClose = ById(bBehaviors, "BENEFIT-CLOSE");
Check("P0W2-21", S(bClose, "from") == "REALIZED" && bClose.GetProperty("preconditions").EnumerateArray().Any(x => x.GetString() == "Knowledge.status=PUBLISHED"), "Benefit close is gated by published Knowledge");

var knowledge = root.GetProperty("knowledge");
var author = knowledge.GetProperty("authorPolicy");
Check("P0W2-22", !author.GetProperty("benefitOwnerPrimaryAuthor").GetBoolean() && S(author, "exactResolution") == "CONFIGURABLE_TBD", "Knowledge author is not Benefit Owner and exact author resolution remains configurable");
var kBehaviors = knowledge.GetProperty("behaviors");
Check("P0W2-23", S(ById(kBehaviors, "KNOWLEDGE-VALIDATE-APPROVE"), "to") == "VALIDATED" && S(ById(kBehaviors, "KNOWLEDGE-PUBLISH"), "to") == "PUBLISHED", "Knowledge validation and publication are separate states");
Check("P0W2-24", knowledge.GetProperty("sod").EnumerateArray().Any(x => x.GetString() == "KNOWLEDGE_STEWARD != KNOWLEDGE_PUBLISHER"), "Knowledge SoD explicitly retained");
var createK = ById(kBehaviors, "KNOWLEDGE-CREATE-DRAFT");
Check("P0W2-25", S(createK.GetProperty("legacyConflict"), "classification") == "LEGACY_SUPERSEDED", "legacy Benefit Owner knowledge-author mapping is not recovered as authority");

var migration = root.GetProperty("controlledMigrationDecisions");
var autoEligibility = ByCode(migration, "PORTFOLIO_ELIGIBILITY_AUTO_AFTER_G04_APPROVED");
Check("P0W2-26", S(autoEligibility, "classification") == "CONTROLLED_MIGRATION_DECISION" && S(autoEligibility, "policy").Contains("automatically after G04 Approved", StringComparison.OrdinalIgnoreCase), "PortfolioEligibility automation is kept separate from frozen reconstruction");
Check("P0W2-27", migration.EnumerateArray().Any(x => S(x, "code") == "POST_G04_DATA_VALUE_AUDIT_BEFORE_SIMPLIFICATION"), "post-G04 simplification blocked pending Data Value Audit");

var unresolved = root.GetProperty("unresolved");
Check("P0W2-28", unresolved.EnumerateArray().Count(x => S(x, "status") == "TBD_UNRECOVERED_SERVER_COMMAND") >= 8, "prototype-only mutations remain explicitly fail-closed until server command identities are baselined");
var gap = ByCode(unresolved, "P1_COMMAND_CATALOG_7_MISSING_IDENTITIES");
Check("P0W2-29", gap.GetProperty("knownRecoveredCount").GetInt32() == 21 && gap.GetProperty("completionReviewDeclaredCount").GetInt32() == 28, "21/28 P1 command gap remains visible");

var raw = await File.ReadAllTextAsync(path);
Check("P0W2-30", !raw.Contains("\"bindingStatus\": \"READY\"", StringComparison.Ordinal) && !raw.Contains("\"status\": \"AUTHORITATIVE\"", StringComparison.Ordinal), "recovery contains no accidental readiness/authority promotion");

foreach (var line in pass) Console.WriteLine(line);
foreach (var line in fail) Console.Error.WriteLine(line);
Console.WriteLine($"RESULT {pass.Count}/{pass.Count + fail.Count} PASS");
return fail.Count == 0 ? 0 : 1;
