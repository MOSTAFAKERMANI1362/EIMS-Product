using System.Text.Json;
using EIMS.Authority.Recovery;

if (args.Length != 2 || !File.Exists(args[0]) || !File.Exists(args[1]))
{
    Console.Error.WriteLine("Usage: EIMS.G03.EventBinding.Recovery.ContractTests <ACR-P0-002-json> <P1-wave3-g03-event-json>");
    return 2;
}

using var acrDoc = JsonDocument.Parse(File.ReadAllText(args[0]));
using var wave3Doc = JsonDocument.Parse(File.ReadAllText(args[1]));
var acr = acrDoc.RootElement;
var wave3 = wave3Doc.RootElement;
var acrEvents = acr.GetProperty("events").EnumerateArray().ToArray();
var accepted = wave3.GetProperty("acceptedEventContracts").EnumerateArray().ToArray();
var runtime = new RecoveredApiCommandCatalog();

var results = new List<(string Id, string Name, bool Pass)>();
void Add(string id, string name, bool pass) => results.Add((id, name, pass));
string S(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
bool B(JsonElement e, string p) => e.GetProperty(p).GetBoolean();
JsonElement AEvent(string type) => acrEvents.Single(x => S(x, "eventType") == type);
JsonElement Accepted(string command) => accepted.Single(x => S(x, "command") == command);

Add("G03EB-CT-01", "ACR-P0-002 identity is exact", S(acr, "acrId") == "ACR-P0-002" && S(acr, "schema") == "EIMS-ACR-EVENT-CONTRACT-1.0");
Add("G03EB-CT-02", "ACR is approved post-freeze architecture decision", S(acr, "status") == "APPROVED_FOR_P1_IMPLEMENTATION" && S(acr, "decisionClass") == "POST_FREEZE_ARCHITECTURE_DECISION");
Add("G03EB-CT-03", "ACR does not claim historical event recovery", S(acr.GetProperty("provenance"), "historicalOriginalG03DomainEventIdentities") == "TBD_OR_UNAVAILABLE" && B(acr.GetProperty("provenance"), "mustNotBeRepresentedAsRecoveredOriginal"));
Add("G03EB-CT-04", "ACR defines exactly the three approved G03 events", acrEvents.Select(x => S(x,"eventType")).OrderBy(x => x).SequenceEqual(new[] { "NeedApprovedForIdeation.v1", "NeedReturnedFromG03Review.v1", "NeedSubmittedForG03Review.v1" }));
Add("G03EB-CT-05", "ACR delivery is Outbox at-least-once and EventId-idempotent", S(acr.GetProperty("commonEnvelope"), "emitTiming") == "AFTER_ATOMIC_COMMIT_VIA_OUTBOX" && S(acr.GetProperty("namingStandard"), "delivery") == "AT_LEAST_ONCE" && S(acr.GetProperty("namingStandard"), "consumerRequirement") == "IDEMPOTENT_BY_EVENT_ID");
Add("G03EB-CT-06", "ACR preserves data minimization", !B(acr.GetProperty("privacyAndMinimization"), "duplicateNeedDossierInEvent") && !B(acr.GetProperty("privacyAndMinimization"), "includeReturnNoteText"));

Add("G03EB-CT-07", "Wave 3 identity and status are explicit", S(wave3,"schema") == "EIMS-P1-RECOVERY-WAVE3-G03-EVENT-REBASELINE-1.0" && S(wave3,"status") == "APPROVED_REBASELINE_FOR_P1_EVENT_BINDING" && S(wave3,"decisionClass") == "RECOVERY_REBASELINE_ACCEPTANCE");
var source = wave3.GetProperty("sourceDecision");
Add("G03EB-CT-08", "Wave 3 provenance points to post-freeze ACR-P0-002", S(source,"acrId") == "ACR-P0-002" && S(source,"decisionClass") == "POST_FREEZE_ARCHITECTURE_DECISION" && !B(source,"historicalOriginalEventIdentitiesRecovered"));
Add("G03EB-CT-09", "Wave 3 leaves frozen v6.360 unchanged", S(wave3.GetProperty("frozenProduct"),"file") == "EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html" && !B(wave3.GetProperty("frozenProduct"),"modifiedByThisRebaseline"));
Add("G03EB-CT-10", "Wave 3 accepts exactly two G03 command event contracts", accepted.Length == 2 && accepted.Select(x => S(x,"command")).OrderBy(x => x).SequenceEqual(new[] { "needs.g03-decision", "needs.submit-g03" }));

var submit = Accepted("needs.submit-g03");
Add("G03EB-CT-11", "submit binding is exact static ACR event", S(submit,"bindingKind") == "STATIC" && S(submit,"eventType") == "NeedSubmittedForG03Review.v1" && S(AEvent("NeedSubmittedForG03Review.v1"),"producerCommand") == "needs.submit-g03");
var decision = Accepted("needs.g03-decision");
var decisionOutcomes = decision.GetProperty("outcomeEvents");
Add("G03EB-CT-12", "decision binding maps APPROVE and RETURN exactly", S(decision,"bindingKind") == "OUTCOME" && S(decisionOutcomes,"APPROVE") == "NeedApprovedForIdeation.v1" && S(decisionOutcomes,"RETURN") == "NeedReturnedFromG03Review.v1");
Add("G03EB-CT-13", "decision event triggers match ACR outcomes", S(AEvent("NeedApprovedForIdeation.v1").GetProperty("trigger"),"decision") == "APPROVE" && S(AEvent("NeedReturnedFromG03Review.v1").GetProperty("trigger"),"decision") == "RETURN");

var safety = wave3.GetProperty("safety");
Add("G03EB-CT-14", "Wave 3 recovers event contract but not mutation", B(safety,"eventContractRecoveredForAcceptedCommands") && B(safety,"mutationContractsRemainUnrecovered") && B(safety,"doesNotEnableProductMutation"));
Add("G03EB-CT-15", "Wave 3 forbids outcome fallback and rebaseline-only emission", B(safety,"outcomeAwareDecisionRequiresExplicitOutcome") && B(safety,"noFallbackEventForUnknownOutcome") && B(safety,"doesNotEmitEventsByRebaselineAlone"));
var delivery = wave3.GetProperty("deliveryContract");
Add("G03EB-CT-16", "Wave 3 delivery contract matches ACR", S(delivery,"delivery") == S(acr.GetProperty("namingStandard"),"delivery") && S(delivery,"emitTiming") == S(acr.GetProperty("commonEnvelope"),"emitTiming") && S(delivery,"consumerIdempotency") == "EVENT_ID");

Add("G03EB-CT-17", "runtime catalog recovery layer counts are exact", runtime.All.Count == 21 && RecoveredApiCommandCatalog.Wave1StateBoundCommandCount == 6 && RecoveredApiCommandCatalog.Wave2RuleBoundCommandCount == 2 && RecoveredApiCommandCatalog.Wave3EventBoundCommandCount == 2);
Add("G03EB-CT-18", "runtime has exactly two event-bound commands", runtime.All.Count(x => x.EventContractRecovered) == 2 && runtime.All.Where(x => x.EventContractRecovered).All(x => x.EventBinding is not null && x.EventBinding.IsValid));
True(runtime.TryGet("needs.submit-g03", out var submitPolicy));
Add("G03EB-CT-19", "runtime submit event resolves exactly", submitPolicy.StateContractRecovered && submitPolicy.RuleContractRecovered && submitPolicy.EventContractRecovered && !submitPolicy.MutationContractRecovered && submitPolicy.ResolveEventName() == "NeedSubmittedForG03Review.v1");
True(runtime.TryGet("needs.g03-decision", out var decisionPolicy));
Add("G03EB-CT-20", "runtime decision events resolve outcome-aware exactly", decisionPolicy.StateContractRecovered && decisionPolicy.RuleContractRecovered && decisionPolicy.EventContractRecovered && !decisionPolicy.MutationContractRecovered && decisionPolicy.ResolveEventName("APPROVE") == "NeedApprovedForIdeation.v1" && decisionPolicy.ResolveEventName("RETURN") == "NeedReturnedFromG03Review.v1");
Add("G03EB-CT-21", "runtime decision binding has no fallback", decisionPolicy.ResolveEventName() is null && decisionPolicy.ResolveEventName("HOLD") is null && decisionPolicy.ResolveEventName("UNKNOWN") is null);
Add("G03EB-CT-22", "all Product mutation contracts remain closed", runtime.All.All(x => !x.MutationContractRecovered));
Add("G03EB-CT-23", "G01 G02 G04 event contracts are not silently promoted", new[] { "g01.decide", "g02.decide", "ideas.submit-g04", "g04.final-decision" }.All(id => runtime.TryGet(id, out var p) && !p.EventContractRecovered));
var runtimeGate = wave3.GetProperty("runtimeExpectedGate");
Add("G03EB-CT-24", "both G03 commands are expected to stop at mutation gate", S(runtimeGate,"needs.submit-g03") == "P1_MUTATION_CONTRACT_NOT_RECOVERED" && S(runtimeGate,"needs.g03-decision") == "P1_MUTATION_CONTRACT_NOT_RECOVERED");
Add("G03EB-CT-25", "ACR payload forbids Need dossier and return note", acrEvents.All(e => { var f = e.GetProperty("forbiddenPayloadFields").EnumerateArray().Select(x => x.GetString() ?? "").ToArray(); return new[]{"title","owner","current","desired","gap","returnNote"}.All(f.Contains); }));

foreach (var r in results) Console.WriteLine($"{(r.Pass ? "PASS" : "FAIL")} {r.Id} {r.Name}");
var passed = results.Count(x => x.Pass);
Console.WriteLine($"RESULT {passed}/{results.Count} PASS");
return passed == results.Count ? 0 : 1;

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}
