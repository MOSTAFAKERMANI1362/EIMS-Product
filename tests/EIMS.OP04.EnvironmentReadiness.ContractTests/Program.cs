using System.Text.Json;
using EIMS.PilotEnvironment.Readiness;

var tests = new List<(string Name, Action Run)>
{
    ("OP04-CT-01 complete Pilot evidence passes all nine gates", CompletePilotPasses),
    ("OP04-CT-02 complete Lab evidence never activates Network Pilot", CompleteLabDoesNotActivate),
    ("OP04-CT-03 missing Oracle live proof blocks ORACLE_P2", MissingOracleBlocks),
    ("OP04-CT-04 trusted client identity headers block P3", TrustedClientHeadersBlock),
    ("OP04-CT-05 missing backup restore proof blocks operations", MissingRestoreBlocks),
    ("OP04-CT-06 incomplete security review blocks activation", SecurityReviewBlocks),
    ("OP04-CT-07 secret-like property names are rejected", SensitivePropertyBlocks),
    ("OP04-CT-08 unknown schema fails closed", UnknownSchemaBlocks),
    ("OP04-CT-09 legacy schema without evidence class cannot activate", LegacyUnclassifiedBlocks),
    ("OP04-CT-10 malformed JSON throws instead of guessing", MalformedJsonThrows)
};

