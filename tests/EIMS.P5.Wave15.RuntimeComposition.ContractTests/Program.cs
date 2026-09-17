using System.Text.Json;
using EIMS.Persistence.Recovery;
using EIMS.PilotAssembly.Activation;
using EIMS.PilotAssembly.Binding;
using EIMS.PilotAssembly.Core;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("W15-01 missing production candidate remains fail closed", MissingCandidate),
    ("W15-02 config-only activation has no authority surface", ConfigOnlyCannotActivate),
    ("W15-03 fail-closed gateway candidate cannot be promoted", FailClosedCandidate),
    ("W15-04 stale P1-P5 binding contract fails closed", ContractMismatch),
    ("W15-05 P1 contract evidence is mandatory", P1EvidenceRequired),
    ("W15-06 physical Oracle P2 evidence is mandatory", PhysicalP2Required),
    ("W15-07 authoritative live P3 evidence is mandatory", PhysicalP3Required),
    ("W15-08 malformed OP04 evidence fails closed", MalformedOp04),
    ("W15-09 LAB evidence can never activate production", LabCannotActivateProduction),
    ("W15-10 blocked OP04 gate cannot activate production", BlockedOp04),
    ("W15-11 sensitive evidence keys fail closed", SensitiveEvidenceFailsClosed),
    ("W15-12 composition evidence reference is mandatory", CompositionEvidenceRefRequired),
    ("W15-13 fully evidenced production candidate activates exact gateway", ProductionHappy),
    ("W15-14 lab wiring can be assessed without becoming production evidence", LabAssessment),
    ("W15-15 fail-closed decision performs no domain mutation", FailClosedNoMutation)
};

var passed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"RESULT {passed}/{tests.Count} PASS");
return passed == tests.Count ? 0 : 1;

static Task MissingCandidate()
{
    var decision = RuntimeCompositionActivator.SelectProduction(null);
    False(decision.Activated);
    Equal("P5_RUNTIME_COMPOSITION_NOT_SUPPLIED", decision.Code);
    True(decision.Gateway is FailClosedCommandGateway);
    return Task.CompletedTask;
}

static Task ConfigOnlyCannotActivate()
{
    var legacyRuntimeBoundConfig = true;
    var provider = new NoRuntimeCompositionCandidateProvider();
    True(legacyRuntimeBoundConfig);
    var decision = RuntimeCompositionActivator.SelectProduction(provider.GetCandidate());
    False(decision.Activated);
    Equal("P5_RUNTIME_COMPOSITION_NOT_SUPPLIED", decision.Code);
    return Task.CompletedTask;
}

static Task FailClosedCandidate()
{
    var decision = RuntimeCompositionActivator.SelectProduction(
        Candidate(gateway: new FailClosedCommandGateway()));
    False(decision.Activated);
    Equal("P5_RUNTIME_GATEWAY_FAIL_CLOSED_CANDIDATE", decision.Code);
    return Task.CompletedTask;
}

static Task ContractMismatch()
{
    var decision = RuntimeCompositionActivator.SelectProduction(
        Candidate(bindingVersion: "P1P5-STALE"));
    False(decision.Activated);
    Equal("P5_RUNTIME_BINDING_CONTRACT_MISMATCH", decision.Code);
    return Task.CompletedTask;
}

static Task P1EvidenceRequired()
{
    var decision = RuntimeCompositionActivator.SelectProduction(
        Candidate(p1TestsPassed: false));
    False(decision.Activated);
    Equal("P5_RUNTIME_P1_CONTRACT_EVIDENCE_REQUIRED", decision.Code);
    return Task.CompletedTask;
}

static Task PhysicalP2Required()
{
    var decision = RuntimeCompositionActivator.SelectProduction(
        Candidate(persistence: PersistenceContractDescriptor.RecoveryBaseline()));
    False(decision.Activated);
    Equal("P5_RUNTIME_P2_PHYSICAL_BINDING_REQUIRED", decision.Code);
    return Task.CompletedTask;
}

