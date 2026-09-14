using System.Text.Json.Nodes;
using EIMS.P0.MachineRecovery;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: EIMS.P0.MachineRecovery.ContractTests <contract-json>");
    return 2;
}

var baseline = File.ReadAllText(args[0]);
var results = new List<(string Id, string Name, bool Pass)>();
void Add(string id, string name, bool pass) => results.Add((id, name, pass));

bool Fails(string json, string checkId)
{
    try { return P0MachineRecoveryValidator.Validate(json).Any(x => x.Id == checkId && !x.Passed); }
    catch { return true; }
}

JsonObject Copy() => JsonNode.Parse(baseline)!.AsObject();
JsonObject Command(JsonObject root, string id) => root["commandCatalog"]!["commands"]!.AsArray().Select(x => x!.AsObject()).Single(x => x["id"]!.GetValue<string>() == id);
JsonObject State(JsonObject root, string id) => root["stateFragments"]![id]!.AsObject();
JsonObject Outcome(JsonObject state, string decision) => state["outcomes"]!.AsArray().Select(x => x!.AsObject()).Single(x => x["decision"]!.GetValue<string>() == decision);

Add("P0-CT-01", "baseline recovered contract passes all verifier checks", P0MachineRecoveryValidator.IsValid(baseline));

void Mutation(string id, string name, string expectedFailure, Action<JsonObject> mutate)
{
    var copy = Copy();
    mutate(copy);
    Add(id, name, Fails(copy.ToJsonString(), expectedFailure));
}

Mutation("P0-CT-02", "recovery cannot claim to be original", "P0R-02", r => r["status"] = "ORIGINAL");
Mutation("P0-CT-03", "frozen product SHA cannot drift", "P0R-03", r => r["frozenProduct"]!["sha256"] = new string('0', 64));
Mutation("P0-CT-04", "missing original package cannot be declared available", "P0R-04", r => r["provenance"]!["originalMachineReadablePackageAvailable"] = true);
Mutation("P0-CT-05", "recovery cannot impersonate missing artifacts", "P0R-05", r => r["provenance"]!["recoveryMayImpersonateOriginal"] = true);
Mutation("P0-CT-06", "missing-artifact inventory cannot be silently shortened", "P0R-06", r => r["provenance"]!["missingOriginalArtifacts"]!.AsArray().RemoveAt(0));
Mutation("P0-CT-07", "28/21/7 command cardinality cannot be rewritten", "P0R-07", r => r["commandCatalog"]!["unidentifiedOriginalCommandCount"] = 6);
Mutation("P0-CT-08", "21 recovered API commands must stay explicit", "P0R-08", r => r["commandCatalog"]!["commands"]!.AsArray().RemoveAt(0));
Mutation("P0-CT-09", "duplicate recovered command id is rejected", "P0R-09", r => Command(r, "g02.decide")["id"] = "g01.decide");
Mutation("P0-CT-10", "duplicate endpoint is rejected", "P0R-10", r => Command(r, "g02.decide")["endpoint"] = "/commands/g01/decide");
Mutation("P0-CT-11", "mutating command cannot silently become GET", "P0R-11", r => Command(r, "g01.decide")["method"] = "GET");
Mutation("P0-CT-12", "P0 API proposal cannot be relabeled as confirmed prototype behavior", "P0R-12", r => Command(r, "g01.decide")["sourceStatus"] = "CONFIRMED");
Mutation("P0-CT-13", "partial evidence cannot be relabeled fully recovered", "P0R-13", r => Command(r, "g04.final-decision")["recoveryStatus"] = "COMPLETE");
Mutation("P0-CT-14", "recovered product command cannot be enabled", "P0R-14", r => Command(r, "g04.final-decision")["executable"] = true);
Mutation("P0-CT-15", "state completeness cannot be fabricated", "P0R-15", r => Command(r, "execution.progress")["completeStateContract"] = true);
Mutation("P0-CT-16", "rule completeness cannot be fabricated", "P0R-16", r => Command(r, "benefit.verify")["completeRuleContract"] = true);
Mutation("P0-CT-17", "event completeness cannot be fabricated", "P0R-17", r => Command(r, "knowledge.publish")["completeEventContract"] = true);
Mutation("P0-CT-18", "fail-closed reason is mandatory", "P0R-18", r => Command(r, "reward.decide")["failClosedReason"] = "");
Mutation("P0-CT-19", "seven unidentified commands cannot be given invented identities", "P0R-19", r => r["commandCatalog"]!["unidentifiedOriginalCommandIds"] = new JsonArray("invented.command"));
Mutation("P0-CT-20", "G01 official event remains unresolved", "P0R-20", r => r["eventRecovery"]!["g01ToCaseOfficialEvent"] = "InventedCaseEvent.v1");
Mutation("P0-CT-21", "G02 official event remains unresolved", "P0R-21", r => r["eventRecovery"]!["g02ToNeedOfficialEvent"] = "InventedNeedEvent.v1");
Mutation("P0-CT-22", "unnamed original-P1 stabilized events cannot be falsely named recovered", "P0R-22", r => r["eventRecovery"]!["stabilizedNamesRecovered"] = true);
Mutation("P0-CT-23", "product execution default cannot change from fail closed", "P0R-25", r => r["executionPolicy"]!["productCommandDefault"] = "ALLOW");
Mutation("P0-CT-24", "automatic enablement remains forbidden", "P0R-26", r => r["executionPolicy"]!["noRecoveredCommandIsAutomaticallyExecutable"] = false);
Mutation("P0-CT-25", "G03 submission target cannot drift", "P0R-35", r => State(r, "need.submit-g03")["targetState"] = "READY_FOR_IDEATION");
Mutation("P0-CT-26", "G04 approval event cannot drift", "P0R-36", r => Outcome(State(r, "g04.final-decision"), "APPROVE")["event"] = "IdeaApproved.v2");
Mutation("P0-CT-27", "Benefit verification state sequence cannot drift", "P0R-37", r => State(r, "benefit.verify")["toState"] = "REALIZED");
Mutation("P0-CT-28", "Execution completion submission cannot bypass review", "P0R-38", r => State(r, "execution.submit-completion")["toState"] = "COMPLETED");
Mutation("P0-CT-29", "G02-created Need initial state remains DRAFT", "P0R-40", r => Outcome(State(r, "g02.decide"), "NEED_CANDIDATE")["creates"]!["initialState"] = "READY_FOR_IDEATION");
Mutation("P0-CT-30", "critical P0 role cannot drift", "P0R-47", r => Command(r, "g04.final-decision")["requiredRole"] = "G04_COMMITTEE_MEMBER");
Mutation("P0-CT-31", "Oracle environment remains explicit TBD", "P0R-45", r => r["openTbd"] = new JsonArray(r["openTbd"]!.AsArray().Select(x => x!.GetValue<string>()).Where(x => !x.Contains("Oracle", StringComparison.Ordinal)).Select(x => JsonValue.Create(x)).ToArray()));
Mutation("P0-CT-32", "frozen G04 SoD cannot be collapsed", "P0R-33", r => r["separationOfDuties"]![2] = "G04 Committee Member is final G04 Decision authority");

foreach (var result in results)
    Console.WriteLine($"{(result.Pass ? "PASS" : "FAIL")} {result.Id} {result.Name}");
var passCount = results.Count(x => x.Pass);
Console.WriteLine($"RESULT {passCount}/{results.Count} PASS");
return passCount == results.Count ? 0 : 1;
