namespace EIMS.Outbox;

/// <summary>Reference implementation of the claim/lease/ordering rules (also used as the conformance baseline for Oracle).</summary>
public sealed class InMemoryOutboxStore : IOutboxStore, IOutboxInspector
{
    private sealed class Row
    {
        public long Sequence;
        public string MessageId = string.Empty;
        public string EventName = string.Empty;
        public string AggregateId = string.Empty;
        public string? AggregateType;
        public long AggregateVersion;
        public string CorrelationId = string.Empty;
        public DateTimeOffset OccurredAt;
        public string? PayloadJson;
        public OutboxStatus Status = OutboxStatus.Pending;
        public int Attempts;
        public DateTimeOffset? NextAttemptAt;
        public DateTimeOffset? SentAt;
        public string? LastError;
        public string? ClaimToken;
        public DateTimeOffset? ClaimedUntil;
    }

    private readonly object _gate = new();
    private readonly List<Row> _rows = new();
    private long _sequence;

    public long Enqueue(string messageId, string eventName, string aggregateId, string? aggregateType, long aggregateVersion,
        string correlationId, DateTimeOffset occurredAt, string? payloadJson)
    {
        lock (_gate)
        {
            var seq = ++_sequence;
            _rows.Add(new Row
            {
                Sequence = seq, MessageId = messageId, EventName = eventName, AggregateId = aggregateId, AggregateType = aggregateType,
                AggregateVersion = aggregateVersion, CorrelationId = correlationId, OccurredAt = occurredAt, PayloadJson = payloadJson
            });
            return seq;
        }
    }

    public ValueTask<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(OutboxClaimRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var types = request.Filter?.AggregateTypes;
            var token = request.WorkerId + ":" + Guid.NewGuid().ToString("N");
            var claimed = new List<OutboxMessage>();
            foreach (var row in _rows.OrderBy(x => x.Sequence))
            {
                if (claimed.Count >= request.MaxCount) break;
                if (row.Status != OutboxStatus.Pending) continue;
                if (row.NextAttemptAt is { } due && due > request.Now) continue;
                if (row.ClaimedUntil is { } until && until >= request.Now) continue;
                if (types is { Count: > 0 } && (row.AggregateType is null || !types.Contains(row.AggregateType))) continue;
                if (_rows.Any(p => p.AggregateId == row.AggregateId && p.Sequence < row.Sequence && p.Status != OutboxStatus.Sent)) continue;

                row.ClaimToken = token;
                row.ClaimedUntil = request.Now + request.Lease;
                claimed.Add(new OutboxMessage(row.Sequence, row.MessageId, row.EventName, row.AggregateId, row.AggregateType,
                    row.AggregateVersion, row.CorrelationId, row.OccurredAt, row.PayloadJson, row.Attempts, token));
            }

            return ValueTask.FromResult<IReadOnlyList<OutboxMessage>>(claimed);
        }
    }

    public ValueTask<bool> MarkSentAsync(OutboxMessage message, DateTimeOffset sentAt, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var row = Owned(message);
            if (row is null) return ValueTask.FromResult(false);
            row.Status = OutboxStatus.Sent;
            row.SentAt = sentAt;
            row.Attempts++;
            row.LastError = null;
            row.NextAttemptAt = null;
            row.ClaimToken = null;
            row.ClaimedUntil = null;
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<bool> MarkFailedAsync(OutboxMessage message, string error, DateTimeOffset? nextAttemptAt, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var row = Owned(message);
            if (row is null) return ValueTask.FromResult(false);
            row.Attempts++;
            row.LastError = error.Length > 1000 ? error[..1000] : error;
            row.NextAttemptAt = nextAttemptAt;
            row.Status = nextAttemptAt is null ? OutboxStatus.Failed : OutboxStatus.Pending;
            row.ClaimToken = null;
            row.ClaimedUntil = null;
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<bool> RequeueFailedAsync(string messageId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var row = _rows.FirstOrDefault(x => x.MessageId == messageId && x.Status == OutboxStatus.Failed);
            if (row is null) return ValueTask.FromResult(false);
            row.Status = OutboxStatus.Pending;
            row.Attempts = 0;
            row.NextAttemptAt = null;
            row.LastError = null;
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<OutboxRow?> GetOutboxRowAsync(string messageId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var r = _rows.FirstOrDefault(x => x.MessageId == messageId);
            return ValueTask.FromResult<OutboxRow?>(r is null ? null
                : new OutboxRow(r.MessageId, r.Status, r.Attempts, r.NextAttemptAt, r.SentAt, r.LastError, r.ClaimToken));
        }
    }

    private Row? Owned(OutboxMessage message) =>
        _rows.FirstOrDefault(x => x.MessageId == message.MessageId && x.Status == OutboxStatus.Pending
            && string.Equals(x.ClaimToken, message.ClaimToken, StringComparison.Ordinal));
}
