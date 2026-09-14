using EIMS.Authority.Recovery;

namespace EIMS.Persistence.Recovery;

public enum PersistenceFaultPoint
{
    None = 0,
    AfterStateStaged = 1,
    AfterAuditStaged = 2,
    AfterOutboxStaged = 3,
    AfterIdempotencyStaged = 4,
    BeforeCommitPublish = 5
}

public sealed class PersistenceAtomicityException(string message) : Exception(message);

public sealed record OracleBindingEvidence(
    string? OracleVersion,
    string? DotNetProvider,
    string? ConnectivityMode,
    string? ServiceAccountModel,
    string? SchemaOwner,
    string? EvidenceReference)
{
    public bool IsPhysicalBindingReady =>
        !string.IsNullOrWhiteSpace(OracleVersion)
        && !string.IsNullOrWhiteSpace(DotNetProvider)
        && !string.IsNullOrWhiteSpace(ConnectivityMode)
        && !string.IsNullOrWhiteSpace(ServiceAccountModel)
        && !string.IsNullOrWhiteSpace(SchemaOwner)
        && !string.IsNullOrWhiteSpace(EvidenceReference);

    public IReadOnlyCollection<string> MissingEvidence
    {
        get
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(OracleVersion)) missing.Add(nameof(OracleVersion));
            if (string.IsNullOrWhiteSpace(DotNetProvider)) missing.Add(nameof(DotNetProvider));
            if (string.IsNullOrWhiteSpace(ConnectivityMode)) missing.Add(nameof(ConnectivityMode));
            if (string.IsNullOrWhiteSpace(ServiceAccountModel)) missing.Add(nameof(ServiceAccountModel));
            if (string.IsNullOrWhiteSpace(SchemaOwner)) missing.Add(nameof(SchemaOwner));
            if (string.IsNullOrWhiteSpace(EvidenceReference)) missing.Add(nameof(EvidenceReference));
            return missing.AsReadOnly();
        }
    }

    public static OracleBindingEvidence Unbound() => new(null, null, null, null, null, null);
}

public sealed record PersistenceContractDescriptor(
    string ContractId,
    string Version,
    bool OptimisticConcurrencyRequired,
    bool ServerIdempotencyRequired,
    bool AppendOnlyAuditRequired,
    bool AtomicStateAuditOutboxIdempotencyRequired,
    bool ConnectionSecretsAllowedInContract,
    OracleBindingEvidence OracleBinding)
{
    public bool IsLogicalContractReady =>
        OptimisticConcurrencyRequired
        && ServerIdempotencyRequired
        && AppendOnlyAuditRequired
        && AtomicStateAuditOutboxIdempotencyRequired
        && !ConnectionSecretsAllowedInContract;

    public bool IsPhysicalOracleReady => IsLogicalContractReady && OracleBinding.IsPhysicalBindingReady;

    public static PersistenceContractDescriptor RecoveryBaseline() => new(
        "EIMS-P2-PERSISTENCE-RECOVERY",
        "1.0",
        OptimisticConcurrencyRequired: true,
        ServerIdempotencyRequired: true,
        AppendOnlyAuditRequired: true,
        AtomicStateAuditOutboxIdempotencyRequired: true,
        ConnectionSecretsAllowedInContract: false,
        OracleBindingEvidence.Unbound());
}

public interface IPersistenceEvidenceSource
{
    PersistenceContractDescriptor Contract { get; }
    IReadOnlyCollection<AuditEnvelope> AuditLog { get; }
    IReadOnlyCollection<OutboxEnvelope> Outbox { get; }
    IReadOnlyCollection<IdempotencyRecord> IdempotencyRecords { get; }
}

public interface IFaultInjectablePersistence
{
    PersistenceFaultPoint FaultPoint { get; set; }
}
