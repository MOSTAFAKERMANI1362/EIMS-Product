using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EIMS.Authority.Recovery;

public sealed class EvaluationCompletionService(
    IEvaluationWorkflowStore store,
    IEvaluatorAssessmentValidator validator,
    ICommandPolicyCatalog? catalog = null)
{
    private readonly ICommandPolicyCatalog _catalog = catalog ?? new RecoveredApiCommandCatalogWave6();

    public async ValueTask<AuthorityResult> CompleteAsync(
        EvaluationCompletionCommand command,
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
            || string.IsNullOrWhiteSpace(command.EvaluationAssignmentId)
            || string.IsNullOrWhiteSpace(command.CorrelationId))
            return AuthorityResult.Deny(400, "P1_EVALUATION_CONTEXT_REQUIRED", command.CorrelationId);
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            return AuthorityResult.Deny(400, "P1_IDEMPOTENCY_KEY_REQUIRED", command.CorrelationId);

        if (!_catalog.TryGet("evaluation-assignments.complete", out var policy)
            || !policy.StateContractRecovered
            || !policy.RuleContractRecovered
            || !policy.EventContractRecovered
            || !policy.MutationContractRecovered)
            return AuthorityResult.Deny(503, "P1_EVALUATION_COMPLETION_CONTRACT_NOT_BOUND", command.CorrelationId);

        var assignment = await store.GetEvaluationAssignmentAsync(command.EvaluationAssignmentId, cancellationToken);
        var plan = await store.GetEvaluationPlanAsync(command.PlanId, cancellationToken);
        var idea = await store.GetAggregateAsync(command.IdeaId, cancellationToken);
        if (assignment is null) return AuthorityResult.Deny(404, "P1_EVALUATION_ASSIGNMENT_NOT_FOUND", command.CorrelationId);
        if (plan is null) return AuthorityResult.Deny(404, "P1_EVALUATION_PLAN_NOT_FOUND", command.CorrelationId);
        if (idea is null || !string.Equals(idea.AggregateType, "Idea", StringComparison.OrdinalIgnoreCase))
            return AuthorityResult.Deny(404, "P1_IDEA_NOT_FOUND", command.CorrelationId);

        if (!LinksMatch(command, idea, plan, assignment))
            return AuthorityResult.Deny(409, "P1_EVALUATION_CONTEXT_MISMATCH", command.CorrelationId);
        if (string.IsNullOrWhiteSpace(idea.Scope)
            || !string.Equals(idea.Scope.Trim(), assignment.Scope.Trim(), StringComparison.OrdinalIgnoreCase))
            return AuthorityResult.Deny(409, "P1_EVALUATION_SCOPE_CONTEXT_MISMATCH", command.CorrelationId);

        var actorRoles = actor.Roles.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (actorRoles.Length != 1 || !string.Equals(actorRoles[0], assignment.Role, StringComparison.OrdinalIgnoreCase))
            return AuthorityResult.Deny(403, "P1_EVALUATION_AUTHORITY_ROLE_MISMATCH", command.CorrelationId);
        if (!actor.Scopes.Contains(assignment.Scope, StringComparer.OrdinalIgnoreCase))
            return AuthorityResult.Deny(403, "P1_EVALUATION_AUTHORITY_SCOPE_MISMATCH", command.CorrelationId);
        if (!string.IsNullOrWhiteSpace(command.RequestedScope)
            && (!actor.Scopes.Contains(command.RequestedScope.Trim(), StringComparer.OrdinalIgnoreCase)
                || !string.Equals(command.RequestedScope.Trim(), assignment.Scope, StringComparison.OrdinalIgnoreCase)))
            return AuthorityResult.Deny(403, "P1_EVALUATION_AUTHORITY_SCOPE_MISMATCH", command.CorrelationId);

        var validation = validator.Validate(assignment.Role, command.RawAssessmentJson);
        if (!validation.Passed
            || string.IsNullOrWhiteSpace(validation.SchemaId)
            || string.IsNullOrWhiteSpace(validation.SchemaVersion)
            || string.IsNullOrWhiteSpace(validation.Outcome)
            || string.IsNullOrWhiteSpace(validation.NormalizedAssessmentJson))
            return AuthorityResult.Deny(422, validation.Code, command.CorrelationId, validation.Detail);

        var fingerprint = Fingerprint(command, actor, validation);
        var prior = await store.GetIdempotencyAsync("evaluation-assignments.complete", command.IdeaId, command.IdempotencyKey, cancellationToken);
        if (prior is not null)
        {
            if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
                return AuthorityResult.Deny(409, "P1_IDEMPOTENCY_CONFLICT", command.CorrelationId);
            return prior.Result with { IdempotentReplay = true, StateMutated = false, CorrelationId = command.CorrelationId };
        }

        if (!string.Equals(assignment.State, "PENDING", StringComparison.OrdinalIgnoreCase))
            return AuthorityResult.Deny(409, "P1_EVALUATION_ASSIGNMENT_NOT_PENDING", command.CorrelationId);
        if (!string.Equals(plan.State, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            return AuthorityResult.Deny(409, "P1_EVALUATION_PLAN_NOT_ACTIVE", command.CorrelationId);
        if (idea.Version != command.ExpectedIdeaVersion)
            return AuthorityResult.Deny(409, "P1_VERSION_CONFLICT", command.CorrelationId);
        if (plan.PlanVersion != command.ExpectedPlanVersion)
            return AuthorityResult.Deny(409, "P1_EVALUATION_PLAN_VERSION_CONFLICT", command.CorrelationId);
        if (assignment.AssignmentVersion != command.ExpectedAssignmentVersion)
            return AuthorityResult.Deny(409, "P1_EVALUATION_ASSIGNMENT_VERSION_CONFLICT", command.CorrelationId);

        var planAssignments = await store.GetEvaluationAssignmentsForPlanAsync(plan.PlanId, cancellationToken);
        if (planAssignments.Count == 0
            || planAssignments.Count(x => string.Equals(x.AssignmentId, assignment.AssignmentId, StringComparison.Ordinal)) != 1
            || planAssignments.Any(x => !string.Equals(x.PlanId, plan.PlanId, StringComparison.Ordinal)
                || !string.Equals(x.IdeaId, idea.AggregateId, StringComparison.Ordinal)
                || x.IdeaVersion != idea.Version))
            return AuthorityResult.Deny(500, "P1_EVALUATION_PLAN_ASSIGNMENT_SET_INVALID", command.CorrelationId);

        var now = DateTimeOffset.UtcNow;
        var assignmentAfter = assignment with
        {
            State = "COMPLETED",
            AssignmentVersion = assignment.AssignmentVersion + 1,
            CompletedAt = now,
            CompletedByPersonId = actor.PersonId,
            AuthorityAssignmentId = actor.AssignmentId,
            AssessmentSchemaId = validation.SchemaId,
            AssessmentSchemaVersion = validation.SchemaVersion,
            AssessmentOutcome = validation.Outcome
        };

        // Only completion of an activated REQUIRED mission may make the Plan ready.
        var finalRequiredCompletion = assignment.Required
            && planAssignments
                .Where(x => x.Required && !string.Equals(x.AssignmentId, assignment.AssignmentId, StringComparison.Ordinal))
                .All(x => string.Equals(x.State, "COMPLETED", StringComparison.OrdinalIgnoreCase));

        var planAfter = plan with
        {
            PlanVersion = plan.PlanVersion + 1,
            State = finalRequiredCompletion ? "READY_FOR_G04_DECISION" : "ACTIVE",
            ReadyAt = finalRequiredCompletion ? now : null
        };

        var normalized = validation.NormalizedAssessmentJson!;
        var snapshot = new AssessmentSnapshotEnvelope(
            $"ASMT-{Guid.NewGuid():N}", assignment.AssignmentId, plan.PlanId, idea.AggregateId, idea.Version,
            assignment.Role, assignment.Scope, validation.SchemaId, validation.SchemaVersion, validation.Outcome!,
            normalized, Sha256(normalized), actor.PersonId, actor.AssignmentId, now, command.CorrelationId);

        G04AssessmentEnvelope? g04Assessment = null;
        if (finalRequiredCompletion)
        {
            if (await store.GetG04AssessmentForPlanAsync(plan.PlanId, cancellationToken) is not null)
                return AuthorityResult.Deny(409, "P1_G04_ASSESSMENT_ALREADY_EXISTS", command.CorrelationId);

            var readinessMaterial = string.Join("\n", planAssignments
                .Select(x => string.Equals(x.AssignmentId, assignment.AssignmentId, StringComparison.Ordinal) ? assignmentAfter : x)
                .Where(x => x.Required)
                .OrderBy(x => x.AssignmentId, StringComparer.Ordinal)
                .Select(x => $"{x.AssignmentId}|{x.Role}|{x.State}|{x.AssignmentVersion}"));
            g04Assessment = new G04AssessmentEnvelope(
                $"G04A-{Guid.NewGuid():N}", plan.PlanId, idea.AggregateId, idea.Version, planAfter.PlanVersion,
                "PENDING", Sha256(readinessMaterial), now, command.CorrelationId);
        }

        var audit = new AuditEnvelope(
            $"AUD-{Guid.NewGuid():N}", actor.PersonId, actor.NetworkIdentity, actor.IdentitySource, actor.Roles,
            actor.AssignmentId, idea.AggregateId, idea.Version, policy.RuleSet, now, command.CorrelationId,
            "evaluation-assignments.complete");

        var events = new List<OutboxEnvelope>
        {
            new($"MSG-{Guid.NewGuid():N}", "EvaluationAssignmentCompleted.v1", idea.AggregateId, idea.Version,
                command.CorrelationId, now, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ideaId"] = idea.AggregateId,
                    ["ideaVersion"] = idea.Version.ToString(CultureInfo.InvariantCulture),
                    ["evaluationPlanId"] = plan.PlanId,
                    ["evaluationPlanVersion"] = planAfter.PlanVersion.ToString(CultureInfo.InvariantCulture),
                    ["evaluationAssignmentId"] = assignmentAfter.AssignmentId,
                    ["evaluationAssignmentVersion"] = assignmentAfter.AssignmentVersion.ToString(CultureInfo.InvariantCulture),
                    ["evaluationRole"] = assignment.Role,
                    ["assignmentState"] = "COMPLETED"
                })
        };

        if (g04Assessment is not null)
        {
            events.Add(new OutboxEnvelope(
                $"MSG-{Guid.NewGuid():N}", "G04DecisionAssessmentCreated.v1", idea.AggregateId, idea.Version,
                command.CorrelationId, now, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ideaId"] = idea.AggregateId,
                    ["ideaVersion"] = idea.Version.ToString(CultureInfo.InvariantCulture),
                    ["evaluationPlanId"] = plan.PlanId,
                    ["evaluationPlanVersion"] = planAfter.PlanVersion.ToString(CultureInfo.InvariantCulture),
                    ["g04AssessmentId"] = g04Assessment.AssessmentId,
                    ["g04AssessmentState"] = g04Assessment.State
                }));
        }

        return await store.CommitEvaluationCompletionAsync(
            new EvaluationCompletionRequest(command, actor, idea, plan, assignment, fingerprint),
            new EvaluationCompletionCommit(assignmentAfter, planAfter, snapshot, g04Assessment, audit, events.AsReadOnly()),
            cancellationToken);
    }

    public static string Fingerprint(EvaluationCompletionCommand command, AuthorityActor actor, AssessmentValidationResult validation)
    {
        var material = string.Join("\n", new[]
        {
            "evaluation-assignments.complete", command.IdeaId, command.ExpectedIdeaVersion.ToString(CultureInfo.InvariantCulture),
            command.PlanId, command.ExpectedPlanVersion.ToString(CultureInfo.InvariantCulture), command.EvaluationAssignmentId,
            command.ExpectedAssignmentVersion.ToString(CultureInfo.InvariantCulture), actor.PersonId, actor.AssignmentId,
            validation.SchemaId, validation.SchemaVersion, validation.NormalizedAssessmentJson ?? string.Empty
        });
        return Sha256(material);
    }

    private static bool LinksMatch(EvaluationCompletionCommand command, AggregateSnapshot idea, EvaluationPlanEnvelope plan, EvaluationAssignmentEnvelope assignment) =>
        string.Equals(plan.PlanId, command.PlanId, StringComparison.Ordinal)
        && string.Equals(plan.IdeaId, command.IdeaId, StringComparison.Ordinal)
        && string.Equals(assignment.AssignmentId, command.EvaluationAssignmentId, StringComparison.Ordinal)
        && string.Equals(assignment.PlanId, plan.PlanId, StringComparison.Ordinal)
        && string.Equals(assignment.IdeaId, idea.AggregateId, StringComparison.Ordinal)
        && assignment.IdeaVersion == idea.Version
        && plan.IdeaVersion == idea.Version;

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
