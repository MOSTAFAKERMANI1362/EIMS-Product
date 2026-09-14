using System.Security.Claims;
using System.Security.Cryptography;

namespace EIMS.PilotAssembly.Core;

public static class PilotBaseline
{
    public const string ProductBaseline = "EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html";
    public const string ProductSha256 = "057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a";
    public const string P4ExpectedSha256 = "5a38c36c6078b8e317d61214b8b30e483326348441e816cd43e30423f65b2efc";
    public const string Version = "P5-1.0.0";
}

public enum PilotGateStatus { Ready, Blocked, TestRequired }

public sealed record PilotGate(string Id, PilotGateStatus Status, string Evidence, string Owner, string? Blocker = null);

public sealed record PilotBindingSnapshot(
    bool P1AuthorityRuntimeBound,
    bool P1ContractTestsPassed,
    string? P1PackagePath,
    string? OracleVersion,
    string? OracleProvider,
    string? OracleConnectionMode,
    string? OracleServiceAccount,
    string? OracleSchemaOwner,
    bool OracleLiveConnectionValidated,
    string WindowsHostingMode,
    bool RequireAuthenticatedWindowsUser,
    bool TrustClientIdentityHeaders,
    bool WindowsIdentityLiveValidated,
    bool WindowsServerVmProvisioned,
    bool TlsCertificateConfigured,
    bool TlsHandshakeValidated,
    string? P4PackagePath,
    string P4ExpectedSha256,
    bool P4RealExportReconciled,
    bool ConcurrencyIdempotencyValidated,
    bool AuditOutboxAtomicityValidated,
    bool BackupRestoreValidated,
    bool MonitoringValidated);

public static class PilotReadinessEvaluator
{
    public static IReadOnlyList<PilotGate> Evaluate(PilotBindingSnapshot b)
    {
        var oracleConfigured = AllPresent(b.OracleVersion,b.OracleProvider,b.OracleConnectionMode,b.OracleServiceAccount,b.OracleSchemaOwner);
        var windowsConfigured = string.Equals(b.WindowsHostingMode,"IIS_WINDOWS_AUTH",StringComparison.OrdinalIgnoreCase)
                                && b.RequireAuthenticatedWindowsUser && !b.TrustClientIdentityHeaders;
        var gates = new List<PilotGate>
        {
            new("baseline.freeze", PilotGateStatus.Ready,
                $"{PilotBaseline.ProductBaseline} / {PilotBaseline.ProductSha256}", "EIMS Architecture"),
            BinaryGate(b.P1AuthorityRuntimeBound, "p1.authority.runtime", "P1 server-authority runtime physically composed", "Development",
                "Physical P1 source/runtime is not bound to this assembly."),
            EvidenceGate(b.P1AuthorityRuntimeBound, b.P1ContractTestsPassed, "p1.authority.contract-tests",
                "P1 compile/contract-test evidence on .NET 10", "Development"),
            BinaryGate(oracleConfigured, "p2.oracle.binding", "Oracle version/provider/connectivity/service-account/schema-owner", "DBA",
                "Oracle environment binding is incomplete."),
            EvidenceGate(oracleConfigured, b.OracleLiveConnectionValidated, "p2.oracle.live",
                "Live Oracle connection and transaction probe", "DBA/Development"),
            BinaryGate(windowsConfigured, "p3.windows.config",
                "IIS Windows Authentication; anonymous denied; client identity headers untrusted", "IT/Security",
                "Windows Integrated Authentication hosting contract is incomplete or unsafe."),
            EvidenceGate(windowsConfigured, b.WindowsIdentityLiveValidated, "p3.windows.live",
                "Live DOMAIN account → authenticated principal → unique PersonID → effective Role/Scope", "IT/Security/HRIT"),
            BinaryGate(b.WindowsServerVmProvisioned, "infra.windows.vm", "Pilot Windows Server/VM provisioned", "IT",
                "Pilot Windows Server/VM has not been provisioned/verified."),
            BinaryGate(b.TlsCertificateConfigured, "security.tls.config", "TLS certificate configured", "Security/IT",
                "TLS/certificate binding is not complete."),
            EvidenceGate(b.TlsCertificateConfigured, b.TlsHandshakeValidated, "security.tls.live",
                "Live TLS handshake and certificate validation", "Security/IT"),
            EvaluateP4(b.P4PackagePath,b.P4ExpectedSha256),
            EvidenceGate(true, b.P4RealExportReconciled, "p4.hrorg.real-export",
                "Approved real HR/Org export validated, reconciled and activation-ready", "HRIT/EIMS"),
            EvidenceGate(true, b.ConcurrencyIdempotencyValidated, "runtime.concurrency-idempotency",
                "Optimistic concurrency + idempotent replay live evidence", "Development/DBA"),
            EvidenceGate(true, b.AuditOutboxAtomicityValidated, "runtime.audit-outbox",
                "State + Audit + Outbox atomic commit/rollback evidence", "Development/DBA"),
            EvidenceGate(true, b.BackupRestoreValidated, "ops.backup-restore",
                "Backup/restore drill evidence", "IT/DBA"),
            EvidenceGate(true, b.MonitoringValidated, "ops.monitoring",
                "Operational logging/monitoring/alert evidence", "IT")
        };
        return gates;
    }

