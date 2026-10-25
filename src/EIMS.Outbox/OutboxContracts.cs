namespace EIMS.Outbox;

/// <summary>A claimed outbox message handed to a publisher. Consumers must be idempotent on <see cref="MessageId"/> (at-least-once delivery).</summary>
public sealed record OutboxMessage(
    long Sequence,
    string MessageId,
    string EventName,
    string AggregateId,
    string? AggregateType,
    long AggregateVersion,
    string CorrelationId,
    DateTimeOffset OccurredAt,
    string? PayloadJson,
    int Attempts,
    string ClaimToken);

public enum OutboxStatus { Pending, Sent, Failed }

/// <summary>Operational read model of one outbox row (admin screens, monitoring, tests).</summary>
public sealed record OutboxRow(
    string MessageId,
    OutboxStatus Status,
    int Attempts,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? SentAt,
    string? LastError,
    string? ClaimToken);

/// <summary>Restricts a worker to some aggregate types (null/empty = all types).</summary>
public sealed record OutboxClaimFilter(IReadOnlyList<string>? AggregateTypes = null);

public sealed record OutboxClaimRequest(string WorkerId, int MaxCount, DateTimeOffset Now, TimeSpan Lease, OutboxClaimFilter? Filter = null);

public interface IOutboxStore
{
    /// <summary>
    /// Claims up to MaxCount due messages, in sequence order, at most one per aggregate and only when no EARLIER message of the same
    /// aggregate is still PENDING or FAILED (strict per-aggregate ordering; a dead-lettered message blocks its aggregate until requeued).
    /// A claim is a lease: after <c>Now + Lease</c> an unfinished message can be claimed again (worker crash safety).
    /// </summary>
    ValueTask<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(OutboxClaimRequest request, CancellationToken cancellationToken = default);

    /// <summary>Marks the claimed message SENT. Returns false when the claim was lost (lease expired and re-claimed).</summary>
    ValueTask<bool> MarkSentAsync(OutboxMessage message, DateTimeOffset sentAt, CancellationToken cancellationToken = default);

    /// <summary>Records a failed attempt. nextAttemptAt = null dead-letters the message (status FAILED). Returns false when the claim was lost.</summary>
    ValueTask<bool> MarkFailedAsync(OutboxMessage message, string error, DateTimeOffset? nextAttemptAt, CancellationToken cancellationToken = default);

    /// <summary>Operator action: FAILED -> PENDING with attempts reset. Returns false when the message is not FAILED.</summary>
    ValueTask<bool> RequeueFailedAsync(string messageId, CancellationToken cancellationToken = default);
}

public interface IOutboxInspector
{
    ValueTask<OutboxRow?> GetOutboxRowAsync(string messageId, CancellationToken cancellationToken = default);
}

public enum OutboxPublishOutcome { Success, RetryableFailure, PermanentFailure }

public sealed record OutboxPublishResult(OutboxPublishOutcome Outcome, string? Error = null)
{
    public static OutboxPublishResult Success { get; } = new(OutboxPublishOutcome.Success);
    public static OutboxPublishResult Retry(string error) => new(OutboxPublishOutcome.RetryableFailure, error);
    public static OutboxPublishResult Permanent(string error) => new(OutboxPublishOutcome.PermanentFailure, error);
}

/// <summary>The integration boundary: an adapter (e.g. SAP, message bus) implements this. Throwing is treated as a retryable failure.</summary>
public interface IOutboxPublisher
{
    ValueTask<OutboxPublishResult> PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}

public sealed class OutboxDispatcherOptions
{
    public string WorkerId { get; init; } = Environment.MachineName + "-" + Environment.ProcessId;
    public int BatchSize { get; init; } = 20;
    /// <summary>Delivery attempts before a message is dead-lettered (status FAILED).</summary>
    public int MaxAttempts { get; init; } = 8;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public OutboxClaimFilter? Filter { get; init; }
    public Action<Exception>? ErrorHandler { get; init; }
}

public sealed record OutboxRunSummary(int Claimed, int Sent, int Retried, int DeadLettered, int LostClaims);
