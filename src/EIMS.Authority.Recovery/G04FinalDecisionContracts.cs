namespace EIMS.Authority.Recovery;

public sealed record G04FinalDecisionCommand(
    string IdeaId,
    long ExpectedIdeaRevision,
    long ExpectedStateMutationVersion,
    string PlanId,
    int ExpectedPlanVersion,
    string AssessmentId,
    int ExpectedAssessmentDecisionVersion,
    string Outcome,
    string ReasonCode,
    string DecisionComment,
    DateTimeOffset? ReviewDate,
    string IdempotencyKey,
    string CorrelationId,
    string? RequestedScope = null);

public sealed record G04DecisionProfileEvidence(
    string ProfileId,
    string ProfileVersion,
    string ProfileSnapshotJson,
    string ProfileSnapshotSha256,
    string RuleSet,
    string HardConditionMappingVersion,
    int GateScore,
    int PassThreshold,
    int DataReadiness,
    int DataThreshold,
    bool BaseNeedPresent,
    bool TechnicalRequirementSatisfied,
    bool HardConditionsAllMappedAndPass,
    bool SpecialistOutcomesNonBlocking,
    string RuleCheckResults,
    string SpecialistOutcomeSummary,
    string EvaluatorConflictPolicy = "PASS");

public interface IG04FinalDecisionEvidenceProvider
{
    ValueTask<G04DecisionProfileEvidence?> ResolveAsync(
        AggregateSnapshot idea,
        EvaluationPlanEnvelope plan,
        G04AssessmentEnvelope assessment,
        CancellationToken cancellationToken = default);
}

public sealed class StaticG04FinalDecisionEvidenceProvider(G04DecisionProfileEvidence evidence)
    : IG04FinalDecisionEvidenceProvider
{
    public ValueTask<G04DecisionProfileEvidence?> ResolveAsync(
        AggregateSnapshot idea,
        EvaluationPlanEnvelope plan,
        G04AssessmentEnvelope assessment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<G04DecisionProfileEvidence?>(evidence);
    }
}

public sealed record G04FinalDecisionEvidenceEnvelope(
    string DecisionId,
    string AssessmentId,
    string PlanId,
    string IdeaId,
    long IdeaRevisionBefore,
    long IdeaRevisionAfter,
    long StateMutationVersionBefore,
    long StateMutationVersionAfter,
    int AssessmentDecisionVersionBefore,
    int AssessmentDecisionVersionAfter,
    string Outcome,
    string ReasonCode,
    string DecisionComment,
    DateTimeOffset? ReviewDate,
    string PersonId,
    string AuthorityAssignmentId,
    string NetworkIdentity,
    string IdentitySource,
    string DecisionRoute,
    string DecisionRouteKind,
    string DecisionMethod,
    string GovernanceProfileId,
    string GovernanceProfileVersion,
    string DecisionProfileId,
    string DecisionProfileVersion,
    string ProfileSnapshotJson,
    string ProfileSnapshotSha256,
    string RuleSet,
    string HardConditionMappingVersion,
    int GateScore,
    int DataReadiness,
    string RuleCheckResults,
    string SpecialistOutcomeSummary,
    string? CommitteeSnapshotId,
    int? CommitteeVersion,
    string? CommitteeStageState,
    bool? CommitteeApprovalRuleSatisfied,
    string SodResult,
    string? SodExceptionPolicy,
    DateTimeOffset Timestamp,
    string CorrelationId);

public sealed record G04FinalDecisionRequest(
    G04FinalDecisionCommand Command,
    AuthorityActor Actor,
    AggregateSnapshot Idea,
    EvaluationPlanEnvelope Plan,
    G04AssessmentEnvelope Assessment,
    string IdempotencyFingerprint);

public sealed record G04FinalDecisionCommit(
    AggregateSnapshot IdeaAfter,
    EvaluationPlanEnvelope PlanAfter,
    G04AssessmentEnvelope AssessmentAfter,
    G04FinalDecisionEvidenceEnvelope Evidence,
    AuditEnvelope Audit,
    OutboxEnvelope Outbox);

public interface IG04FinalDecisionStore
{
    ValueTask<AggregateSnapshot?> GetIdeaAsync(string ideaId, CancellationToken cancellationToken = default);
    ValueTask<EvaluationPlanEnvelope?> GetPlanAsync(string planId, CancellationToken cancellationToken = default);
    ValueTask<G04AssessmentEnvelope?> GetAssessmentAsync(string assessmentId, CancellationToken cancellationToken = default);
    ValueTask<G04CommitteeSnapshotEnvelope?> GetCommitteeSnapshotAsync(string assessmentId, CancellationToken cancellationToken = default);
    ValueTask<G04CommitteeStateEnvelope?> GetCommitteeStateAsync(string assessmentId, CancellationToken cancellationToken = default);
    ValueTask<IdempotencyRecord?> GetFinalDecisionIdempotencyAsync(string assessmentId, string idempotencyKey, CancellationToken cancellationToken = default);
    ValueTask<AuthorityResult> CommitFinalDecisionAsync(G04FinalDecisionRequest request, G04FinalDecisionCommit commit, CancellationToken cancellationToken = default);
}