var passed = 0;
foreach (var (name, run) in tests)
{
    try { run(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
Console.WriteLine($"RESULT {passed}/{tests.Count} PASS");
return passed == tests.Count ? 0 : 1;

void CompletePilotPasses()
{
    var r = EnvironmentEvidenceEvaluator.Evaluate(CompleteEvidence("PILOT_ENVIRONMENT_EVIDENCE"));
    True(r.SchemaSupported); True(r.EvidenceClassSupported); False(r.SensitiveKeysDetected);
    Eq(9, r.TotalGateCount); Eq(9, r.PassedGateCount); True(r.PilotActivationReady);
}

void CompleteLabDoesNotActivate()
{
    var r = EnvironmentEvidenceEvaluator.Evaluate(CompleteEvidence("LAB_EVIDENCE"));
    Eq(9, r.PassedGateCount); True(r.EvidenceClassSupported); False(r.PilotActivationReady);
}

void MissingOracleBlocks()
{
    var json = CompleteEvidence("PILOT_ENVIRONMENT_EVIDENCE")
        .Replace("\"liveConnectionValidated\":true", "\"liveConnectionValidated\":false", StringComparison.Ordinal);
    var r = EnvironmentEvidenceEvaluator.Evaluate(json);
    False(r.PilotActivationReady); False(Gate(r, "ORACLE_P2").Passed);
    True(Gate(r, "ORACLE_P2").MissingOrInvalid.Contains("oracle.liveConnectionValidated"));
}

void TrustedClientHeadersBlock()
{
    var json = CompleteEvidence("PILOT_ENVIRONMENT_EVIDENCE")
        .Replace("\"clientIdentityHeadersTrusted\":false", "\"clientIdentityHeadersTrusted\":true", StringComparison.Ordinal);
    var r = EnvironmentEvidenceEvaluator.Evaluate(json);
    False(r.PilotActivationReady); False(Gate(r, "WINDOWS_IDENTITY_P3").Passed);
}

void MissingRestoreBlocks()
{
    var json = CompleteEvidence("PILOT_ENVIRONMENT_EVIDENCE")
        .Replace("\"backupRestoreValidated\":true", "\"backupRestoreValidated\":false", StringComparison.Ordinal);
    var r = EnvironmentEvidenceEvaluator.Evaluate(json);
    False(r.PilotActivationReady); False(Gate(r, "OPERATIONS").Passed);
}

void SecurityReviewBlocks()
{
    var json = CompleteEvidence("PILOT_ENVIRONMENT_EVIDENCE")
        .Replace("\"reviewedByRole\":\"SECURITY\"", "\"reviewedByRole\":\"\"", StringComparison.Ordinal);
    var r = EnvironmentEvidenceEvaluator.Evaluate(json);
    False(r.PilotActivationReady); False(Gate(r, "SECURITY_EVIDENCE").Passed);
}

void SensitivePropertyBlocks()
{
    var source = CompleteEvidence("PILOT_ENVIRONMENT_EVIDENCE");
    var insertAt = source.LastIndexOf('}');
    var json = source.Insert(insertAt, ",\"dbPassword\":\"must-never-be-here\"");
    var r = EnvironmentEvidenceEvaluator.Evaluate(json);
    True(r.SensitiveKeysDetected);
    True(r.SensitiveKeyPaths.Any(x => x.EndsWith(".dbPassword", StringComparison.Ordinal)));
    False(r.PilotActivationReady);
}

void UnknownSchemaBlocks()
{
    var json = CompleteEvidence("PILOT_ENVIRONMENT_EVIDENCE")
        .Replace("\"schemaVersion\":\"1.1\"", "\"schemaVersion\":\"99\"", StringComparison.Ordinal);
    var r = EnvironmentEvidenceEvaluator.Evaluate(json);
    False(r.SchemaSupported); False(r.PilotActivationReady);
}

void LegacyUnclassifiedBlocks()
{
    var json = CompleteEvidence("PILOT_ENVIRONMENT_EVIDENCE")
        .Replace("\"schemaVersion\":\"1.1\",", "\"schemaVersion\":\"1.0\",", StringComparison.Ordinal)
        .Replace("\"evidenceClass\":\"PILOT_ENVIRONMENT_EVIDENCE\",", string.Empty, StringComparison.Ordinal);
    var r = EnvironmentEvidenceEvaluator.Evaluate(json);
    True(r.SchemaSupported); False(r.EvidenceClassSupported); False(r.PilotActivationReady);
}

void MalformedJsonThrows()
{
    var threw = false;
    try { EnvironmentEvidenceEvaluator.Evaluate("{not-json"); }
    catch (JsonException) { threw = true; }
    True(threw);
}

EvidenceGate Gate(EnvironmentEvidenceReport r, string id) => r.Gates.Single(x => x.Id == id);

string CompleteEvidence(string evidenceClass) => $$"""
{"schemaVersion":"1.1","evidenceClass":"{{evidenceClass}}","referenceCustomer":"Mes Shahid Bahonar",
"windows":{"serverName":"EIMS-PILOT-01","osVersion":"Windows Server","vmProvisioned":true,"domainJoined":true,"domainName":"EXAMPLE","iisInstalled":true,"windowsAuthenticationInstalled":true,"dotnetRuntimeVersion":"10.0","collectedAt":"2026-09-15T12:00:00Z","collectedByRole":"IT"},
"oracle":{"version":"VERIFIED","providerName":"VERIFIED","providerVersion":"VERIFIED","connectionMode":"VERIFIED","serviceAccountName":"EIMS_SVC","schemaOwner":"EIMS_OWNER","liveConnectionValidated":true,"evidenceRef":"EV-ORA-1"},
"p1Authority":{"physicalPackageAvailable":true,"packageRef":"PKG-P1P5","buildPassed":true,"contractTestsPassed":true,"evidenceRef":"EV-P1-1"},
"windowsIdentity":{"identityFormat":"DOMAIN\\user","liveDomainIdentityValidated":true,"personIdMappingValidated":true,"serverRoleScopeValidated":true,"clientIdentityHeadersTrusted":false,"evidenceRef":"EV-P3-1"},
"hrOrg":{"sourceSystem":"Oracle HR","exportOwnerRole":"HRIT","realExportPrepared":true,"p4SchemaValidated":true,"reconciled":true,"approvalRef":"APR-HR-1","evidenceRef":"EV-P4-1"},
"tls":{"configured":true,"hostname":"eims.example.local","certificateSubject":"CN=eims.example.local","validTo":"2027-09-15","liveHandshakeValidated":true,"evidenceRef":"EV-TLS-1"},
"runtime":{"concurrencyValidated":true,"idempotencyValidated":true,"auditOutboxAtomicityValidated":true,"evidenceRef":"EV-RUN-1"},
"operations":{"backupMethod":"VERIFIED","backupRestoreValidated":true,"monitoringTarget":"VERIFIED","monitoringValidated":true,"evidenceRef":"EV-OPS-1"},
"security":{"noSecretsCommitted":true,"noPersonalDataCommitted":true,"reviewedByRole":"SECURITY","reviewDate":"2026-09-15"}}
""";

void True(bool v) { if (!v) throw new Exception("Expected true"); }
void False(bool v) { if (v) throw new Exception("Expected false"); }
void Eq<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected '{expected}', actual '{actual}'");
}
