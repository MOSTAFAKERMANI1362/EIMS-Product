using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EIMS.Authority.Recovery;

public sealed class G04FinalDecisionService(
    IG04FinalDecisionStore store,
    IG04FinalDecisionEvidenceProvider evidenceProvider,
    ICommandPolicyCatalog? catalog = null)
{
    private readonly ICommandPolicyCatalog _catalog = catalog ?? new RecoveredApiCommandCatalogWave8();

    private static readonly HashSet<string> AllowedOutcomes = new(StringComparer.Ordinal)
    {
        "APPROVE", "RETURN", "HOLD", "REJECT"
    };

    private static readonly HashSet<string> AllowedReasonCodes = new(StringComparer.Ordinal)
    {
        "G04_READY", "G04_SOLUTION_INCOMPLETE", "G04_EVIDENCE_INCOMPLETE", "G04_COST_INCOMPLETE",
        "G04_RISK_UNRESOLVED", "G04_TECH_FAIL", "G04_ALIGNMENT", "OTHER"
    };

    public async ValueTask<AuthorityResult> DecideAsync(
        G04FinalDecisionCommand command,
        AuthorityActor? actor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.PersonId)
            || string.IsNullOrWhiteSpace(actor.NetworkIdentity)
            || string.IsNullOrWhiteSpace(actor.IdentitySource)
            || string.IsNullOrWhiteSpace(actor.AssignmentId))
            return AuthorityResult.Deny(401, "P1_IDENTITY_ASSIGNMENT_REQUIRED", command.CorrelationId);

        if (string.IsNullOrWhiteSpace(command.IdeaId)
            || string.IsNullOrWhiteSpace(command.PlanId)
            || string.IsNullOrWhiteSpace(command.AssessmentId)
            || string.IsNullOrWhiteSpace(command.CorrelationId))
            return AuthorityResult.Deny(400, "P1_G04_FINAL_CONTEXT_REQUIRED", command.CorrelationId);
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            return AuthorityResult.Deny(400, "P1_IDEMPOTENCY_KEY_REQUIRED", command.CorrelationId);

        if (!_catalog.TryGet("g04.final-decision", out var policy)
            || !policy.StateContractRecovered
            || !policy.RuleContractRecovered
            || !policy.EventContractRecovered
            || !policy.MutationContractRecovered)
            return AuthorityResult.Deny(503, "P1_G04_FINAL_DECISION_CONTRACT_NOT_BOUND", command.CorrelationId);

        var actorRoles = actor.Roles
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (actorRoles.Length != 1 || !string.Equals(actorRoles[0], "IDEA_DECISION", StringComparison.OrdinalIgnoreCase))
            return AuthorityResult.Deny(403, "P1_G04_FINAL_AUTHORITY_REQUIRED", command.CorrelationId);

        var normalized = NormalizeInput(command);
        if (!AllowedOutcomes.Contains(normalized.Outcome))
            return AuthorityResult.Deny(422, "P1_G04_FINAL_OUTCOME_INVALID", command.CorrelationId);
        if (!AllowedReasonCodes.Contains(normalized.ReasonCode))
            return AuthorityResult.Deny(422, "P1_G04_FINAL_REASON_INVALID", command.CorrelationId);
        if (normalized.DecisionComment.Length < 15)
            return AuthorityResult.Deny(422, "P1_G04_FINAL_COMMENT_REQUIRED", command.CorrelationId);
        if (normalized.Outcome == "APPROVE" && normalized.ReasonCode != "G04_READY")
            return AuthorityResult.Deny(422, "P1_G04_APPROVE_REASON_MUST_BE_READY", command.CorrelationId);
        if (normalized.Outcome != "APPROVE" && normalized.ReasonCode == "G04_READY")
            return AuthorityResult.Deny(422, "P1_G04_NEGATIVE_REASON_CANNOT_BE_READY", command.CorrelationId);
        if (normalized.Outcome == "HOLD" && normalized.ReviewDate is null)
            return AuthorityResult.Deny(422, "P1_G04_HOLD_REVIEW_DATE_REQUIRED", command.CorrelationId);

        var idea = await store.GetIdeaAsync(command.IdeaId, cancellationToken);
        var plan = await store.GetPlanAsync(command.PlanId, cancellationToken);
        var assessment = await store.GetAssessmentAsync(command.AssessmentId, cancellationToken);
        if (idea is null || !string.Equals(idea.AggregateType, "Idea", StringComparison.OrdinalIgnoreCase))
            return AuthorityResult.Deny(404, "P1_IDEA_NOT_FOUND", command.CorrelationId);
        if (plan is null)
            return AuthorityResult.Deny(404, "P1_EVALUATION_PLAN_NOT_FOUND", command.CorrelationId);
        if (assessment is null)
            return AuthorityResult.Deny(404, "P1_G04_ASSESSMENT_NOT_FOUND", command.CorrelationId);

        if (!ContextMatches(idea, plan, assessment))
            return AuthorityResult.Deny(409, "P1_G04_FINAL_CONTEXT_MISMATCH", command.CorrelationId);
        if (!G04RouteIntegrity.HasCompleteRoute(plan)
            || !G04RouteIntegrity.HasCompleteRoute(assessment)
            || !G04RouteIntegrity.PlanAssessmentMatch(plan, assessment))
            return AuthorityResult.Deny(409, "P1_G04_FINAL_ROUTE_MISMATCH", command.CorrelationId);

        if (string.IsNullOrWhiteSpace(idea.Scope)
            || !actor.Scopes.Contains(idea.Scope, StringComparer.OrdinalIgnoreCase))
            return AuthorityResult.Deny(403, "P1_G04_FINAL_SCOPE_DENIED", command.CorrelationId);
        if (!string.IsNullOrWhiteSpace(command.RequestedScope)
            && (!actor.Scopes.Contains(command.RequestedScope.Trim(), StringComparer.OrdinalIgnoreCase)
                || !string.Equals(command.RequestedScope.Trim(), idea.Scope, StringComparison.OrdinalIgnoreCase)))
            return AuthorityResult.Deny(403, "P1_G04_FINAL_SCOPE_DENIED", command.CorrelationId);

        var fingerprint = Fingerprint(normalized, actor);
        var prior = await store.GetFinalDecisionIdempotencyAsync(assessment.AssessmentId, command.IdempotencyKey, cancellationToken);
        if (prior is not null)
        {
            if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
                return AuthorityResult.Deny(409, "P1_IDEMPOTENCY_CONFLICT", command.CorrelationId);
            return prior.Result with { IdempotentReplay = true, StateMutated = false, CorrelationId = command.CorrelationId };
        }

        if (!string.Equals(idea.State, "UNDER_REVIEW", StringComparison.OrdinalIgnoreCase))
            return AuthorityResult.Deny(409, "P1_G04_IDEA_NOT_UNDER_REVIEW", command.CorrelationId);
        if (!string.Equals(plan.State, "READY_FOR_G04_DECISION", StringComparison.OrdinalIgnoreCase))
            return AuthorityResult.Deny(409, "P1_G04_PLAN_NOT_READY", command.CorrelationId);
        if (!string.Equals(assessment.State, "PENDING", StringComparison.OrdinalIgnoreCase))
            return AuthorityResult.Deny(409, "P1_G04_ASSESSMENT_NOT_PENDING", command.CorrelationId);
        if (idea.Version != command.ExpectedIdeaRevision)
            return AuthorityResult.Deny(409, "P1_G04_IDEA_REVISION_CONFLICT", command.CorrelationId);
        if (plan.PlanVersion != command.ExpectedPlanVersion)
            return AuthorityResult.Deny(409, "P1_G04_PLAN_VERSION_CONFLICT", command.CorrelationId);
        if (assessment.DecisionVersion != command.ExpectedAssessmentDecisionVersion)
            return AuthorityResult.Deny(409, "P1_G04_ASSESSMENT_DECISION_VERSION_CONFLICT", command.CorrelationId);

        var technicalBefore = idea.StateMutationVersion ?? idea.Version;
        if (technicalBefore != command.ExpectedStateMutationVersion)
            return AuthorityResult.Deny(409, "P1_G04_STATE_MUTATION_VERSION_CONFLICT", command.CorrelationId);

        var committeeSnapshot = await store.GetCommitteeSnapshotAsync(assessment.AssessmentId, cancellationToken);
        var committeeState = await store.GetCommitteeStateAsync(assessment.AssessmentId, cancellationToken);
        var committeeCheck = ValidateCommitteeRoute(assessment, actor, normalized.Outcome, committeeSnapshot, committeeState);
        if (!committeeCheck.Passed)
            return AuthorityResult.Deny(committeeCheck.HttpStatus, committeeCheck.Code, command.CorrelationId, committeeCheck.Detail);

        var profileEvidence = await evidenceProvider.ResolveAsync(idea, plan, assessment, cancellationToken);
        if (profileEvidence is null || !ValidateEvidenceIntegrity(profileEvidence))
            return AuthorityResult.Deny(503, "P1_G04_DECISION_EVIDENCE_NOT_BOUND", command.CorrelationId);

        string sodResult = "PASS";
        string? sodException = null;
        if (normalized.Outcome == "APPROVE")
        {
            var conflict = profileEvidence.EvaluatorConflictPolicy?.Trim().ToUpperInvariant() ?? string.Empty;
            if (conflict == "DENY")
                return AuthorityResult.Deny(403, "P1_G04_EVALUATOR_FINAL_DECIDER_CONFLICT", command.CorrelationId);
            if (conflict == "ALLOW_WITH_AUDIT")
            {
                sodResult = "ALLOW_WITH_AUDIT";
                sodException = "FROZEN_GOVERNANCE_ALLOW_WITH_AUDIT";
            }
            else if (conflict != "PASS")
            {
                return AuthorityResult.Deny(503, "P1_G04_SOD_POLICY_UNKNOWN", command.CorrelationId);
            }

            var gateFailure = ApprovalGateFailure(profileEvidence);
            if (gateFailure is not null)
                return AuthorityResult.Deny(422, gateFailure, command.CorrelationId);
        }

        var eventName = policy.ResolveEventName(normalized.Outcome);
        if (string.IsNullOrWhiteSpace(eventName))
            return AuthorityResult.Deny(500, "P1_G04_FINAL_EVENT_NOT_BOUND", command.CorrelationId);

        var targetState = normalized.Outcome switch
        {
            "APPROVE" => "APPROVED",
            "RETURN" => "RETURNED",
            "HOLD" => "HOLD",
            "REJECT" => "REJECTED",
            _ => throw new InvalidOperationException("Normalized outcome not supported.")
        };

        var businessRevisionAfter = normalized.Outcome == "RETURN" ? idea.Version + 1 : idea.Version;
        var technicalAfter = technicalBefore + 1;
        var ideaAfter = idea with
        {
            State = targetState,
            Version = businessRevisionAfter,
            StateMutationVersion = technicalAfter,
            WorkRoutingRole = null
        };
        var planAfter = normalized.Outcome == "RETURN"
            ? plan with { State = "SUPERSEDED", PlanVersion = plan.PlanVersion + 1 }
            : plan;
        var assessmentAfter = assessment with
        {
            State = "DECIDED",
            DecisionVersion = assessment.DecisionVersion + 1
        };

        var timestamp = DateTimeOffset.UtcNow;
        var decisionId = $"G04DEC-{Guid.NewGuid():N}";
        var evidence = new G04FinalDecisionEvidenceEnvelope(
            decisionId, assessment.AssessmentId, plan.PlanId, idea.AggregateId,
            idea.Version, businessRevisionAfter, technicalBefore, technicalAfter,
            assessment.DecisionVersion, assessmentAfter.DecisionVersion,
            normalized.Outcome, normalized.ReasonCode, normalized.DecisionComment, normalized.ReviewDate,
            actor.PersonId, actor.AssignmentId, actor.NetworkIdentity, actor.IdentitySource,
            assessment.DecisionRoute!, assessment.DecisionRouteKind!, assessment.DecisionMethod!,
            assessment.GovernanceProfileId!, assessment.GovernanceProfileVersion!,
            profileEvidence.ProfileId, profileEvidence.ProfileVersion, profileEvidence.ProfileSnapshotJson,
            profileEvidence.ProfileSnapshotSha256, profileEvidence.RuleSet, profileEvidence.HardConditionMappingVersion,
            profileEvidence.GateScore, profileEvidence.DataReadiness, profileEvidence.RuleCheckResults,
            profileEvidence.SpecialistOutcomeSummary, committeeSnapshot?.SnapshotId,
            committeeState?.CommitteeVersion, committeeState?.VotingStageState,
            committeeState?.ApprovalRuleSatisfied, sodResult, sodException, timestamp, command.CorrelationId);

        var audit = new AuditEnvelope(
            $"AUD-{Guid.NewGuid():N}", actor.PersonId, actor.NetworkIdentity, actor.IdentitySource,
            actor.Roles, actor.AssignmentId, idea.AggregateId, businessRevisionAfter, policy.RuleSet,
            timestamp, command.CorrelationId, "g04.final-decision");

        var payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["decisionEvidenceId"] = decisionId,
            ["ideaId"] = idea.AggregateId,
            ["ideaRevision"] = businessRevisionAfter.ToString(CultureInfo.InvariantCulture),
            ["stateMutationVersion"] = technicalAfter.ToString(CultureInfo.InvariantCulture),
            ["ideaState"] = targetState,
            ["evaluationPlanId"] = plan.PlanId,
            ["g04AssessmentId"] = assessment.AssessmentId,
            ["assessmentDecisionVersion"] = assessmentAfter.DecisionVersion.ToString(CultureInfo.InvariantCulture),
            ["outcome"] = normalized.Outcome,
            ["decisionRoute"] = assessment.DecisionRoute!,
            ["governanceProfileId"] = assessment.GovernanceProfileId!,
            ["governanceProfileVersion"] = assessment.GovernanceProfileVersion!,
            ["decisionProfileId"] = profileEvidence.ProfileId,
            ["decisionProfileVersion"] = profileEvidence.ProfileVersion,
            ["profileSnapshotSha256"] = profileEvidence.ProfileSnapshotSha256
        };
        var outbox = new OutboxEnvelope(
            $"MSG-{Guid.NewGuid():N}", eventName, idea.AggregateId, businessRevisionAfter,
            command.CorrelationId, timestamp, payload);

        return await store.CommitFinalDecisionAsync(
            new G04FinalDecisionRequest(command, actor, idea, plan, assessment, fingerprint),
            new G04FinalDecisionCommit(ideaAfter, planAfter, assessmentAfter, evidence, audit, outbox),
            cancellationToken);
    }

    private static G04FinalDecisionCommand NormalizeInput(G04FinalDecisionCommand command) => command with
    {
        Outcome = command.Outcome?.Trim().ToUpperInvariant() ?? string.Empty,
        ReasonCode = command.ReasonCode?.Trim().ToUpperInvariant() ?? string.Empty,
        DecisionComment = command.DecisionComment?.Trim() ?? string.Empty
    };

    private static bool ContextMatches(
        AggregateSnapshot idea,
        EvaluationPlanEnvelope plan,
        G04AssessmentEnvelope assessment) =>
        string.Equals(plan.PlanId, assessment.PlanId, StringComparison.Ordinal)
        && string.Equals(plan.IdeaId, idea.AggregateId, StringComparison.Ordinal)
        && string.Equals(assessment.IdeaId, idea.AggregateId, StringComparison.Ordinal)
        && plan.IdeaVersion == idea.Version
        && assessment.IdeaVersion == idea.Version
        && assessment.PlanVersion == plan.PlanVersion;

    private static CommitteeValidation ValidateCommitteeRoute(
        G04AssessmentEnvelope assessment,
        AuthorityActor actor,
        string outcome,
        G04CommitteeSnapshotEnvelope? snapshot,
        G04CommitteeStateEnvelope? state)
    {
        if (G04RouteIntegrity.IsCommitteeRoute(assessment))
        {
            if (snapshot is null || state is null)
                return CommitteeValidation.Fail(409, "P1_G04_COMMITTEE_FINALIZATION_REQUIRED");
            if (!string.Equals(snapshot.AssessmentId, assessment.AssessmentId, StringComparison.Ordinal)
                || !string.Equals(state.AssessmentId, assessment.AssessmentId, StringComparison.Ordinal)
                || !string.Equals(state.SnapshotId, snapshot.SnapshotId, StringComparison.Ordinal)
                || !string.Equals(snapshot.DecisionRoute, assessment.DecisionRoute, StringComparison.Ordinal)
                || !string.Equals(snapshot.GovernanceProfileId, assessment.GovernanceProfileId, StringComparison.Ordinal)
                || !string.Equals(snapshot.GovernanceProfileVersion, assessment.GovernanceProfileVersion, StringComparison.Ordinal)
                || !string.Equals(snapshot.VoteRule, assessment.DecisionMethod, StringComparison.OrdinalIgnoreCase))
                return CommitteeValidation.Fail(409, "P1_G04_COMMITTEE_CONTEXT_MISMATCH");
            if (!string.Equals(state.VotingStageState, "COMPLETED", StringComparison.OrdinalIgnoreCase))
                return CommitteeValidation.Fail(409, "P1_G04_COMMITTEE_STAGE_NOT_COMPLETED");
            if (snapshot.Members.Any(x => string.Equals(x.PersonId, actor.PersonId, StringComparison.OrdinalIgnoreCase)))
                return CommitteeValidation.Fail(403, "SOD_G04_MEMBER_NOT_FINAL_AUTHORITY");
            if (outcome == "APPROVE" && !state.ApprovalRuleSatisfied)
                return CommitteeValidation.Fail(422, "P1_G04_COMMITTEE_APPROVAL_NOT_SATISFIED");
            return CommitteeValidation.Pass();
        }

        if (!string.Equals(assessment.DecisionRouteKind, "INDIVIDUAL", StringComparison.Ordinal)
            || !string.Equals(assessment.DecisionMethod, "INDIVIDUAL_GOVERNANCE_DECISION", StringComparison.Ordinal))
            return CommitteeValidation.Fail(409, "P1_G04_FINAL_ROUTE_INVALID");
        if (snapshot is not null || state is not null)
            return CommitteeValidation.Fail(409, "P1_G04_INDIVIDUAL_ROUTE_HAS_COMMITTEE_CONTEXT");
        return CommitteeValidation.Pass();
    }

    private static bool ValidateEvidenceIntegrity(G04DecisionProfileEvidence evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence.ProfileId)
            || string.IsNullOrWhiteSpace(evidence.ProfileVersion)
            || string.IsNullOrWhiteSpace(evidence.ProfileSnapshotJson)
            || string.IsNullOrWhiteSpace(evidence.ProfileSnapshotSha256)
            || string.IsNullOrWhiteSpace(evidence.RuleSet)
            || string.IsNullOrWhiteSpace(evidence.HardConditionMappingVersion)
            || string.IsNullOrWhiteSpace(evidence.RuleCheckResults)
            || string.IsNullOrWhiteSpace(evidence.SpecialistOutcomeSummary)
            || evidence.GateScore is < 0 or > 100
            || evidence.PassThreshold is < 0 or > 100
            || evidence.DataReadiness is < 0 or > 100
            || evidence.DataThreshold is < 0 or > 100)
            return false;

        var actualHash = Sha256(evidence.ProfileSnapshotJson);
        return string.Equals(actualHash, evidence.ProfileSnapshotSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ApprovalGateFailure(G04DecisionProfileEvidence evidence)
    {
        if (evidence.GateScore < evidence.PassThreshold) return "P1_G04_GATE_SCORE_BELOW_THRESHOLD";
        if (evidence.DataReadiness < evidence.DataThreshold) return "P1_G04_DATA_READINESS_BELOW_THRESHOLD";
        if (!evidence.BaseNeedPresent) return "P1_G04_BASE_NEED_REQUIRED";
        if (!evidence.TechnicalRequirementSatisfied) return "P1_G04_TECHNICAL_REQUIREMENT_NOT_SATISFIED";
        if (!evidence.HardConditionsAllMappedAndPass) return "P1_G04_HARD_CONDITIONS_NOT_SATISFIED";
        if (!evidence.SpecialistOutcomesNonBlocking) return "P1_G04_SPECIALIST_OUTCOME_BLOCKS_APPROVAL";
        return null;
    }

    public static string Fingerprint(G04FinalDecisionCommand command, AuthorityActor actor)
    {
        var review = command.ReviewDate?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;
        var material = string.Join("\n", new[]
        {
            "g04.final-decision", command.IdeaId, command.ExpectedIdeaRevision.ToString(CultureInfo.InvariantCulture),
            command.ExpectedStateMutationVersion.ToString(CultureInfo.InvariantCulture), command.PlanId,
            command.ExpectedPlanVersion.ToString(CultureInfo.InvariantCulture), command.AssessmentId,
            command.ExpectedAssessmentDecisionVersion.ToString(CultureInfo.InvariantCulture), command.Outcome,
            command.ReasonCode, command.DecisionComment, review, actor.PersonId, actor.AssignmentId
        });
        return Sha256(material);
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record CommitteeValidation(bool Passed, int HttpStatus, string Code, string? Detail = null)
    {
        public static CommitteeValidation Pass() => new(true, 200, "OK");
        public static CommitteeValidation Fail(int status, string code, string? detail = null) => new(false, status, code, detail);
    }
}
