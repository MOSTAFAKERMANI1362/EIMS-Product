using System.Security.Cryptography;
using System.Text;

namespace EIMS.Authority.Recovery;

/// <summary>
/// Wave 11 server-authoritative Benefit runtime derived from ACR-P0-008.
/// Exact Benefit value schema was not recovered, therefore baseline/target and dossiers are bound by immutable evidence references.
/// </summary>
public sealed class BenefitServiceWave11(
    IBenefitWave11Store store,
    IBenefitOwnershipEvidenceProviderWave11 ownershipProvider,
    IBenefitBaselineTargetEvidenceProviderWave11 baselineTargetProvider,
    IBenefitExecutionOwnerEvidenceProviderWave11 executionOwnerProvider,
    IBenefitMeasurementAuthorityProviderWave11 measurementAuthorityProvider,
    IBenefitKnowledgePublicationEvidenceProviderWave11 knowledgePublicationProvider,
    ICommandPolicyCatalog? catalog = null,
    TimeProvider? clock = null)
{
    private readonly ICommandPolicyCatalog _catalog = catalog ?? new RecoveredApiCommandCatalogWave11();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async ValueTask<AuthorityResult> ExecuteAsync(
        BenefitCommandWave11 command,
        AuthorityActor? actor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(command);

        if (string.IsNullOrWhiteSpace(normalized.CommandName)
            || string.IsNullOrWhiteSpace(normalized.BenefitId)
            || string.IsNullOrWhiteSpace(normalized.IdempotencyKey)
            || string.IsNullOrWhiteSpace(normalized.CorrelationId)
            || normalized.ExpectedVersion <= 0)
            return AuthorityResult.Deny(400, "P1_BENEFIT_COMMAND_INVALID", normalized.CorrelationId ?? string.Empty);

        if (!_catalog.TryGet(normalized.CommandName, out var policy)
            || !policy.MutationContractRecovered
            || !policy.RuleContractRecovered
            || !policy.EventContractRecovered)
            return AuthorityResult.Deny(503, "P1_BENEFIT_COMMAND_NOT_BOUND", normalized.CorrelationId);

        if (!ValidateActor(actor, normalized, policy, out var actorError))
            return AuthorityResult.Deny(actorError.Status, actorError.Code, normalized.CorrelationId);
        var resolvedActor = actor!;

        var fingerprint = Fingerprint(normalized, resolvedActor);
        var prior = await store.GetBenefitIdempotencyAsync(
            normalized.CommandName,
            normalized.BenefitId,
            normalized.IdempotencyKey,
            cancellationToken);
        if (prior is not null)
        {
            if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
                return AuthorityResult.Deny(409, "P1_IDEMPOTENCY_CONFLICT", normalized.CorrelationId);
            return prior.Result with
            {
                IdempotentReplay = true,
                StateMutated = false,
                CorrelationId = normalized.CorrelationId
            };
        }

        var before = await store.GetBenefitAsync(normalized.BenefitId, cancellationToken);
        if (before is null)
            return AuthorityResult.Deny(404, "P1_BENEFIT_NOT_FOUND", normalized.CorrelationId);
        if (before.Version != normalized.ExpectedVersion)
            return AuthorityResult.Deny(409, "P1_BENEFIT_VERSION_CONFLICT", normalized.CorrelationId);

        var ownership = await ownershipProvider.ResolveAsync(normalized.BenefitId, cancellationToken);
        if (!ValidOwnership(ownership, normalized.BenefitId))
            return AuthorityResult.Deny(409, "P1_BENEFIT_OWNERSHIP_EVIDENCE_REQUIRED", normalized.CorrelationId);

        if (!ValidateAuthorityContext(resolvedActor, normalized, ownership!, out actorError))
            return AuthorityResult.Deny(actorError.Status, actorError.Code, normalized.CorrelationId);

        BenefitBaselineTargetEvidenceWave11? baselineTarget = null;
        BenefitExecutionOwnerEvidenceWave11? executionOwner = null;
        BenefitKnowledgePublicationEvidenceWave11? knowledge = null;

        var role = resolvedActor.Roles.Single().Trim();
        var isOwnerRole = string.Equals(role, "BENEFIT_OWNER", StringComparison.OrdinalIgnoreCase);
        var isVerifierRole = string.Equals(role, "BENEFIT_VERIFIER", StringComparison.OrdinalIgnoreCase);
        var isDataProviderRole = string.Equals(role, "AUTHORIZED_DATA_PROVIDER", StringComparison.OrdinalIgnoreCase);

        if (isOwnerRole
            && (!string.Equals(ownership!.OwnerPersonId, resolvedActor.PersonId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ownership.OwnerAssignmentId, resolvedActor.AssignmentId, StringComparison.OrdinalIgnoreCase)))
            return AuthorityResult.Deny(403, "P1_BENEFIT_OWNER_CONTEXT_MISMATCH", normalized.CorrelationId);

        if (isDataProviderRole)
        {
            if (!string.Equals(normalized.CommandName, "benefits.measure", StringComparison.Ordinal))
                return AuthorityResult.Deny(403, "P1_BENEFIT_DATA_PROVIDER_COMMAND_DENIED", normalized.CorrelationId);

            var measurementAuthority = await measurementAuthorityProvider.ResolveAsync(
                normalized.BenefitId,
                resolvedActor,
                cancellationToken);
            if (measurementAuthority is null
                || !measurementAuthority.Authorized
                || !string.Equals(measurementAuthority.BenefitId, normalized.BenefitId, StringComparison.Ordinal)
                || !string.Equals(measurementAuthority.PersonId, resolvedActor.PersonId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(measurementAuthority.AssignmentId, resolvedActor.AssignmentId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(measurementAuthority.Scope, ownership!.Scope, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(measurementAuthority.EvidenceRef)
                || string.IsNullOrWhiteSpace(measurementAuthority.EvidenceVersion))
                return AuthorityResult.Deny(403, "P1_BENEFIT_DATA_PROVIDER_POLICY_REQUIRED", normalized.CorrelationId);
        }

        if (string.Equals(normalized.CommandName, "benefits.set-baseline", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(normalized.BaselineEvidenceRef)
                || string.IsNullOrWhiteSpace(normalized.TargetEvidenceRef))
                return AuthorityResult.Deny(400, "P1_BENEFIT_BASELINE_TARGET_REQUIRED", normalized.CorrelationId);
            if (string.Equals(normalized.BaselineEvidenceRef, normalized.TargetEvidenceRef, StringComparison.Ordinal))
                return AuthorityResult.Deny(400, "P1_BENEFIT_BASELINE_TARGET_NOT_DISTINCT", normalized.CorrelationId);

            baselineTarget = await baselineTargetProvider.ValidateAsync(
                normalized.BenefitId,
                normalized.BaselineEvidenceRef,
                normalized.TargetEvidenceRef,
                cancellationToken);
            if (baselineTarget is null
                || !baselineTarget.BaselineValid
                || !baselineTarget.TargetValid
                || !baselineTarget.Distinct
                || string.IsNullOrWhiteSpace(baselineTarget.BenefitClass)
                || string.IsNullOrWhiteSpace(baselineTarget.EvidenceRef)
                || string.IsNullOrWhiteSpace(baselineTarget.EvidenceVersion))
                return AuthorityResult.Deny(409, "P1_BENEFIT_BASELINE_TARGET_EVIDENCE_INVALID", normalized.CorrelationId);
        }

        if (isVerifierRole)
        {
            executionOwner = await executionOwnerProvider.ResolveAsync(normalized.BenefitId, cancellationToken);
            if (executionOwner is null
                || !string.Equals(executionOwner.BenefitId, normalized.BenefitId, StringComparison.Ordinal)
                || !string.Equals(executionOwner.ExecutionId, before.ExecutionId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(executionOwner.ExecutionOwnerPersonId)
                || string.IsNullOrWhiteSpace(executionOwner.EvidenceRef)
                || string.IsNullOrWhiteSpace(executionOwner.EvidenceVersion))
                return AuthorityResult.Deny(409, "P1_BENEFIT_EXECUTION_OWNER_EVIDENCE_REQUIRED", normalized.CorrelationId);

            if (string.Equals(executionOwner.ExecutionOwnerPersonId, resolvedActor.PersonId, StringComparison.OrdinalIgnoreCase))
                return AuthorityResult.Deny(403, "SOD_BENEFIT_VERIFIER_EXECUTION_OWNER", normalized.CorrelationId,
                    "Benefit verifier must be independent from Execution Owner for the same output.");
        }

        if (string.Equals(normalized.CommandName, "benefits.close", StringComparison.Ordinal))
        {
            knowledge = await knowledgePublicationProvider.ResolveAsync(normalized.BenefitId, cancellationToken);
            if (knowledge is null
                || !knowledge.Exists
                || !string.Equals(knowledge.BenefitId, normalized.BenefitId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(knowledge.KnowledgeId)
                || !string.Equals(knowledge.Status, "PUBLISHED", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(knowledge.PublicationDossierRef)
                || string.IsNullOrWhiteSpace(knowledge.EvidenceRef)
                || string.IsNullOrWhiteSpace(knowledge.EvidenceVersion))
                return AuthorityResult.Deny(409, "P1_BENEFIT_PUBLISHED_KNOWLEDGE_REQUIRED", normalized.CorrelationId);
        }

        var plan = Plan(normalized, resolvedActor, ownership!, baselineTarget, knowledge, before, policy);
        if (!plan.Allowed || plan.Commit is null)
            return AuthorityResult.Deny(plan.Status, plan.Code, normalized.CorrelationId, plan.Detail);

        return await store.CommitBenefitCommandAsync(
            new BenefitCommandRequestWave11(normalized, resolvedActor, before, policy, ownership!, fingerprint),
            plan.Commit,
            cancellationToken);
    }

    private Planned Plan(
        BenefitCommandWave11 command,
        AuthorityActor actor,
        BenefitOwnershipEvidenceWave11 ownership,
        BenefitBaselineTargetEvidenceWave11? baselineTarget,
        BenefitKnowledgePublicationEvidenceWave11? knowledge,
        BenefitEnvelopeWave11 before,
        CommandPolicy policy)
    {
        var now = _clock.GetUtcNow();
        var nextVersion = before.Version + 1;
        BenefitEnvelopeWave11 after;
        IReadOnlyCollection<OutboxEnvelope> events;

        switch (command.CommandName)
        {
            case "benefits.accept":
                if (!State(before, "OBLIGATION_PENDING_ACCEPTANCE"))
                    return Planned.Deny(409, "P1_BENEFIT_ACCEPT_STATE_INVALID");
                after = before with
                {
                    State = "BASELINE_REQUIRED",
                    Version = nextVersion,
                    AcceptedByPersonId = actor.PersonId,
                    AcceptedByAssignmentId = actor.AssignmentId,
                    UpdatedAt = now,
                    CorrelationId = command.CorrelationId
                };
                events = One("BenefitObligationAccepted.v1", after, command, now,
                    new Dictionary<string,string> { ["state"] = after.State });
                break;

            case "benefits.set-baseline":
                if (!State(before, "BASELINE_REQUIRED"))
                    return Planned.Deny(409, "P1_BENEFIT_BASELINE_STATE_INVALID");
                if (baselineTarget is null)
                    return Planned.Deny(409, "P1_BENEFIT_BASELINE_TARGET_EVIDENCE_INVALID");
                after = before with
                {
                    State = "PLAN_REQUIRED",
                    Version = nextVersion,
                    BaselineEvidenceRef = command.BaselineEvidenceRef,
                    TargetEvidenceRef = command.TargetEvidenceRef,
                    UpdatedAt = now,
                    CorrelationId = command.CorrelationId
                };
                events = One("BenefitBaselineDefined.v1", after, command, now,
                    new Dictionary<string,string>
                    {
                        ["state"] = after.State,
                        ["baselineEvidenceRef"] = after.BaselineEvidenceRef!,
                        ["targetEvidenceRef"] = after.TargetEvidenceRef!,
                        ["benefitClass"] = baselineTarget.BenefitClass
                    });
                break;

            case "benefits.approve-measurement-plan":
                if (!State(before, "PLAN_REQUIRED")
                    || string.IsNullOrWhiteSpace(before.BaselineEvidenceRef)
                    || string.IsNullOrWhiteSpace(before.TargetEvidenceRef))
                    return Planned.Deny(409, "P1_BENEFIT_PLAN_STATE_INVALID");
                if (string.IsNullOrWhiteSpace(command.MeasurementPlanRef))
                    return Planned.Deny(400, "P1_BENEFIT_MEASUREMENT_PLAN_REQUIRED");
                after = before with
                {
                    State = "MEASUREMENT_PENDING",
                    Version = nextVersion,
                    MeasurementPlanRef = command.MeasurementPlanRef,
                    UpdatedAt = now,
                    CorrelationId = command.CorrelationId
                };
                events = One("BenefitMeasurementPlanApproved.v1", after, command, now,
                    new Dictionary<string,string>
                    {
                        ["state"] = after.State,
                        ["measurementPlanRef"] = after.MeasurementPlanRef!
                    });
                break;

            case "benefits.measure":
                if (!State(before, "MEASUREMENT_PENDING") || string.IsNullOrWhiteSpace(before.MeasurementPlanRef))
                    return Planned.Deny(409, "P1_BENEFIT_MEASUREMENT_STATE_INVALID");
                if (string.IsNullOrWhiteSpace(command.MeasurementDossierRef))
                    return Planned.Deny(400, "P1_BENEFIT_MEASUREMENT_DOSSIER_REQUIRED");
                after = before with
                {
                    State = "MEASURED",
                    Version = nextVersion,
                    MeasurementDossierRef = command.MeasurementDossierRef,
                    MeasuredByPersonId = actor.PersonId,
                    UpdatedAt = now,
                    CorrelationId = command.CorrelationId
                };
                events = One("BenefitMeasured.v1", after, command, now,
                    new Dictionary<string,string>
                    {
                        ["state"] = after.State,
                        ["measurementDossierRef"] = after.MeasurementDossierRef!
                    });
                break;

            case "benefits.verify":
                if (!State(before, "MEASURED") || string.IsNullOrWhiteSpace(before.MeasurementDossierRef))
                    return Planned.Deny(409, "P1_BENEFIT_VERIFY_STATE_INVALID");
                if (string.IsNullOrWhiteSpace(command.VerificationDossierRef))
                    return Planned.Deny(400, "P1_BENEFIT_VERIFICATION_DOSSIER_REQUIRED");
                after = before with
                {
                    State = "VERIFIED",
                    Version = nextVersion,
                    VerificationDossierRef = command.VerificationDossierRef,
                    VerifiedByPersonId = actor.PersonId,
                    UpdatedAt = now,
                    CorrelationId = command.CorrelationId
                };
                events = One("BenefitVerified.v1", after, command, now,
                    new Dictionary<string,string>
                    {
                        ["state"] = after.State,
                        ["verificationDossierRef"] = after.VerificationDossierRef!
                    });
                break;

            case "benefits.attribution":
                if (!State(before, "VERIFIED") || string.IsNullOrWhiteSpace(before.VerificationDossierRef))
                    return Planned.Deny(409, "P1_BENEFIT_ATTRIBUTION_STATE_INVALID");
                if (string.IsNullOrWhiteSpace(command.AttributionDossierRef))
                    return Planned.Deny(400, "P1_BENEFIT_ATTRIBUTION_DOSSIER_REQUIRED");
                after = before with
                {
                    State = "VALIDATED",
                    Version = nextVersion,
                    AttributionDossierRef = command.AttributionDossierRef,
                    UpdatedAt = now,
                    CorrelationId = command.CorrelationId
                };
                events = One("BenefitAttributionValidated.v1", after, command, now,
                    new Dictionary<string,string>
                    {
                        ["state"] = after.State,
                        ["attributionDossierRef"] = after.AttributionDossierRef!
                    });
                break;

            case "benefits.realize":
                if (!State(before, "VALIDATED") || string.IsNullOrWhiteSpace(before.AttributionDossierRef))
                    return Planned.Deny(409, "P1_BENEFIT_REALIZATION_STATE_INVALID");
                if (string.IsNullOrWhiteSpace(command.RealizationDossierRef))
                    return Planned.Deny(400, "P1_BENEFIT_REALIZATION_DOSSIER_REQUIRED");
                after = before with
                {
                    State = "REALIZED",
                    Version = nextVersion,
                    RealizationDossierRef = command.RealizationDossierRef,
                    UpdatedAt = now,
                    CorrelationId = command.CorrelationId
                };
                events = One("BenefitRealized.v1", after, command, now,
                    new Dictionary<string,string>
                    {
                        ["state"] = after.State,
                        ["realizationDossierRef"] = after.RealizationDossierRef!
                    });
                break;

            case "benefits.close":
                if (!State(before, "REALIZED") || string.IsNullOrWhiteSpace(before.RealizationDossierRef))
                    return Planned.Deny(409, "P1_BENEFIT_CLOSE_STATE_INVALID");
                if (knowledge is null)
                    return Planned.Deny(409, "P1_BENEFIT_PUBLISHED_KNOWLEDGE_REQUIRED");
                after = before with
                {
                    State = "CLOSED",
                    Version = nextVersion,
                    KnowledgeId = knowledge.KnowledgeId,
                    UpdatedAt = now,
                    CorrelationId = command.CorrelationId
                };
                events = One("BenefitClosed.v1", after, command, now,
                    new Dictionary<string,string>
                    {
                        ["state"] = after.State,
                        ["knowledgeId"] = knowledge.KnowledgeId,
                        ["knowledgePublicationEvidenceRef"] = knowledge.EvidenceRef
                    });
                break;

            default:
                return Planned.Deny(503, "P1_BENEFIT_COMMAND_NOT_IMPLEMENTED");
        }

        return Planned.Allow(new BenefitCommitWave11(
            after,
            UserAudit(actor, ownership, after, command, policy, now),
            events));
    }

    private static bool ValidateActor(
        AuthorityActor? actor,
        BenefitCommandWave11 command,
        CommandPolicy policy,
        out (int Status, string Code) error)
    {
        if (actor is null
            || string.IsNullOrWhiteSpace(actor.PersonId)
            || string.IsNullOrWhiteSpace(actor.NetworkIdentity)
            || string.IsNullOrWhiteSpace(actor.IdentitySource)
            || string.IsNullOrWhiteSpace(actor.AssignmentId))
        {
            error = (401, "P1_IDENTITY_ASSIGNMENT_REQUIRED");
            return false;
        }

        var roles = actor.Roles.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roles.Length != 1
            || !policy.RequiredRoles.Any(required => string.Equals(required, roles[0], StringComparison.OrdinalIgnoreCase)))
        {
            error = (403, "P1_BENEFIT_COMMAND_ROLE_REQUIRED");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(command.RequestedScope)
            && !actor.Scopes.Any(x => string.Equals(x, command.RequestedScope, StringComparison.OrdinalIgnoreCase)))
        {
            error = (403, "P1_SCOPE_DENIED");
            return false;
        }

        error = default;
        return true;
    }

    private static bool ValidateAuthorityContext(
        AuthorityActor actor,
        BenefitCommandWave11 command,
        BenefitOwnershipEvidenceWave11 ownership,
        out (int Status, string Code) error)
    {
        if (!actor.Scopes.Any(x => string.Equals(x, ownership.Scope, StringComparison.OrdinalIgnoreCase)))
        {
            error = (403, "P1_BENEFIT_OWNERSHIP_SCOPE_DENIED");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(command.RequestedScope)
            && !string.Equals(command.RequestedScope, ownership.Scope, StringComparison.OrdinalIgnoreCase))
        {
            error = (403, "P1_BENEFIT_SCOPE_CONTEXT_MISMATCH");
            return false;
        }

        error = default;
        return true;
    }

    private static bool ValidOwnership(BenefitOwnershipEvidenceWave11? value, string benefitId) =>
        value is not null
        && string.Equals(value.BenefitId, benefitId, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(value.OwnerPersonId)
        && !string.IsNullOrWhiteSpace(value.OwnerAssignmentId)
        && !string.IsNullOrWhiteSpace(value.Scope)
        && !string.IsNullOrWhiteSpace(value.EvidenceRef)
        && !string.IsNullOrWhiteSpace(value.EvidenceVersion);

    private static BenefitCommandWave11 Normalize(BenefitCommandWave11 command) => command with
    {
        CommandName = command.CommandName?.Trim() ?? string.Empty,
        BenefitId = command.BenefitId?.Trim() ?? string.Empty,
        IdempotencyKey = command.IdempotencyKey?.Trim() ?? string.Empty,
        CorrelationId = command.CorrelationId?.Trim() ?? string.Empty,
        RequestedScope = Clean(command.RequestedScope),
        BaselineEvidenceRef = Clean(command.BaselineEvidenceRef),
        TargetEvidenceRef = Clean(command.TargetEvidenceRef),
        MeasurementPlanRef = Clean(command.MeasurementPlanRef),
        MeasurementDossierRef = Clean(command.MeasurementDossierRef),
        VerificationDossierRef = Clean(command.VerificationDossierRef),
        AttributionDossierRef = Clean(command.AttributionDossierRef),
        RealizationDossierRef = Clean(command.RealizationDossierRef)
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool State(BenefitEnvelopeWave11 benefit, string state) =>
        string.Equals(benefit.State, state, StringComparison.Ordinal);

    private static string Fingerprint(BenefitCommandWave11 command, AuthorityActor actor)
    {
        var raw = string.Join("\u001f", new[]
        {
            command.CommandName,
            command.BenefitId,
            command.ExpectedVersion.ToString(),
            command.RequestedScope ?? string.Empty,
            command.BaselineEvidenceRef ?? string.Empty,
            command.TargetEvidenceRef ?? string.Empty,
            command.MeasurementPlanRef ?? string.Empty,
            command.MeasurementDossierRef ?? string.Empty,
            command.VerificationDossierRef ?? string.Empty,
            command.AttributionDossierRef ?? string.Empty,
            command.RealizationDossierRef ?? string.Empty,
            actor.PersonId,
            actor.AssignmentId
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static AuditEnvelope UserAudit(
        AuthorityActor actor,
        BenefitOwnershipEvidenceWave11 ownership,
        BenefitEnvelopeWave11 after,
        BenefitCommandWave11 command,
        CommandPolicy policy,
        DateTimeOffset now) =>
        new(
            "AUD-" + Guid.NewGuid().ToString("N"),
            actor.PersonId,
            actor.NetworkIdentity,
            actor.IdentitySource,
            actor.Roles,
            actor.AssignmentId,
            after.BenefitId,
            after.Version,
            policy.RuleSet + "|ownershipEvidence=" + ownership.EvidenceRef,
            now,
            command.CorrelationId,
            command.CommandName);

    private static IReadOnlyCollection<OutboxEnvelope> One(
        string eventName,
        BenefitEnvelopeWave11 after,
        BenefitCommandWave11 command,
        DateTimeOffset now,
        IReadOnlyDictionary<string,string> payload) =>
        new[] { Event(eventName, after, command.CorrelationId, now, payload) };

    private static OutboxEnvelope Event(
        string eventName,
        BenefitEnvelopeWave11 after,
        string correlationId,
        DateTimeOffset now,
        IReadOnlyDictionary<string,string> payload)
    {
        var full = new Dictionary<string,string>(payload, StringComparer.Ordinal)
        {
            ["benefitId"] = after.BenefitId,
            ["executionId"] = after.ExecutionId,
            ["ideaId"] = after.IdeaId,
            ["approvedIdeaVersion"] = after.ApprovedIdeaVersion.ToString()
        };
        return new OutboxEnvelope(
            "MSG-" + Guid.NewGuid().ToString("N"),
            eventName,
            after.BenefitId,
            after.Version,
            correlationId,
            now,
            full);
    }

    private sealed record Planned(bool Allowed, int Status, string Code, string? Detail, BenefitCommitWave11? Commit)
    {
        public static Planned Deny(int status, string code, string? detail = null) => new(false, status, code, detail, null);
        public static Planned Allow(BenefitCommitWave11 commit) => new(true, 200, "PASS", null, commit);
    }
}
