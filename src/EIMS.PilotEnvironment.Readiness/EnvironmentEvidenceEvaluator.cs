using System.Text.Json;

namespace EIMS.PilotEnvironment.Readiness;

public sealed record EvidenceGate(
    string Id,
    bool Passed,
    string Code,
    IReadOnlyCollection<string> MissingOrInvalid);

public sealed record EnvironmentEvidenceReport(
    string SchemaVersion,
    string EvidenceClass,
    bool SchemaSupported,
    bool EvidenceClassSupported,
    bool SensitiveKeysDetected,
    IReadOnlyCollection<string> SensitiveKeyPaths,
    IReadOnlyCollection<EvidenceGate> Gates,
    bool PilotActivationReady)
{
    public int PassedGateCount => Gates.Count(x => x.Passed);
    public int TotalGateCount => Gates.Count;
}

public static class EnvironmentEvidenceEvaluator
{
    public const string CurrentSchemaVersion = "1.1";
    public const string PilotEvidenceClass = "PILOT_ENVIRONMENT_EVIDENCE";
    public const string LabEvidenceClass = "LAB_EVIDENCE";

    private static readonly HashSet<string> SupportedSchemaVersions = new(StringComparer.OrdinalIgnoreCase)
    {
        "1.0", "1.1"
    };

    private static readonly string[] ProhibitedKeyFragments =
    {
        "password", "passwd", "pwd", "secret", "token", "apikey", "api_key",
        "connectionstring", "connection_string", "privatekey", "private_key", "pfx", "pem",
        "nationalid", "national_id", "salary", "bankaccount", "bank_account"
    };

    public static EnvironmentEvidenceReport Evaluate(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Environment evidence root must be a JSON object.");

        var schemaVersion = ReadString(root, "schemaVersion") ?? string.Empty;
        var evidenceClass = ReadString(root, "evidenceClass") ?? string.Empty;
        var schemaSupported = SupportedSchemaVersions.Contains(schemaVersion);
        var classSupported = evidenceClass is PilotEvidenceClass or LabEvidenceClass;

        var sensitivePaths = new List<string>();
        FindSensitiveKeys(root, "$", sensitivePaths);

        var gates = new[]
        {
            Gate("WINDOWS_HOST", root,
                S("windows.serverName"),
                S("windows.osVersion"),
                T("windows.vmProvisioned"),
                T("windows.domainJoined"),
                S("windows.domainName"),
                T("windows.iisInstalled"),
                T("windows.windowsAuthenticationInstalled"),
                S("windows.dotnetRuntimeVersion"),
                S("windows.collectedAt")),

            Gate("ORACLE_P2", root,
                S("oracle.version"),
                S("oracle.providerName"),
                S("oracle.providerVersion"),
                S("oracle.connectionMode"),
                S("oracle.serviceAccountName"),
                S("oracle.schemaOwner"),
                T("oracle.liveConnectionValidated"),
                S("oracle.evidenceRef")),

            Gate("P1_AUTHORITY_PACKAGE", root,
                T("p1Authority.physicalPackageAvailable"),
                S("p1Authority.packageRef"),
                T("p1Authority.buildPassed"),
                T("p1Authority.contractTestsPassed"),
                S("p1Authority.evidenceRef")),

            Gate("WINDOWS_IDENTITY_P3", root,
                S("windowsIdentity.identityFormat"),
                T("windowsIdentity.liveDomainIdentityValidated"),
                T("windowsIdentity.personIdMappingValidated"),
                T("windowsIdentity.serverRoleScopeValidated"),
                F("windowsIdentity.clientIdentityHeadersTrusted"),
                S("windowsIdentity.evidenceRef")),

            Gate("HR_ORG_P4", root,
                S("hrOrg.sourceSystem"),
                S("hrOrg.exportOwnerRole"),
                T("hrOrg.realExportPrepared"),
                T("hrOrg.p4SchemaValidated"),
                T("hrOrg.reconciled"),
                S("hrOrg.approvalRef"),
                S("hrOrg.evidenceRef")),

            Gate("TLS", root,
                T("tls.configured"),
                S("tls.hostname"),
                S("tls.certificateSubject"),
                S("tls.validTo"),
                T("tls.liveHandshakeValidated"),
                S("tls.evidenceRef")),

            Gate("RUNTIME_PROOF", root,
                T("runtime.concurrencyValidated"),
                T("runtime.idempotencyValidated"),
                T("runtime.auditOutboxAtomicityValidated"),
                S("runtime.evidenceRef")),

            Gate("OPERATIONS", root,
                S("operations.backupMethod"),
                T("operations.backupRestoreValidated"),
                S("operations.monitoringTarget"),
                T("operations.monitoringValidated"),
                S("operations.evidenceRef")),

            Gate("SECURITY_EVIDENCE", root,
                T("security.noSecretsCommitted"),
                T("security.noPersonalDataCommitted"),
                S("security.reviewedByRole"),
                S("security.reviewDate"))
        };

        var ready = schemaSupported
            && string.Equals(evidenceClass, PilotEvidenceClass, StringComparison.Ordinal)
            && sensitivePaths.Count == 0
            && gates.All(x => x.Passed);

        return new EnvironmentEvidenceReport(
            schemaVersion,
            evidenceClass,
            schemaSupported,
            classSupported,
            sensitivePaths.Count > 0,
            sensitivePaths.AsReadOnly(),
            Array.AsReadOnly(gates),
            ready);
    }

    private static Requirement S(string path) => new(path, RequirementKind.NonBlankString);
    private static Requirement T(string path) => new(path, RequirementKind.TrueBoolean);
    private static Requirement F(string path) => new(path, RequirementKind.FalseBoolean);

    private static EvidenceGate Gate(string id, JsonElement root, params Requirement[] requirements)
    {
        var failures = requirements
            .Where(x => !Satisfied(root, x))
            .Select(x => x.Path)
            .ToArray();
        return failures.Length == 0
            ? new EvidenceGate(id, true, "PASS", Array.Empty<string>())
            : new EvidenceGate(id, false, "BLOCKED_EVIDENCE_REQUIRED", Array.AsReadOnly(failures));
    }

    private static bool Satisfied(JsonElement root, Requirement requirement)
    {
        if (!TryResolve(root, requirement.Path, out var value))
            return false;
        return requirement.Kind switch
        {
            RequirementKind.NonBlankString => value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()),
            RequirementKind.TrueBoolean => value.ValueKind is JsonValueKind.True,
            RequirementKind.FalseBoolean => value.ValueKind is JsonValueKind.False,
            _ => false
        };
    }

    private static bool TryResolve(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                return false;
        }
        return true;
    }

    private static string? ReadString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static void FindSensitiveKeys(JsonElement element, string path, ICollection<string> findings)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var key = property.Name.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
                if (ProhibitedKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                    findings.Add($"{path}.{property.Name}");
                FindSensitiveKeys(property.Value, $"{path}.{property.Name}", findings);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in element.EnumerateArray())
                FindSensitiveKeys(item, $"{path}[{i++}]", findings);
        }
    }

    private enum RequirementKind
    {
        NonBlankString,
        TrueBoolean,
        FalseBoolean
    }

    private sealed record Requirement(string Path, RequirementKind Kind);
}
