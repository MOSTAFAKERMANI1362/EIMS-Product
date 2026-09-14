using System.Text.Json;

const string expectedSchema = "EIMS-P0-MACHINE-CONTRACT-RECOVERY-WAVE1-1.0";
const string expectedSha = "057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a";
const string defaultPath = "recovery/p0-wave1/P0_G01_G04_RECOVERED_CONTRACT_v1.0.json";

var path = args.Length > 0 ? args[0] : defaultPath;
if (!File.Exists(path))
{
    Console.Error.WriteLine($"FAIL P0R-00 recovery contract not found: {path}");
    return 2;
}

using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
var root = doc.RootElement;
var failures = new List<string>();
var passes = new List<string>();

void Check(string id, bool condition, string message)
{
    if (condition) passes.Add($"PASS {id} {message}");
    else failures.Add($"FAIL {id} {message}");
}

string S(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? p.GetString() ?? string.Empty : string.Empty;
bool B(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

Check("P0R-01", S(root, "schema") == expectedSchema, "recovery schema identity");
Check("P0R-02", S(root, "recoveryStatus") == "RECOVERED_FROM_FROZEN_EVIDENCE_NOT_ORIGINAL_P0_ARTIFACT", "artifact cannot impersonate original P0");
Check("P0R-03", root.GetProperty("sourceProduct").GetProperty("sha256").GetString() == expectedSha, "frozen v6.360 SHA bound");
Check("P0R-04", S(root, "bindingStatus") != "READY" && S(root, "bindingStatus").Contains("NOT_YET_BOUND", StringComparison.Ordinal), "recovery cannot self-promote to P1 ready");

var roles = root.GetProperty("roles").EnumerateArray().ToArray();
var commands = root.GetProperty("commands").EnumerateArray().ToArray();
var transitions = root.GetProperty("transitions").EnumerateArray().ToArray();
var unresolved = root.GetProperty("unresolved").EnumerateArray().ToArray();
var invariants = root.GetProperty("invariants").EnumerateArray().ToArray();

Check("P0R-05", roles.Length == 8, "Wave 1 authority-role set is explicit");
Check("P0R-06", commands.Length == 8 && commands.All(x => S(x, "evidenceStatus") == "PROPOSED_P0_API"), "Wave 1 P0 API commands remain proposed recovery inputs");
Check("P0R-07", transitions.Length == 17, "Wave 1 transition set count");

var commandRole = commands.ToDictionary(x => S(x, "code"), x => S(x, "requiredRole"), StringComparer.OrdinalIgnoreCase);
Check("P0R-08", commandRole.GetValueOrDefault("g04.vote") == "G04_COMMITTEE_MEMBER" && commandRole.GetValueOrDefault("g04.final-decision") == "IDEA_DECISION", "G04 vote and final authority remain separated");
Check("P0R-09", commandRole.GetValueOrDefault("needs.submit-g03") == "NEED_OWNER" && commandRole.GetValueOrDefault("needs.g03-decision") == "NEED_REVIEWER", "G03 owner/reviewer separation preserved");

var unresolvedEvents = transitions
    .Where(x => x.TryGetProperty("event", out var ev) && S(ev, "evidenceStatus") == "TBD_UNRECOVERED")
    .Select(x => x.GetProperty("event"))
    .ToArray();
Check("P0R-10", unresolvedEvents.Length == 2, "exactly two known G01/G02 domain-event identities remain unrecovered");
Check("P0R-11", unresolvedEvents.All(x => S(x, "identity") == "UNRECOVERED_ORIGINAL_P1_EVENT_IDENTITY" && !B(x, "authoritativeForOutbox")), "unrecovered event identities remain fail closed");

var authoritativeDomainEvents = transitions
    .Where(x => x.TryGetProperty("event", out var ev) && S(ev, "eventClass") == "DOMAIN_EVENT" && B(ev, "authoritativeForOutbox"))
    .Select(x => x.GetProperty("event"))
    .ToArray();
Check("P0R-12", authoritativeDomainEvents.Length == 1 && S(authoritativeDomainEvents[0], "identity") == "IdeaApprovedForPortfolio.v1", "only explicitly frozen domain event is outbox-authoritative in Wave 1");

var g01Approve = transitions.SingleOrDefault(x => S(x, "id") == "G01-APPROVE-TO-CASE");
Check("P0R-13", g01Approve.ValueKind != JsonValueKind.Undefined && S(g01Approve, "toState") == "CONVERTED_TO_CASE" && g01Approve.GetProperty("sideEffects")[0].GetProperty("initialState").GetString() == "UNDER_REVIEW", "G01 approve creates UNDER_REVIEW Case and converts source");

var g02Need = transitions.SingleOrDefault(x => S(x, "id") == "G02-NEED-CANDIDATE");
Check("P0R-14", g02Need.ValueKind != JsonValueKind.Undefined && S(g02Need, "toState") == "APPROVED" && g02Need.GetProperty("sideEffects")[0].GetProperty("initialState").GetString() == "DRAFT", "G02 Need candidate creates DRAFT Need");

var g03Approve = transitions.SingleOrDefault(x => S(x, "id") == "G03-APPROVE");
Check("P0R-15", g03Approve.ValueKind != JsonValueKind.Undefined && S(g03Approve, "toState") == "READY_FOR_IDEATION" && g03Approve.GetProperty("derived").GetProperty("g03ReviewStatus").GetString() == "APPROVED", "G03 approval evidence required before ideation");

var g04Submit = transitions.SingleOrDefault(x => S(x, "id") == "G04-SUBMIT");
Check("P0R-16", g04Submit.ValueKind != JsonValueKind.Undefined && S(g04Submit, "toState") == "UNDER_REVIEW" && g04Submit.GetProperty("sideEffects")[0].GetProperty("freezeEntityVersion").GetBoolean(), "G04 submission freezes assessment context");

var knownEvidence = new HashSet<string>(StringComparer.Ordinal)
{
    "CONFIRMED_V6_360", "CONFIRMED_P0_CONTRACT", "PROPOSED_P0_API", "SUPPORTING_ENGINEERING", "TBD_UNRECOVERED"
};
Check("P0R-17", transitions.All(x => knownEvidence.Contains(S(x, "evidenceStatus"))), "all transitions carry recognized evidence classification");
Check("P0R-18", invariants.All(x => S(x, "evidenceStatus") == "CONFIRMED_P0_CONTRACT"), "recovered invariants originate from Product Contract");

var gap = unresolved.SingleOrDefault(x => S(x, "code") == "P1_COMMAND_CATALOG_7_MISSING_IDENTITIES");
var gapOk = gap.ValueKind != JsonValueKind.Undefined
    && gap.GetProperty("knownRecoveredCount").GetInt32() == 21
    && gap.GetProperty("completionReviewDeclaredCount").GetInt32() == 28
    && S(gap, "status") == "TBD_UNRECOVERED";
Check("P0R-19", gapOk, "21/28 P1 command-catalog discrepancy remains explicit");

var raw = await File.ReadAllTextAsync(path);
Check("P0R-20", !raw.Contains("CaseCreatedFromApprovedSource.v1", StringComparison.Ordinal) && !raw.Contains("NeedCreatedFromQualifiedCase.v1", StringComparison.Ordinal), "suggested TBD event names were not silently promoted into recovery contract");

foreach (var line in passes) Console.WriteLine(line);
foreach (var line in failures) Console.Error.WriteLine(line);
Console.WriteLine($"RESULT {passes.Count}/{passes.Count + failures.Count} PASS");
return failures.Count == 0 ? 0 : 1;
