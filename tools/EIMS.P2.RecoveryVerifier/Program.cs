using System.Text.Json;

const string expectedSchema = "EIMS-P2-PERSISTENCE-RECOVERY-1.0";
const string expectedSha = "057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a";
const string defaultPath = "recovery/p2/P2_PERSISTENCE_RECOVERY_CONTRACT_v1.0.json";

var path = args.Length > 0 ? args[0] : defaultPath;
if (!File.Exists(path))
{
    Console.Error.WriteLine($"FAIL P2R-00 contract not found: {path}");
    return 2;
}

using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
var root = doc.RootElement;
var pass = new List<string>();
var fail = new List<string>();

void Check(string id, bool ok, string message) =>
    (ok ? pass : fail).Add($"{(ok ? "PASS" : "FAIL")} {id} {message}");

string S(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? p.GetString() ?? string.Empty : string.Empty;
bool B(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

Check("P2R-01", S(root, "schema") == expectedSchema, "P2 recovery schema identity");
Check("P2R-02", S(root, "recoveryStatus") == "RECOVERED_LOGICAL_CONTRACT_NOT_ORIGINAL_P2_ARTIFACT", "recovery cannot impersonate original P2");
Check("P2R-03", root.GetProperty("sourceProduct").GetProperty("sha256").GetString() == expectedSha, "frozen v6.360 SHA bound");
Check("P2R-04", S(root, "logicalContractStatus") == "READY_FOR_ADAPTER_IMPLEMENTATION", "logical persistence contract is ready for adapter implementation");
Check("P2R-05", S(root, "physicalOracleStatus") == "BLOCKED_ON_ENVIRONMENT_EVIDENCE", "physical Oracle binding remains fail closed");

var tx = root.GetProperty("atomicTransactionUnit").EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal);
Check("P2R-06", tx.SetEquals(new[] { "AggregateState", "AppendOnlyAudit", "EventOutbox", "IdempotencyRecord" }), "atomic transaction unit is complete");

var semantics = root.GetProperty("requiredSemantics");
var requiredFlags = new[]
{
    "optimisticConcurrency",
    "expectedVersionRequired",
    "serverSideIdempotency",
    "exactReplayReturnsPriorResultWithoutMutation",
    "idempotencyFingerprintConflictRejected",
    "appendOnlyAudit",
    "atomicStateAuditOutboxIdempotency",
    "correlationIdPreservedAcrossAuditAndOutbox",
    "aggregateVersionPreservedAcrossStateAuditAndOutbox",
    "rollbackOnAnyWriteFailure"
};
Check("P2R-07", requiredFlags.All(x => B(semantics, x)), "all recovered persistence invariants are enabled");

var uniqueness = root.GetProperty("logicalUniqueness").EnumerateArray().ToArray();
Check("P2R-08", uniqueness.Length == 3, "idempotency, audit and outbox uniqueness are explicit");
Check("P2R-09", uniqueness.All(x => S(x, "physicalDDLStatus") == "TBD_ORACLE_BINDING"), "physical Oracle DDL is not fabricated");

var binding = root.GetProperty("oracleBindingEvidence");
var evidenceFields = new[] { "oracleVersion", "dotNetProvider", "connectivityMode", "serviceAccountModel", "schemaOwner", "evidenceReference" };
Check("P2R-10", evidenceFields.All(x => binding.GetProperty(x).ValueKind == JsonValueKind.Null), "environment binding evidence remains unresolved rather than guessed");
Check("P2R-11",
    S(binding, "connectionString") == "PROHIBITED_FROM_CONTRACT"
    && S(binding, "password") == "PROHIBITED_FROM_CONTRACT"
    && S(binding, "secret") == "PROHIBITED_FROM_CONTRACT",
    "secrets and connection string are explicitly prohibited from recovery contract");

var adapter = root.GetProperty("adapterBoundary");
Check("P2R-12", S(adapter, "authorityPort") == "EIMS.Authority.Recovery.IAuthorityStore", "P1/P2 adapter boundary is stable");
Check("P2R-13", S(adapter, "productionOracleAdapter") == "TBD_AFTER_ENVIRONMENT_BINDING", "production Oracle adapter cannot be selected early");

var failure = root.GetProperty("failurePolicy");
Check("P2R-14", failure.GetProperty("partialCommitAllowed").ValueKind == JsonValueKind.False, "partial commit is forbidden");
Check("P2R-15", B(failure, "failClosedWhenOracleBindingIncomplete"), "incomplete Oracle binding fails closed");
Check("P2R-16", B(failure, "liveOracleProbeRequiredBeforeNetworkPilot"), "live Oracle probe is required before Network Pilot");

var physical = root.GetProperty("physicalDecisionsNotYetAuthorized").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
Check("P2R-17", physical.Length >= 8, "physical Oracle decisions are explicitly deferred");
Check("P2R-18", physical.Any(x => x.Contains("provider", StringComparison.OrdinalIgnoreCase)) && physical.Any(x => x.Contains("connection string", StringComparison.OrdinalIgnoreCase)), "provider and connection remain environment decisions");

var evidence = root.GetProperty("evidence");
Check("P2R-19", S(evidence, "sdk") == "10.0.302", "verification SDK is pinned");
Check("P2R-20", evidence.GetProperty("minimumRequiredTests").GetInt32() >= 23, "minimum atomicity test count is enforced");

foreach (var line in pass) Console.WriteLine(line);
foreach (var line in fail) Console.Error.WriteLine(line);
Console.WriteLine($"RESULT {pass.Count}/{pass.Count + fail.Count} PASS");
return fail.Count == 0 ? 0 : 1;
