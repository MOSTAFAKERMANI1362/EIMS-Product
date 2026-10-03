using EIMS.Authority.Recovery;

namespace EIMS.Persistence.Recovery;

public enum PersistenceFaultPoint
{
    None = 0,
    AfterStateStaged = 1,
    AfterAuditStaged = 2,
    AfterOutboxStaged = 3,
    AfterIdempotencyStaged = 4,
    BeforeCommitPublish = 5,
    AfterDecisionStaged = 6,
    AfterEvaluationPlanStaged = 7,
    AfterEvaluationAssignmentsStaged = 8,
    AfterAssessmentSnapshotStaged = 9,
    AfterEvaluationAssignmentCompletionStaged = 10,
    AfterEvaluationPlanReadinessStaged = 11,
    AfterG04AssessmentStaged = 12,
    AfterCompletionOutboxStaged = 13
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
    // Wave 4 extends the logical transaction with immutable domain decision history.
    // Wave 5 extends the same transaction boundary with Idea Evaluation Plan + Assignments.
    // Wave 6 adds immutable evaluator assessment evidence + assignment completion + Plan readiness + optional G04Assessment.
    // Historical constructor shape remains unchanged for prior recovery evidence.
    public bool AppendOnlyDecisionHistoryRequired => true;
    public bool AtomicStateDecisionAuditOutboxIdempotencyRequired => AtomicStateAuditOutboxIdempotencyRequired;
    public bool AtomicEvaluationPlanAssignmentsSupported => true;
    public bool AtomicEvaluationCompletionSupported => true;
    public bool AppendOnlyAssessmentSnapshotRequired => true;

    public bool IsLogicalContractReady =>
        OptimisticConcurrencyRequired
        && ServerIdempotencyRequired
        && AppendOnlyAuditRequired
        && AppendOnlyDecisionHistoryRequired
        && AppendOnlyAssessmentSnapshotRequired
        && AtomicStateAuditOutboxIdempotencyRequired
        && AtomicStateDecisionAuditOutboxIdempotencyRequired
        && AtomicEvaluationPlanAssignmentsSupported
        && AtomicEvaluationCompletionSupported
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
    IReadOnlyCollection<DomainDecisionEnvelope> DomainDecisions { get; }
    IReadOnlyCollection<G01DecisionSnapshotEnvelope> DecisionSnapshots { get; }
    IReadOnlyCollection<EvaluationPlanEnvelope> EvaluationPlans { get; }
    IReadOnlyCollection<EvaluationAssignmentEnvelope> EvaluationAssignments { get; }
    IReadOnlyCollection<AssessmentSnapshotEnvelope> AssessmentSnapshots { get; }
    IReadOnlyCollection<G04AssessmentEnvelope> G04Assessments { get; }
}

public interface IFaultInjectablePersistence
{
    PersistenceFaultPoint FaultPoint { get; set; }
}
