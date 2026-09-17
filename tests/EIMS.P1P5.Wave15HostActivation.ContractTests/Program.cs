using EIMS.PilotAssembly.Core;
using EIMS.PilotEnvironment.Readiness;

var tests = new List<(string Name, Action Run)>
{
    ("W15-01 complete LAB evidence cannot activate", LabBlocked),
    ("W15-02 incomplete PILOT evidence cannot activate", PilotMissingGateBlocked),
    ("W15-03 sensitive evidence cannot activate", SensitiveBlocked),
    ("W15-04 OP04-ready evidence alone cannot activate without durable P2", MissingP2Blocked),
    ("W15-05 OP04-ready evidence alone cannot activate without authoritative P3", MissingP3Blocked),
    ("W15-06 OP04-ready evidence alone cannot activate without production gateway", MissingGatewayBlocked),
    ("W15-07 fail-closed candidate cannot activate", FailClosedCandidateBlocked),
    ("W15-08 all prerequisites allow real gateway selection", CompletePrerequisitesActivate)
};

var passed = 0;
foreach (var (name, run) in tests)
{
    try { run(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
Console.WriteLine($"RESULT {passed}/{tests.Count} PASS");
return passed == tests.Count ? 0 : 1;

void LabBlocked()
{
    var report = EnvironmentEvidenceEvaluator.Evaluate(Evidence("LAB_EVIDENCE"));
    Eq(9, report.PassedGateCount); False(report.PilotActivationReady);
    False(Decision(report, true, true, true, false).Activate);
}

void PilotMissingGateBlocked()
{
    var json = Evidence("PILOT_ENVIRONMENT_EVIDENCE").Replace("\"liveConnectionValidated\":true", "\"liveConnectionValidated\":false", StringComparison.Ordinal);
    var report = EnvironmentEvidenceEvaluator.Evaluate(json);
    False(report.PilotActivationReady); False(Decision(report, true, true, true, false).Activate);
}

void SensitiveBlocked()
{
    var json = Evidence("PILOT_ENVIRONMENT_EVIDENCE");
    json = json.Insert(json.LastIndexOf('}'), ",\"deploymentSecret\":\"x\"");
    var report = EnvironmentEvidenceEvaluator.Evaluate(json);
    True(report.SensitiveKeysDetected); False(report.PilotActivationReady);
}

void MissingP2Blocked()
{
    var report = EnvironmentEvidenceEvaluator.Evaluate(Evidence("PILOT_ENVIRONMENT_EVIDENCE"));
    True(report.PilotActivationReady);
    var d = Decision(report, false, true, true, false);
    False(d.Activate); True(d.Blockers.Contains("DURABLE_P2_NOT_BOUND"));
}

void MissingP3Blocked()
{
    var report = EnvironmentEvidenceEvaluator.Evaluate(Evidence("PILOT_ENVIRONMENT_EVIDENCE"));
    var d = Decision(report, true, false, true, false);
    False(d.Activate); True(d.Blockers.Contains("AUTHORITATIVE_P3_NOT_BOUND"));
}

void MissingGatewayBlocked()
{
    var report = EnvironmentEvidenceEvaluator.Evaluate(Evidence("PILOT_ENVIRONMENT_EVIDENCE"));
    var d = Decision(report, true, true, false, false);
    False(d.Activate); True(d.Blockers.Contains("PRODUCTION_GATEWAY_NOT_AVAILABLE"));
}

void FailClosedCandidateBlocked()
{
    var report = EnvironmentEvidenceEvaluator.Evaluate(Evidence("PILOT_ENVIRONMENT_EVIDENCE"));
    var d = Decision(report, true, true, true, true);
    False(d.Activate); True(d.Blockers.Contains("CANDIDATE_GATEWAY_IS_FAIL_CLOSED"));
    var selected = RuntimeActivationGate.SelectGateway(d, new FailClosedCommandGateway(), new FailClosedCommandGateway());
    True(selected is FailClosedCommandGateway);
}

void CompletePrerequisitesActivate()
{
    var report = EnvironmentEvidenceEvaluator.Evaluate(Evidence("PILOT_ENVIRONMENT_EVIDENCE"));
    var d = Decision(report, true, true, true, false);
    True(d.Activate); Eq("RUNTIME_ACTIVATION_ALLOWED", d.Code);
    var candidate = new MarkerGateway();
    var selected = RuntimeActivationGate.SelectGateway(d, candidate, new FailClosedCommandGateway());
    True(ReferenceEquals(candidate, selected));
}

RuntimeActivationDecision Decision(EnvironmentEvidenceReport r, bool p2, bool p3, bool gateway, bool failClosed) =>
    RuntimeActivationGate.Evaluate(new RuntimeActivationPrerequisites(r.PilotActivationReady, p2, p3, gateway, failClosed));

string Evidence(string evidenceClass)
{
    const string template = """
{"schemaVersion":"1.1","evidenceClass":"__EVIDENCE_CLASS__","referenceCustomer":"Mes Shahid Bahonar",
"windows":{"serverName":"EIMS-PILOT-01","osVersion":"Windows Server","vmProvisioned":true,"domainJoined":true,"domainName":"EXAMPLE","iisInstalled":true,"windowsAuthenticationInstalled":true,"dotnetRuntimeVersion":"10.0","collectedAt":"2026-09-15T12:00:00Z"},
"oracle":{"version":"VERIFIED","providerName":"VERIFIED","providerVersion":"VERIFIED","connectionMode":"VERIFIED","serviceAccountName":"EIMS_SVC","schemaOwner":"EIMS_OWNER","liveConnectionValidated":true,"evidenceRef":"EV-ORA"},
"p1Authority":{"physicalPackageAvailable":true,"packageRef":"PKG","buildPassed":true,"contractTestsPassed":true,"evidenceRef":"EV-P1"},
"windowsIdentity":{"identityFormat":"DOMAIN\\user","liveDomainIdentityValidated":true,"personIdMappingValidated":true,"serverRoleScopeValidated":true,"clientIdentityHeadersTrusted":false,"evidenceRef":"EV-P3"},
"hrOrg":{"sourceSystem":"Oracle HR","exportOwnerRole":"HRIT","realExportPrepared":true,"p4SchemaValidated":true,"reconciled":true,"approvalRef":"APR-HR","evidenceRef":"EV-P4"},
"tls":{"configured":true,"hostname":"eims.example.local","certificateSubject":"CN=eims.example.local","validTo":"2027-09-15","liveHandshakeValidated":true,"evidenceRef":"EV-TLS"},
"runtime":{"concurrencyValidated":true,"idempotencyValidated":true,"auditOutboxAtomicityValidated":true,"evidenceRef":"EV-RUN"},
"operations":{"backupMethod":"VERIFIED","backupRestoreValidated":true,"monitoringTarget":"VERIFIED","monitoringValidated":true,"evidenceRef":"EV-OPS"},
"security":{"noSecretsCommitted":true,"noPersonalDataCommitted":true,"reviewedByRole":"SECURITY","reviewDate":"2026-09-15"}}
""";
    return template.Replace("__EVIDENCE_CLASS__", evidenceClass, StringComparison.Ordinal);
}

void True(bool v) { if (!v) throw new Exception("Expected true"); }
void False(bool v) { if (v) throw new Exception("Expected false"); }
void Eq<T>(T expected, T actual) where T : notnull { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, actual {actual}"); }

sealed class MarkerGateway : ICommandGateway
{
    public Task<CommandAttemptResult> ExecuteAsync(CommandAttempt attempt, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CommandAttemptResult(200, "MARKER", "marker", false));
}