static Task PhysicalP3Required()
{
    var decision = RuntimeCompositionActivator.SelectProduction(
        Candidate(identity: IdentityRuntimeBindingEvidence.Unbound()));
    False(decision.Activated);
    Equal("P5_RUNTIME_P3_PHYSICAL_BINDING_REQUIRED", decision.Code);
    return Task.CompletedTask;
}

static Task MalformedOp04()
{
    var decision = RuntimeCompositionActivator.SelectProduction(
        Candidate(environmentJson: "{not-json"));
    False(decision.Activated);
    Equal("P5_RUNTIME_OP04_EVIDENCE_INVALID", decision.Code);
    return Task.CompletedTask;
}

static Task LabCannotActivateProduction()
{
    var decision = RuntimeCompositionActivator.SelectProduction(
        Candidate(environmentJson: Evidence("LAB_EVIDENCE")));
    False(decision.Activated);
    Equal("P5_RUNTIME_OP04_PILOT_EVIDENCE_REQUIRED", decision.Code);
    return Task.CompletedTask;
}

static Task BlockedOp04()
{
    var json = Evidence("PILOT_ENVIRONMENT_EVIDENCE")
        .Replace("\"liveConnectionValidated\":true", "\"liveConnectionValidated\":false", StringComparison.Ordinal);
    var decision = RuntimeCompositionActivator.SelectProduction(Candidate(environmentJson: json));
    False(decision.Activated);
    Equal("P5_RUNTIME_OP04_PILOT_EVIDENCE_REQUIRED", decision.Code);
    return Task.CompletedTask;
}

static Task SensitiveEvidenceFailsClosed()
{
    var json = Evidence("PILOT_ENVIRONMENT_EVIDENCE")
        .Replace("\"reviewDate\":\"2026-09-17\"", "\"reviewDate\":\"2026-09-17\",\"deploymentSecret\":\"do-not-use\"", StringComparison.Ordinal);
    var decision = RuntimeCompositionActivator.SelectProduction(Candidate(environmentJson: json));
    False(decision.Activated);
    Equal("P5_RUNTIME_OP04_PILOT_EVIDENCE_REQUIRED", decision.Code);
    return Task.CompletedTask;
}

static Task CompositionEvidenceRefRequired()
{
    var decision = RuntimeCompositionActivator.SelectProduction(Candidate(evidenceRef: null));
    False(decision.Activated);
    Equal("P5_RUNTIME_COMPOSITION_EVIDENCE_REF_REQUIRED", decision.Code);
    return Task.CompletedTask;
}

static Task ProductionHappy()
{
    var gateway = new ProbeGateway();
    var decision = RuntimeCompositionActivator.SelectProduction(Candidate(gateway: gateway));
    True(decision.Activated);
    Equal("P5_RUNTIME_COMPOSITION_ACTIVE", decision.Code);
    True(ReferenceEquals(gateway, decision.Gateway));
    return Task.CompletedTask;
}

static Task LabAssessment()
{
    var candidate = Candidate(
        persistence: PersistenceContractDescriptor.RecoveryBaseline(),
        identity: IdentityRuntimeBindingEvidence.Unbound(),
        environmentJson: Evidence("LAB_EVIDENCE"));
    var lab = RuntimeCompositionActivator.AssessLab(candidate);
    True(lab.WiringAccepted);
    Equal("P5_LAB_COMPOSITION_WIRING_ACCEPTED_NON_PRODUCTION", lab.Code);
    var production = RuntimeCompositionActivator.SelectProduction(candidate);
    False(production.Activated);
    return Task.CompletedTask;
}

static async Task FailClosedNoMutation()
{
    var decision = RuntimeCompositionActivator.SelectProduction(null);
    var result = await decision.Gateway.ExecuteAsync(
        new CommandAttempt("knowledge.publish", "DOMAIN\\user", "CORR", 1, "IDEM", "{}"));
    Equal(503, result.HttpStatus);
    Equal("P5_COMMAND_GATEWAY_NOT_BOUND", result.Code);
    False(result.StateMutated);
}

