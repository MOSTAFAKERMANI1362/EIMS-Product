using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: EIMS.ACR.P0.G03Events.ContractTests <ACR-P0-002-json>");
    return 2;
}

using var doc = JsonDocument.Parse(File.ReadAllText(args[0]));
var root = doc.RootElement;
var events = root.GetProperty("events").EnumerateArray().ToArray();
var checks = new List<(string Id, string Name, bool Pass)>();
void Add(string id, string name, bool pass) => checks.Add((id, name, pass));
string S(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
bool B(JsonElement e, string p) => e.GetProperty(p).GetBoolean();
string[] A(JsonElement e, string p) => e.GetProperty(p).EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
JsonElement E(string type) => events.Single(x => S(x, "eventType") == type);

Add("G03E-CT-01", "ACR identity and schema are stable", S(root,"acrId") == "ACR-P0-002" && S(root,"schema") == "EIMS-ACR-EVENT-CONTRACT-1.0");
Add("G03E-CT-02", "decision is explicitly post-freeze", S(root,"decisionClass") == "POST_FREEZE_ARCHITECTURE_DECISION");
Add("G03E-CT-03", "frozen v6.360 remains unchanged", !B(root.GetProperty("frozenProduct"), "modifiedByThisDecision"));
Add("G03E-CT-04", "historical event identities remain unavailable/TBD", S(root.GetProperty("provenance"), "historicalOriginalG03DomainEventIdentities") == "TBD_OR_UNAVAILABLE");
Add("G03E-CT-05", "new names cannot impersonate recovered originals", B(root.GetProperty("provenance"), "mustNotBeRepresentedAsRecoveredOriginal"));
Add("G03E-CT-06", "audit labels cannot become Domain Events", B(root.GetProperty("provenance"), "auditLabelsMayNotBePromotedToDomainEvents"));
Add("G03E-CT-07", "exactly three G03 event contracts exist", events.Length == 3);
Add("G03E-CT-08", "all event names use versioned PascalCase", events.All(x => Regex.IsMatch(S(x,"eventType"), "^[A-Z][A-Za-z0-9]+\\.v[1-9][0-9]*$")));

var submit = E("NeedSubmittedForG03Review.v1");
var approve = E("NeedApprovedForIdeation.v1");
var returned = E("NeedReturnedFromG03Review.v1");
Add("G03E-CT-09", "submit event command/state semantics are exact", S(submit,"producerCommand") == "needs.submit-g03" && S(submit.GetProperty("trigger"),"fromState") == "DRAFT" && S(submit.GetProperty("trigger"),"toState") == "PENDING_G03_REVIEW" && submit.GetProperty("trigger").GetProperty("decision").ValueKind == JsonValueKind.Null);
Add("G03E-CT-10", "submit routes only by NEED_REVIEWER role", S(submit,"routingRole") == "NEED_REVIEWER");
Add("G03E-CT-11", "approve event command/state/decision semantics are exact", S(approve,"producerCommand") == "needs.g03-decision" && S(approve.GetProperty("trigger"),"fromState") == "PENDING_G03_REVIEW" && S(approve.GetProperty("trigger"),"toState") == "READY_FOR_IDEATION" && S(approve.GetProperty("trigger"),"decision") == "APPROVE");
Add("G03E-CT-12", "approve routes by IDEA_OWNER role only", S(approve,"routingRole") == "IDEA_OWNER");
Add("G03E-CT-13", "return event command/state/decision semantics are exact", S(returned,"producerCommand") == "needs.g03-decision" && S(returned.GetProperty("trigger"),"fromState") == "PENDING_G03_REVIEW" && S(returned.GetProperty("trigger"),"toState") == "DRAFT" && S(returned.GetProperty("trigger"),"decision") == "RETURN");
Add("G03E-CT-14", "return routes by NEED_OWNER role only", S(returned,"routingRole") == "NEED_OWNER");

var envelope = root.GetProperty("commonEnvelope");
var envelopeFields = A(envelope,"requiredFields");
Add("G03E-CT-15", "event emission is post-atomic-commit through Outbox", S(envelope,"emitTiming") == "AFTER_ATOMIC_COMMIT_VIA_OUTBOX");
Add("G03E-CT-16", "event aggregate is Need", S(envelope,"aggregateType") == "Need");
Add("G03E-CT-17", "envelope retains identity version actor correlation and causation", new[]{"eventId","eventType","eventVersion","eventCategory","occurredAtUtc","aggregateId","aggregateType","aggregateVersion","actorId","correlationId","causationId"}.All(envelopeFields.Contains));
Add("G03E-CT-18", "delivery is at-least-once with EventId idempotency", S(root.GetProperty("namingStandard"),"delivery") == "AT_LEAST_ONCE" && S(root.GetProperty("namingStandard"),"consumerRequirement") == "IDEMPOTENT_BY_EVENT_ID");

Add("G03E-CT-19", "all events require only routingRole as business payload", events.All(x => A(x,"requiredPayloadFields").SequenceEqual(new[]{"routingRole"})));
var forbidden = new[]{"title","owner","current","desired","gap","returnNote"};
Add("G03E-CT-20", "Need dossier and return note are forbidden in all event payloads", events.All(x => forbidden.All(A(x,"forbiddenPayloadFields").Contains)));
var privacy = root.GetProperty("privacyAndMinimization");
Add("G03E-CT-21", "return note text is not published", !B(privacy,"includeReturnNoteText") && !B(privacy,"duplicateNeedDossierInEvent"));
Add("G03E-CT-22", "return note stays authoritative in domain/audit", S(privacy,"authoritativeReturnNoteLocation") == "DOMAIN_STATE_OR_APPEND_ONLY_AUDIT");

var compatibility = root.GetProperty("compatibility");
Add("G03E-CT-23", "v1 event identities are immutable", B(compatibility,"eventTypeImmutableWithinV1") && B(compatibility,"breakingSemanticChangeRequiresNewMajor"));
var gate = root.GetProperty("p1ConsumptionGate");
Add("G03E-CT-24", "ACR alone cannot promote P1 event recovery", B(gate,"eventContractDecisionAvailable") && !B(gate,"runtimeEventContractRecoveredByThisAcrAlone") && !B(gate,"commandPromotionAllowedByThisAcrAlone"));
Add("G03E-CT-25", "next step requires outcome-aware binding while mutation stays closed", S(gate,"nextRequiredStep").Contains("outcome-aware", StringComparison.OrdinalIgnoreCase) && S(gate,"nextRequiredStep").Contains("MutationContractRecovered=false", StringComparison.Ordinal));

foreach (var c in checks) Console.WriteLine($"{(c.Pass ? "PASS" : "FAIL")} {c.Id} {c.Name}");
var passed = checks.Count(x => x.Pass);
Console.WriteLine($"RESULT {passed}/{checks.Count} PASS");
return passed == checks.Count ? 0 : 1;
