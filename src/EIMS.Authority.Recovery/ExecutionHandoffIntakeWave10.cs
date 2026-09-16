using System.Security.Cryptography;
using System.Text;

namespace EIMS.Authority.Recovery;

public sealed record ExecutionHandoffSourceEvidenceWave10(
    string ExecutionId,
    string RecommendationId,
    string CandidateId,
    string IdeaId,
    long ApprovedIdeaVersion,
    string State,
    long Version,
    string EvidenceRef,
    string EvidenceVersion);

public interface IExecutionHandoffSourceProviderWave10
{
    ValueTask<ExecutionHandoffSourceEvidenceWave10?> ResolveAsync(
        string executionId,
        CancellationToken cancellationToken = default);
}

public sealed class StaticExecutionHandoffSourceProviderWave10(
    params ExecutionHandoffSourceEvidenceWave10[] evidence) : IExecutionHandoffSourceProviderWave10
{
    private readonly IReadOnlyDictionary<string, ExecutionHandoffSourceEvidenceWave10> _byExecution =
        evidence.ToDictionary(x => x.ExecutionId, StringComparer.Ordinal);

    public ValueTask<ExecutionHandoffSourceEvidenceWave10?> ResolveAsync(
        string executionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_byExecution.TryGetValue(executionId, out var value) ? value : null);
    }
}

public sealed record ExecutionHandoffIntakeRequestWave10(
    OutboxEnvelope SourceEvent,
    ExecutionHandoffSourceEvidenceWave10 SourceEvidence,
    string Fingerprint);

public sealed record ExecutionHandoffIntakeCommitWave10(
    ExecutionThreadEnvelopeWave10 Thread,
    AuditEnvelope Audit);

public interface IExecutionHandoffIntakeStoreWave10
{
    ValueTask<IdempotencyRecord?> GetExecutionIntakeIdempotencyAsync(
        string executionId,
        string sourceMessageId,
        CancellationToken cancellationToken = default);