static RuntimeCompositionCandidate Candidate(
    ICommandGateway? gateway = null,
    PersistenceContractDescriptor? persistence = null,
    IdentityRuntimeBindingEvidence? identity = null,
    string? environmentJson = null,
    string? bindingVersion = null,
    bool p1TestsPassed = true,
    string? evidenceRef = "RUNTIME-COMPOSITION-EV") =>
    new(
        gateway ?? new ProbeGateway(),
        persistence ?? PhysicalPersistence(),
        identity ?? PhysicalIdentity(),
        environmentJson ?? Evidence("PILOT_ENVIRONMENT_EVIDENCE"),
        bindingVersion ?? P1P5BindingContract.Version,
        p1TestsPassed,
        evidenceRef);

static PersistenceContractDescriptor PhysicalPersistence() =>
    PersistenceContractDescriptor.RecoveryBaseline() with
    {
        OracleBinding = new OracleBindingEvidence(
            "19c",
            "ODP.NET",
            "TCPS",
            "DOMAIN_SERVICE_ACCOUNT",
            "EIMS_PILOT",
            "P2-EVIDENCE-REF")
    };

static IdentityRuntimeBindingEvidence PhysicalIdentity() =>
    new(
        "IIS_WINDOWS_AUTH",
        "ORGANIZATION_DIRECTORY",
        true,
        true,
        true,
        false,
        "P3-EVIDENCE-REF");

static string Evidence(string evidenceClass) => JsonSerializer.Serialize(new
{
    schemaVersion = "1.1",
    evidenceClass,
    windows = new
    {
        serverName = "PILOT-SERVER",
        osVersion = "WINDOWS_SERVER",
        vmProvisioned = true,
        domainJoined = true,
        domainName = "CORP",
        iisInstalled = true,
        windowsAuthenticationInstalled = true,
        dotnetRuntimeVersion = "10.0",
        collectedAt = "2026-09-17T00:00:00Z"
    },
    oracle = new
    {
        version = "19c",
        providerName = "ODP.NET",
        providerVersion = "TESTED",
        connectionMode = "TCPS",
        serviceAccountName = "EIMS-SVC",
        schemaOwner = "EIMS_PILOT",
        liveConnectionValidated = true,
        evidenceRef = "P2-EV"
    },
    p1Authority = new
    {
        physicalPackageAvailable = true,
        packageRef = "P1-PKG",
        buildPassed = true,
        contractTestsPassed = true,
        evidenceRef = "P1-EV"
    },
    windowsIdentity = new
    {
        identityFormat = "DOMAIN\\user",
        liveDomainIdentityValidated = true,
        personIdMappingValidated = true,
        serverRoleScopeValidated = true,
        clientIdentityHeadersTrusted = false,
        evidenceRef = "P3-EV"
    },
    hrOrg = new
    {
        sourceSystem = "ORACLE_HR",
        exportOwnerRole = "HRIT",
        realExportPrepared = true,
        p4SchemaValidated = true,
        reconciled = true,
        approvalRef = "HR-APPROVAL",
        evidenceRef = "P4-EV"
    },
    tls = new
    {
        configured = true,
        hostname = "eims-pilot.corp",
        certificateSubject = "CN=eims-pilot.corp",
        validTo = "2027-09-17",
        liveHandshakeValidated = true,
        evidenceRef = "TLS-EV"
    },
    runtime = new
    {
        concurrencyValidated = true,
        idempotencyValidated = true,
        auditOutboxAtomicityValidated = true,
        evidenceRef = "RUNTIME-EV"
    },
    operations = new
    {
        backupMethod = "ORACLE_BACKUP",
        backupRestoreValidated = true,
        monitoringTarget = "EIMS_HOST",
        monitoringValidated = true,
        evidenceRef = "OPS-EV"
    },
    security = new
    {
        noSecretsCommitted = true,
        noPersonalDataCommitted = true,
        reviewedByRole = "SECURITY",
        reviewDate = "2026-09-17"
    }
});

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}

static void False(bool value)
{
    if (value) throw new InvalidOperationException("Expected false.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
}

sealed class ProbeGateway : ICommandGateway
{
    public int Attempts { get; private set; }

    public Task<CommandAttemptResult> ExecuteAsync(CommandAttempt attempt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Attempts++;
        return Task.FromResult(new CommandAttemptResult(200, "PROBE_OK", "Probe gateway invoked.", true));
    }
}
