using System.Text.Json;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: EIMS.ACR.P0.EventNaming.ContractTests <acr-json>");
    return 2;
}

using var doc = JsonDocument.Parse(File.ReadAllText(args[0]));
var root = doc.RootElement;
var events = root.GetProperty("events").EnumerateArray().ToArray();
var checks = new List<(string Id, string Name, bool Pass)>();
void Add(string id, string name, bool pass) => checks.Add((id, name, pass));
string S(JsonElement e, string p) => e.GetProperty(p).GetString() ?? string.Empty;
bool B(JsonElement e, string p) => e.GetProperty(p).GetBoolean();
string[] A(JsonElement e, string p) => e.GetProperty(p).EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
JsonElement E(string type) => events.Single(x => S(x, "eventType") == type);

Add("ACR-CT-01", "ACR identity is stable", S(root, "acrId") == "ACR-P0-001" && S(root, "schema") == "EIMS-ACR-EVENT-NAMING-1.0");
Add("ACR-CT-02", "decision is explicitly post-freeze", S(root, "decisionClass") == "POST_FREEZE_ARCHITECTURE_DECISION");
Add("ACR-CT-03", "frozen v6.360 is not modified", !B(root.GetProperty("frozenProduct"), "modifiedByThisDecision"));
Add("ACR-CT-04", "historical mapping remains recorded as TBD", S(root.GetProperty("provenance"), "historicalOriginalEventMappingStatus") == "TBD");
Add("ACR-CT-05", "new decision cannot impersonate recovered original", B(root.GetProperty("provenance"), "mustNotBeRepresentedAsRecoveredOriginal"));
Add("ACR-CT-06", "exactly two event names are stabilized", events.Length == 2);
Add("ACR-CT-07", "G01 event name is stable", S(E("CaseCreatedFromApprovedSource.v1"), "producerContext") == "ImprovementSource/G01");
Add("ACR-CT-08", "G02 event name is stable", S(E("NeedCreatedFromQualifiedCase.v1"), "producerContext") == "Case/G02");

var g01 = E("CaseCreatedFromApprovedSource.v1");
var g02 = E("NeedCreatedFromQualifiedCase.v1");
Add("ACR-CT-09", "G01 trigger preserves APPROVE → Case UNDER_REVIEW semantics", S(g01.GetProperty("trigger"), "command") == "g01.decide" && S(g01.GetProperty("trigger"), "decision") == "APPROVE" && S(g01.GetProperty("trigger"), "createdAggregateType") == "Case" && S(g01.GetProperty("trigger"), "createdAggregateInitialState") == "UNDER_REVIEW");
Add("ACR-CT-10", "G02 trigger preserves NEED_CANDIDATE → Need DRAFT semantics", S(g02.GetProperty("trigger"), "command") == "g02.decide" && S(g02.GetProperty("trigger"), "decision") == "NEED_CANDIDATE" && S(g02.GetProperty("trigger"), "createdAggregateType") == "Need" && S(g02.GetProperty("trigger"), "createdAggregateInitialState") == "DRAFT");
Add("ACR-CT-11", "both events are emitted only after atomic commit via outbox", events.All(x => S(x, "emitTiming") == "AFTER_ATOMIC_COMMIT_VIA_OUTBOX"));
Add("ACR-CT-12", "event envelope requires correlation and causation", events.All(x => A(x, "requiredEnvelopeFields").Contains("correlationId") && A(x, "requiredEnvelopeFields").Contains("causationId") && A(x, "requiredEnvelopeFields").Contains("eventId")));
Add("ACR-CT-13", "delivery requires idempotent at-least-once consumers", S(root.GetProperty("namingStandard"), "delivery") == "AT_LEAST_ONCE" && S(root.GetProperty("namingStandard"), "consumerRequirement") == "IDEMPOTENT_BY_EVENT_ID");
Add("ACR-CT-14", "v1 compatibility is guarded", B(root.GetProperty("compatibility"), "eventTypeImmutableWithinV1") && B(root.GetProperty("compatibility"), "breakingSemanticChangeRequiresNewMajor"));
Add("ACR-CT-15", "event naming alone cannot promote a P1 command", B(root.GetProperty("p1ConsumptionGate"), "eventIdentityResolved") && !B(root.GetProperty("p1ConsumptionGate"), "commandPromotionAllowedByThisAcrAlone"));
Add("ACR-CT-16", "remaining P1 promotion gates stay explicit", A(root.GetProperty("p1ConsumptionGate"), "stillRequiredBeforeCommandExecution").Contains("completeStateContract") && A(root.GetProperty("p1ConsumptionGate"), "stillRequiredBeforeCommandExecution").Contains("completeRuleContract") && A(root.GetProperty("p1ConsumptionGate"), "stillRequiredBeforeCommandExecution").Contains("atomicPersistence"));
Add("ACR-CT-17", "event types follow versioned PascalCase naming", events.All(x => System.Text.RegularExpressions.Regex.IsMatch(S(x, "eventType"), "^[A-Z][A-Za-z0-9]+\\.v[1-9][0-9]*$")));
Add("ACR-CT-18", "event payloads retain only source/created aggregate references as mandatory business fields", A(g01, "requiredPayloadFields").SequenceEqual(new[] { "sourceImprovementSourceId", "createdCaseId" }) && A(g02, "requiredPayloadFields").SequenceEqual(new[] { "sourceCaseId", "createdNeedId" }));

foreach (var c in checks) Console.WriteLine($"{(c.Pass ? "PASS" : "FAIL")} {c.Id} {c.Name}");
var passed = checks.Count(x => x.Pass);
Console.WriteLine($"RESULT {passed}/{checks.Count} PASS");
return passed == checks.Count ? 0 : 1;