    ValueTask<AuthorityResult> CommitExecutionIntakeAsync(
        ExecutionHandoffIntakeRequestWave10 request,
        ExecutionHandoffIntakeCommitWave10 commit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// System-only consumer for the authoritative Wave 9 ExecutionCreatedFromRecommendation.v1 event.
/// The event is a trigger, not the source of full domain data. Full handoff facts are reloaded from
/// a server-side provider so missing/minimal event payload cannot become authority.
/// </summary>
public sealed class ExecutionHandoffEventHandlerWave10(
    IExecutionWave10Store executionStore,
    IExecutionHandoffIntakeStoreWave10 intakeStore,
    IExecutionHandoffSourceProviderWave10 sourceProvider,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async ValueTask<AuthorityResult> HandleAsync(
        OutboxEnvelope sourceEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(sourceEvent.EventName, "ExecutionCreatedFromRecommendation.v1", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(sourceEvent.MessageId)
            || string.IsNullOrWhiteSpace(sourceEvent.AggregateId)
            || sourceEvent.AggregateVersion != 1
            || string.IsNullOrWhiteSpace(sourceEvent.CorrelationId))
            return AuthorityResult.Deny(400, "P1_EXECUTION_HANDOFF_EVENT_INVALID", sourceEvent.CorrelationId ?? string.Empty);

        var executionId = sourceEvent.AggregateId.Trim();
        var source = await sourceProvider.ResolveAsync(executionId, cancellationToken);
        if (!ValidSource(source, executionId))
            return AuthorityResult.Deny(409, "P1_EXECUTION_HANDOFF_SOURCE_EVIDENCE_REQUIRED", sourceEvent.CorrelationId);
        var resolved = source!;

        if (!EventPayloadConsistent(sourceEvent.Payload, resolved))
            return AuthorityResult.Deny(409, "P1_EXECUTION_HANDOFF_EVENT_SOURCE_MISMATCH", sourceEvent.CorrelationId);

        var fingerprint = Fingerprint(sourceEvent.MessageId, resolved);
        var prior = await intakeStore.GetExecutionIntakeIdempotencyAsync(executionId, sourceEvent.MessageId, cancellationToken);
        if (prior is not null)
        {
            if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
                return AuthorityResult.Deny(409, "P1_IDEMPOTENCY_CONFLICT", sourceEvent.CorrelationId);
            return prior.Result with
            {
                IdempotentReplay = true,
                StateMutated = false,
                CorrelationId = sourceEvent.CorrelationId
            };
        }

        var existing = await executionStore.GetExecutionAsync(executionId, cancellationToken);
        if (existing is not null)
        {
            if (!Matches(existing.Execution, resolved))
                return AuthorityResult.Deny(409, "P1_EXECUTION_HANDOFF_THREAD_CONFLICT", sourceEvent.CorrelationId);

            return new AuthorityResult(
                200,
                "P1_EXECUTION_HANDOFF_ALREADY_MATERIALIZED",
                true,
                false,
                true,
                existing.Execution.Version,
                sourceEvent.CorrelationId,
                Array.Empty<string>());
        }

        var now = _clock.GetUtcNow();
        var execution = new ExecutionEnvelopeWave10(
            resolved.ExecutionId,
            resolved.RecommendationId,
            resolved.CandidateId,
            resolved.IdeaId,
            resolved.ApprovedIdeaVersion,
            "PLANNING",
            1,
            false,
            false,
            0,
            null,
            false,
            null,
            null,
            null,
            null,
            null,
            now,
            now,
            sourceEvent.CorrelationId);
        var thread = new ExecutionThreadEnvelopeWave10(execution, null);
        var audit = new AuditEnvelope(
            "AUD-" + Guid.NewGuid().ToString("N"),
            "SYSTEM",
            "SYSTEM",
            "SYSTEM_EVENT",
            new[] { "SYSTEM_SERVICE" },
            "SYSTEM",
            execution.ExecutionId,
            execution.Version,
            "P1-EXECUTION-HANDOFF-ACR-P0-008-1.0|SOURCE:" + resolved.EvidenceRef + "@" + resolved.EvidenceVersion,
            now,
            sourceEvent.CorrelationId,
            "system.execution.materialize-from-handoff");

        return await intakeStore.CommitExecutionIntakeAsync(
            new ExecutionHandoffIntakeRequestWave10(sourceEvent, resolved, fingerprint),
            new ExecutionHandoffIntakeCommitWave10(thread, audit),
            cancellationToken);
    }

    private static bool ValidSource(ExecutionHandoffSourceEvidenceWave10? source, string executionId) =>
        source is not null
        && string.Equals(source.ExecutionId, executionId, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(source.RecommendationId)
        && !string.IsNullOrWhiteSpace(source.CandidateId)
        && !string.IsNullOrWhiteSpace(source.IdeaId)
        && source.ApprovedIdeaVersion > 0
        && string.Equals(source.State, "PLANNING", StringComparison.Ordinal)
        && source.Version == 1
        && !string.IsNullOrWhiteSpace(source.EvidenceRef)
        && !string.IsNullOrWhiteSpace(source.EvidenceVersion);

    private static bool EventPayloadConsistent(
        IReadOnlyDictionary<string, string>? payload,
        ExecutionHandoffSourceEvidenceWave10 source)
    {
        if (payload is null) return true;
        return MatchIfPresent(payload, "executionId", source.ExecutionId)
            && MatchIfPresent(payload, "recommendationId", source.RecommendationId)
            && MatchIfPresent(payload, "ideaId", source.IdeaId)
            && MatchIfPresent(payload, "approvedIdeaVersion", source.ApprovedIdeaVersion.ToString())
            && MatchIfPresent(payload, "state", source.State);
    }

    private static bool MatchIfPresent(IReadOnlyDictionary<string, string> payload, string key, string expected) =>
        !payload.TryGetValue(key, out var value) || string.Equals(value, expected, StringComparison.Ordinal);

    private static bool Matches(ExecutionEnvelopeWave10 execution, ExecutionHandoffSourceEvidenceWave10 source) =>
        string.Equals(execution.ExecutionId, source.ExecutionId, StringComparison.Ordinal)
        && string.Equals(execution.RecommendationId, source.RecommendationId, StringComparison.Ordinal)
        && string.Equals(execution.CandidateId, source.CandidateId, StringComparison.Ordinal)
        && string.Equals(execution.IdeaId, source.IdeaId, StringComparison.Ordinal)
        && execution.ApprovedIdeaVersion == source.ApprovedIdeaVersion;

    private static string Fingerprint(string sourceMessageId, ExecutionHandoffSourceEvidenceWave10 source)
    {
        var raw = string.Join('|', new[]
        {
            sourceMessageId,
            source.ExecutionId,
            source.RecommendationId,
            source.CandidateId,
            source.IdeaId,
            source.ApprovedIdeaVersion.ToString(),
            source.State,
            source.Version.ToString(),
            source.EvidenceRef,
            source.EvidenceVersion
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }
}
