namespace EIMS.Outbox;

/// <summary>
/// Provider-neutral outbox worker: claim -> publish -> mark. At-least-once delivery, strict per-aggregate ordering,
/// exponential backoff, dead-lettering after MaxAttempts. Hosting (BackgroundService) is wired in the Host (S4).
/// </summary>
public sealed class OutboxDispatcher
{
    private readonly IOutboxStore _store;
    private readonly IOutboxPublisher _publisher;
    private readonly OutboxDispatcherOptions _options;
    private readonly Func<DateTimeOffset> _clock;

    public OutboxDispatcher(IOutboxStore store, IOutboxPublisher publisher, OutboxDispatcherOptions options, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.WorkerId) || options.WorkerId.Length > 60)
            throw new ArgumentException("WorkerId must be 1..60 characters.", nameof(options));
        if (options.BatchSize < 1) throw new ArgumentException("BatchSize must be >= 1.", nameof(options));
        if (options.MaxAttempts < 1) throw new ArgumentException("MaxAttempts must be >= 1.", nameof(options));
        if (options.BaseDelay <= TimeSpan.Zero || options.MaxDelay < options.BaseDelay)
            throw new ArgumentException("Delays must satisfy 0 < BaseDelay <= MaxDelay.", nameof(options));
        if (options.Lease <= TimeSpan.Zero) throw new ArgumentException("Lease must be positive.", nameof(options));
        _store = store;
        _publisher = publisher;
        _options = options;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Delay before attempt number attempt+1: BaseDelay * 2^(attempt-1), capped at MaxDelay.</summary>
    public static TimeSpan ComputeDelay(int attempt, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        var factor = Math.Pow(2, Math.Min(Math.Max(attempt - 1, 0), 30));
        var ticks = Math.Min(baseDelay.Ticks * factor, maxDelay.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    public Task<OutboxRunSummary> RunOnceAsync(CancellationToken cancellationToken = default) =>
        RunOnceAsync(_clock(), cancellationToken);

    public async Task<OutboxRunSummary> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var batch = await _store.ClaimBatchAsync(
            new OutboxClaimRequest(_options.WorkerId, _options.BatchSize, now, _options.Lease, _options.Filter), cancellationToken);
        int sent = 0, retried = 0, dead = 0, lost = 0;
        foreach (var message in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OutboxPublishResult result;
            try
            {
                result = await _publisher.PublishAsync(message, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = OutboxPublishResult.Retry(ex.GetType().Name + ": " + ex.Message);
            }

            switch (result.Outcome)
            {
                case OutboxPublishOutcome.Success:
                    if (await _store.MarkSentAsync(message, now, cancellationToken)) sent++; else lost++;
                    break;
                case OutboxPublishOutcome.RetryableFailure:
                    var attempt = message.Attempts + 1;
                    if (attempt >= _options.MaxAttempts)
                    {
                        if (await _store.MarkFailedAsync(message, result.Error ?? "retries exhausted", null, cancellationToken)) dead++; else lost++;
                    }
                    else
                    {
                        var next = now + ComputeDelay(attempt, _options.BaseDelay, _options.MaxDelay);
                        if (await _store.MarkFailedAsync(message, result.Error ?? "retryable failure", next, cancellationToken)) retried++; else lost++;
                    }
                    break;
                default:
                    if (await _store.MarkFailedAsync(message, result.Error ?? "permanent failure", null, cancellationToken)) dead++; else lost++;
                    break;
            }
        }

        return new OutboxRunSummary(batch.Count, sent, retried, dead, lost);
    }

    /// <summary>Long-running loop: drains while there is work, sleeps PollInterval when idle, survives store errors.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var idle = true;
            try
            {
                var summary = await RunOnceAsync(cancellationToken);
                idle = summary.Claimed == 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _options.ErrorHandler?.Invoke(ex);
            }

            if (!idle) continue;
            try
            {
                await Task.Delay(_options.PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
