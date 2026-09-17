using System.Security.Cryptography;
using System.Text;

namespace EIMS.Authority.Recovery;

public sealed class KnowledgeServiceWave13(
    IKnowledgeWave13Store store,
    IKnowledgeBenefitSourceProviderWave13 benefitSourceProvider,
    IKnowledgeAuthorPolicyProviderWave13 authorPolicyProvider,
    ICommandPolicyCatalog? catalog = null,
    TimeProvider? clock = null)
{
    private readonly ICommandPolicyCatalog _catalog = catalog ?? new RecoveredApiCommandCatalogWave13();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async ValueTask<AuthorityResult> ExecuteAsync(
        KnowledgeCommandWave13 command,
        AuthorityActor? actor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(command);

        if (string.IsNullOrWhiteSpace(normalized.CommandName)
            || string.IsNullOrWhiteSpace(normalized.IdempotencyKey)
            || string.IsNullOrWhiteSpace(normalized.CorrelationId)
            || normalized.ExpectedVersion <= 0)
            return AuthorityResult.Deny(400, "P1_KNOWLEDGE_COMMAND_INVALID", normalized.CorrelationId ?? string.Empty);

        if (!_catalog.TryGet(normalized.CommandName, out var policy)
            || !policy.MutationContractRecovered
            || !policy.RuleContractRecovered
            || !policy.EventContractRecovered)
            return AuthorityResult.Deny(503, "P1_KNOWLEDGE_COMMAND_NOT_BOUND", normalized.CorrelationId);

        if (!ValidateActor(actor, normalized, policy, out var actorError))
            return AuthorityResult.Deny(actorError.Status, actorError.Code, normalized.CorrelationId);
        var resolvedActor = actor!;

        var aggregateId = string.Equals(normalized.CommandName, "knowledge.create-draft", StringComparison.Ordinal)
            ? normalized.BenefitId ?? string.Empty
            : normalized.KnowledgeId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(aggregateId))
            return AuthorityResult.Deny(400, "P1_KNOWLEDGE_AGGREGATE_REQUIRED", normalized.CorrelationId);

        var fingerprint = Fingerprint(normalized, resolvedActor);
        var prior = await store.GetKnowledgeIdempotencyAsync(
            normalized.CommandName,
            aggregateId,
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

        if (string.Equals(normalized.CommandName, "knowledge.create-draft", StringComparison.Ordinal))
            return await CreateDraftAsync(normalized, resolvedActor, policy, fingerprint, cancellationToken);

        return await MutateExistingAsync(normalized, resolvedActor, policy, fingerprint, cancellationToken);
    }

    private async ValueTask<AuthorityResult> CreateDraftAsync(
        KnowledgeCommandWave13 command,
        AuthorityActor actor,
        CommandPolicy policy,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var benefitId = command.BenefitId!;
        var source = await benefitSourceProvider.ResolveAsync(benefitId, cancellationToken);
        if (source is null
            || !string.Equals(source.BenefitId, benefitId, StringComparison.Ordinal)
            || source.BenefitVersion <= 0
            || !string.Equals(source.BenefitState, "REALIZED", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(source.VerificationDossierRef)
            || string.IsNullOrWhiteSpace(source.AttributionDossierRef)
            || string.IsNullOrWhiteSpace(source.RealizationDossierRef)
            || string.IsNullOrWhiteSpace(source.Scope)
            || string.IsNullOrWhiteSpace(source.EvidenceRef)
            || string.IsNullOrWhiteSpace(source.EvidenceVersion))
            return AuthorityResult.Deny(409, "P1_KNOWLEDGE_REALIZED_BENEFIT_EVIDENCE_REQUIRED", command.CorrelationId);

        if (source.BenefitVersion != command.ExpectedVersion)
            return AuthorityResult.Deny(409, "P1_KNOWLEDGE_SOURCE_VERSION_CONFLICT", command.CorrelationId);

        if (!actor.Scopes.Any(x => string.Equals(x, source.Scope, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(command.RequestedScope)
                && !string.Equals(command.RequestedScope, source.Scope, StringComparison.OrdinalIgnoreCase)))
            return AuthorityResult.Deny(403, "P1_KNOWLEDGE_SCOPE_CONTEXT_MISMATCH", command.CorrelationId);

        var authority = await authorPolicyProvider.ResolveAsync(benefitId, actor, cancellationToken);
        if (authority is null
            || !authority.Authorized
            || !string.Equals(authority.BenefitId, benefitId, StringComparison.Ordinal)
            || !string.Equals(authority.PersonId, actor.PersonId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(authority.AssignmentId, actor.AssignmentId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(authority.Scope, source.Scope, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(authority.ResolvedPolicy)
            || string.IsNullOrWhiteSpace(authority.EvidenceRef)
            || string.IsNullOrWhiteSpace(authority.EvidenceVersion))
            return AuthorityResult.Deny(403, "P1_KNOWLEDGE_AUTHOR_POLICY_REQUIRED", command.CorrelationId);

        var existing = await store.GetKnowledgeByBenefitAsync(benefitId, cancellationToken);
        if (existing is not null)
            return AuthorityResult.Deny(409, "P1_KNOWLEDGE_BENEFIT_ALREADY_HAS_ASSET", command.CorrelationId);

        var now = _clock.GetUtcNow();
        var knowledgeId = "KN-" + Guid.NewGuid().ToString("N");
        var after = new KnowledgeEnvelopeWave13(
            knowledgeId,
            source.BenefitId,
            source.ExecutionId,
            source.IdeaId,
            source.ApprovedIdeaVersion,
            "DRAFT",
            1,
            actor.PersonId,
            actor.AssignmentId,
            authority.ResolvedPolicy,
            source.Scope,
            null, null, null, null, null,
            now,
            now,
            command.CorrelationId);

        var eventName = policy.ResolveEventName()!;
        var audit = Audit(actor, after, policy.RuleSet + "|benefitEvidence=" + source.EvidenceRef + "|authorPolicy=" + authority.EvidenceRef, command, now);
        var outbox = Event(eventName, after, command.CorrelationId, now,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["knowledgeId"] = knowledgeId,
                ["benefitId"] = source.BenefitId,
                ["executionId"] = source.ExecutionId,
                ["ideaId"] = source.IdeaId,
                ["approvedIdeaVersion"] = source.ApprovedIdeaVersion.ToString(),
                ["state"] = "DRAFT"
            });

        return await store.CommitKnowledgeCreateAsync(
            new KnowledgeCommandRequestWave13(command, actor, null, policy, fingerprint),
            new KnowledgeCommitWave13(after, audit, new[] { outbox }),
            cancellationToken);
    }

    private async ValueTask<AuthorityResult> MutateExistingAsync(
        KnowledgeCommandWave13 command,
        AuthorityActor actor,
        CommandPolicy policy,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var before = await store.GetKnowledgeAsync(command.KnowledgeId!, cancellationToken);
        if (before is null)
            return AuthorityResult.Deny(404, "P1_KNOWLEDGE_NOT_FOUND", command.CorrelationId);
        if (before.Version != command.ExpectedVersion)
            return AuthorityResult.Deny(409, "P1_KNOWLEDGE_VERSION_CONFLICT", command.CorrelationId);
        if (!policy.AllowedStates.Contains(before.State, StringComparer.OrdinalIgnoreCase))
            return AuthorityResult.Deny(409, "P1_KNOWLEDGE_STATE_INVALID", command.CorrelationId);
        if (!actor.Scopes.Any(x => string.Equals(x, before.Scope, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(command.RequestedScope)
                && !string.Equals(command.RequestedScope, before.Scope, StringComparison.OrdinalIgnoreCase)))
            return AuthorityResult.Deny(403, "P1_KNOWLEDGE_SCOPE_CONTEXT_MISMATCH", command.CorrelationId);

        var now = _clock.GetUtcNow();
        KnowledgeEnvelopeWave13 after;
        string? eventName;

        if (string.Equals(command.CommandName, "knowledge.validate", StringComparison.Ordinal))
        {
            var decision = NormalizeDecision(command.Decision);
            if (decision is not ("APPROVE" or "RETURN"))
                return AuthorityResult.Deny(400, "P1_KNOWLEDGE_VALIDATION_DECISION_INVALID", command.CorrelationId);

            eventName = policy.ResolveEventName(decision);
            after = before with
            {
                State = decision == "APPROVE" ? "VALIDATED" : "DRAFT",
                Version = before.Version + 1,
                ValidatedByPersonId = decision == "APPROVE" ? actor.PersonId : null,
                ValidatedByAssignmentId = decision == "APPROVE" ? actor.AssignmentId : null,
                PublishedByPersonId = null,
                PublishedByAssignmentId = null,
                PublicationDossierRef = null,
                UpdatedAt = now,
                CorrelationId = command.CorrelationId
            };
        }
        else if (string.Equals(command.CommandName, "knowledge.publish", StringComparison.Ordinal))
        {
            var decision = NormalizeDecision(command.Decision);
            if (decision is not ("PUBLISH" or "RETURN"))
                return AuthorityResult.Deny(400, "P1_KNOWLEDGE_PUBLICATION_DECISION_INVALID", command.CorrelationId);
            if (string.Equals(decision, "PUBLISH", StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(command.PublicationDossierRef))
                return AuthorityResult.Deny(400, "P1_KNOWLEDGE_PUBLICATION_DOSSIER_REQUIRED", command.CorrelationId);
            if (string.Equals(decision, "PUBLISH", StringComparison.Ordinal)
                && string.Equals(before.ValidatedByPersonId, actor.PersonId, StringComparison.OrdinalIgnoreCase))
                return AuthorityResult.Deny(403, "SOD_KNOWLEDGE_VALIDATOR_NOT_PUBLISHER", command.CorrelationId);

            eventName = policy.ResolveEventName(decision);
            after = before with
            {
                State = decision == "PUBLISH" ? "PUBLISHED" : "DRAFT",
                Version = before.Version + 1,
                ValidatedByPersonId = decision == "RETURN" ? null : before.ValidatedByPersonId,
                ValidatedByAssignmentId = decision == "RETURN" ? null : before.ValidatedByAssignmentId,
                PublishedByPersonId = decision == "PUBLISH" ? actor.PersonId : null,
                PublishedByAssignmentId = decision == "PUBLISH" ? actor.AssignmentId : null,
                PublicationDossierRef = decision == "PUBLISH" ? command.PublicationDossierRef : null,
                UpdatedAt = now,
                CorrelationId = command.CorrelationId
            };
        }
        else
        {
            return AuthorityResult.Deny(503, "P1_KNOWLEDGE_COMMAND_NOT_BOUND", command.CorrelationId);
        }

        if (string.IsNullOrWhiteSpace(eventName))
            return AuthorityResult.Deny(503, "P1_KNOWLEDGE_EVENT_BINDING_INVALID", command.CorrelationId);

        var audit = Audit(actor, after, policy.RuleSet, command, now);
        var outbox = Event(eventName, after, command.CorrelationId, now,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["knowledgeId"] = after.KnowledgeId,
                ["benefitId"] = after.BenefitId,
                ["state"] = after.State
            });

        return await store.CommitKnowledgeCommandAsync(
            new KnowledgeCommandRequestWave13(command, actor, before, policy, fingerprint),
            new KnowledgeCommitWave13(after, audit, new[] { outbox }),
            cancellationToken);
    }

    private static bool ValidateActor(
        AuthorityActor? actor,
        KnowledgeCommandWave13 command,
        CommandPolicy policy,
        out (int Status, string Code) error)
    {
        if (actor is null
            || string.IsNullOrWhiteSpace(actor.PersonId)
            || string.IsNullOrWhiteSpace(actor.AssignmentId)
            || actor.Roles.Count != 1)
        {
            error = (401, "P1_IDENTITY_ASSIGNMENT_REQUIRED");
            return false;
        }

        var role = actor.Roles.Single();
        if (!policy.RequiredRoles.Any(x => string.Equals(x, role, StringComparison.OrdinalIgnoreCase)))
        {
            error = (403, "P1_KNOWLEDGE_COMMAND_ROLE_REQUIRED");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(command.RequestedScope)
            && !actor.Scopes.Any(x => string.Equals(x, command.RequestedScope, StringComparison.OrdinalIgnoreCase)))
        {
            error = (403, "P1_KNOWLEDGE_SCOPE_DENIED");
            return false;
        }

        error = default;
        return true;
    }

    private static KnowledgeCommandWave13 Normalize(KnowledgeCommandWave13 command) =>
        command with
        {
            CommandName = command.CommandName?.Trim() ?? string.Empty,
            KnowledgeId = Trim(command.KnowledgeId),
            BenefitId = Trim(command.BenefitId),
            IdempotencyKey = command.IdempotencyKey?.Trim() ?? string.Empty,
            CorrelationId = command.CorrelationId?.Trim() ?? string.Empty,
            RequestedScope = Trim(command.RequestedScope),
            Decision = Trim(command.Decision),
            Note = Trim(command.Note),
            PublicationDossierRef = Trim(command.PublicationDossierRef)
        };

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NormalizeDecision(string? value) => Trim(value)?.ToUpperInvariant();

    private static string Fingerprint(KnowledgeCommandWave13 command, AuthorityActor actor)
    {
        var raw = string.Join("\u001f", new[]
        {
            command.CommandName,
            command.KnowledgeId ?? string.Empty,
            command.BenefitId ?? string.Empty,
            command.ExpectedVersion.ToString(),
            command.RequestedScope ?? string.Empty,
            command.Decision ?? string.Empty,
            command.Note ?? string.Empty,
            command.PublicationDossierRef ?? string.Empty,
            actor.PersonId,
            actor.AssignmentId,
            actor.Roles.Single()
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static AuditEnvelope Audit(
        AuthorityActor actor,
        KnowledgeEnvelopeWave13 after,
        string ruleSet,
        KnowledgeCommandWave13 command,
        DateTimeOffset now) =>
        new(
            "AUD-" + Guid.NewGuid().ToString("N"),
            actor.PersonId,
            actor.NetworkIdentity,
            actor.IdentitySource,
            actor.Roles,
            actor.AssignmentId,
            after.KnowledgeId,
            after.Version,
            ruleSet,
            now,
            command.CorrelationId,
            command.CommandName);

    private static OutboxEnvelope Event(
        string eventName,
        KnowledgeEnvelopeWave13 after,
        string correlationId,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> payload) =>
        new(
            "MSG-" + Guid.NewGuid().ToString("N"),
            eventName,
            after.KnowledgeId,
            after.Version,
            correlationId,
            now,
            payload);
}