    public static bool IsNetworkPilotReady(IReadOnlyList<PilotGate> gates) =>
        gates.Count > 0 && gates.All(x => x.Status == PilotGateStatus.Ready);

    private static PilotGate EvaluateP4(string? path, string expected)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new("p4.hrorg.package", PilotGateStatus.Blocked, "P4 package missing", "HRIT/Integration",
                "P4 HR/Organization Offline Import package is not available at configured path.");
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var ok = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        return new("p4.hrorg.package", ok ? PilotGateStatus.Ready : PilotGateStatus.Blocked,
            $"SHA-256={actual}", "HRIT/Integration", ok ? null : "P4 package hash mismatch.");
    }

    private static PilotGate BinaryGate(bool ready,string id,string evidence,string owner,string blocker) =>
        new(id, ready ? PilotGateStatus.Ready : PilotGateStatus.Blocked, evidence, owner, ready ? null : blocker);

    private static PilotGate EvidenceGate(bool prerequisite, bool evidencePresent,string id,string evidence,string owner)
    {
        if (!prerequisite) return new(id, PilotGateStatus.Blocked, evidence, owner, "Prerequisite binding is incomplete.");
        return evidencePresent
            ? new(id, PilotGateStatus.Ready, evidence, owner)
            : new(id, PilotGateStatus.TestRequired, evidence, owner, "Live evidence has not yet been recorded.");
    }

    private static bool AllPresent(params string?[] values) => values.All(v => !string.IsNullOrWhiteSpace(v));
}

public static class IdentitySourcePolicy
{
    public static string? GetAuthenticatedNetworkName(ClaimsPrincipal principal, IReadOnlyDictionary<string,string>? clientHeaders = null)
    {
        var identity = principal.Identity;
        if (identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(identity.Name)) return null;
        return identity.Name.Trim();
    }
}

public sealed record CommandAttempt(string Command,string? NetworkIdentity,string? CorrelationId,long? ExpectedVersion,string? IdempotencyKey,string RawBody);
public sealed record CommandAttemptResult(int HttpStatus,string Code,string Message,bool StateMutated);

public interface ICommandGateway
{
    Task<CommandAttemptResult> ExecuteAsync(CommandAttempt attempt, CancellationToken cancellationToken = default);
}

public sealed class FailClosedCommandGateway : ICommandGateway
{
    public Task<CommandAttemptResult> ExecuteAsync(CommandAttempt attempt, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CommandAttemptResult(503, "P5_COMMAND_GATEWAY_NOT_BOUND",
            "P1 server-authority runtime is not physically bound. No domain mutation was attempted.", false));
}
