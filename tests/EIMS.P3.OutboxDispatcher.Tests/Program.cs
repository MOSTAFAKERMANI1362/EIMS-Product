using EIMS.Outbox;
using EIMS.Persistence.OracleAdapter;

// S3 conformance suite: the SAME dispatcher scenarios run against the in-memory reference store and Oracle.
// Oracle runs only when EIMS_ORACLE_CONNECTION is set. Every scenario uses a unique aggregate TYPE and the dispatcher's claim filter,
// so rows left by other tests (PENDING outbox rows from the S2 suites) are never touched. Rebuild the DB to clean up.

var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (!ok) failures++;
}

var backends = new List<(string Name, Func<OutboxBackend> Create)>
{
    ("InMemory", () =>
    {
        var s = new InMemoryOutboxStore();
        return new OutboxBackend(s, s, (id, aggregate, type, version, payload, at) =>
        {
            s.Enqueue(id, "TestEvent.v1", aggregate, type, version, "CORR-" + id, at, payload);
            return Task.CompletedTask;
        });
    })
};
var connection = Environment.GetEnvironmentVariable("EIMS_ORACLE_CONNECTION");
if (string.IsNullOrWhiteSpace(connection))
    Console.WriteLine("SKIP Oracle backend (EIMS_ORACLE_CONNECTION is not set)");
else
{
    var schema = Environment.GetEnvironmentVariable("EIMS_ORACLE_SCHEMA") ?? "EIMS_OWNER";
    backends.Add(("Oracle", () =>
    {
        var s = new OracleAuthorityStore(connection!, schema);
        return new OutboxBackend(s, s, (id, aggregate, type, version, payload, at) =>
            s.EnqueueOutboxMessageAsync(id, "TestEvent.v1", aggregate, type, version, "CORR-" + id, at, payload));
    }));
}

foreach (var (name, create) in backends)
{
    Console.WriteLine($"=== {name} ===");
    var b = create();
    var t0 = Trunc(DateTimeOffset.UtcNow);

    // 1) ordering and happy path
    var type1 = NewId("S3T");
    var aggA = NewId("AGG-A");
    var aggB = NewId("AGG-B");
    var a1 = NewId("M");
    var a2 = NewId("M");
    var a3 = NewId("M");
    var bb1 = NewId("M");
    await b.Enqueue(a1, aggA, type1, 1, "{\"k\":\"v1\"}", t0);
    await b.Enqueue(a2, aggA, type1, 2, "{\"k\":\"v2\"}", t0);
    await b.Enqueue(a3, aggA, type1, 3, "{\"k\":\"v3\"}", t0);
    await b.Enqueue(bb1, aggB, type1, 1, "{\"k\":\"vb\"}", t0);
    var pub1 = new ScriptedPublisher((_, _) => OutboxPublishResult.Success);
    var d1 = new OutboxDispatcher(b.Store, pub1, Opts(type1, "W1"));
    var run1 = await d1.RunOnceAsync(t0);
    Check($"{name}: first run claims one message per aggregate (strict per-aggregate order)", run1.Claimed == 2 && run1.Sent == 2);
    var totalSent = run1.Sent;
    for (var i = 0; i < 5 && totalSent < 4; i++) totalSent += (await d1.RunOnceAsync(t0)).Sent;
    Check($"{name}: all four messages are delivered and aggregate A arrives in version order",
        totalSent == 4 && pub1.Delivered.Where(m => m.AggregateId == aggA).Select(m => m.AggregateVersion).SequenceEqual(new long[] { 1, 2, 3 }));
    var rowsOk = true;
    foreach (var id in new[] { a1, a2, a3, bb1 })
    {
        var r = await b.Inspector.GetOutboxRowAsync(id);
        rowsOk &= r is { Status: OutboxStatus.Sent, Attempts: 1 } && r.SentAt == t0;
    }
    Check($"{name}: every row is SENT with 1 attempt and the sent-at time", rowsOk);
    var first = pub1.Delivered.First(m => m.MessageId == a1);
    Check($"{name}: published message carries all fields unchanged",
        first.EventName == "TestEvent.v1" && first.PayloadJson == "{\"k\":\"v1\"}" && first.OccurredAt == t0
        && first.CorrelationId == "CORR-" + a1 && first.AggregateType == type1 && first.AggregateVersion == 1 && first.AggregateId == aggA);

    // 2) retry with exponential backoff, then success
    var type2 = NewId("S3T");
    var m2 = NewId("M");
    await b.Enqueue(m2, NewId("AGG"), type2, 1, "{}", t0);
    var pub2 = new ScriptedPublisher((_, n) => n < 3 ? OutboxPublishResult.Retry("transient-" + n) : OutboxPublishResult.Success);
    var d2 = new OutboxDispatcher(b.Store, pub2, Opts(type2, "W1"));
    var r2a = await d2.RunOnceAsync(t0);
    var row2a = await b.Inspector.GetOutboxRowAsync(m2);
    Check($"{name}: retryable failure keeps the message PENDING and schedules the base delay",
        r2a.Retried == 1 && row2a is { Status: OutboxStatus.Pending, Attempts: 1, LastError: "transient-1" } && row2a.NextAttemptAt == t0.AddSeconds(10));
    var r2b = await d2.RunOnceAsync(t0.AddSeconds(5));
    Check($"{name}: a message is not retried before it is due", r2b.Claimed == 0);
    var r2c = await d2.RunOnceAsync(t0.AddSeconds(10));
    var row2c = await b.Inspector.GetOutboxRowAsync(m2);
    Check($"{name}: the second failure doubles the delay", r2c.Retried == 1 && row2c is { Attempts: 2 } && row2c.NextAttemptAt == t0.AddSeconds(30));
    var r2d = await d2.RunOnceAsync(t0.AddSeconds(30));
    var row2d = await b.Inspector.GetOutboxRowAsync(m2);
    Check($"{name}: the third attempt succeeds",
        r2d.Sent == 1 && row2d is { Status: OutboxStatus.Sent, Attempts: 3 } && row2d.SentAt == t0.AddSeconds(30) && pub2.Calls(m2) == 3);

    // 3) dead-letter after MaxAttempts blocks its aggregate (other aggregates unaffected); requeue restores order
    var type3 = NewId("S3T");
    var aggD = NewId("AGG-D");
    var aggE = NewId("AGG-E");
    var dm1 = NewId("M");
    var dm2 = NewId("M");
    var em1 = NewId("M");
    await b.Enqueue(dm1, aggD, type3, 1, "{}", t0);
    await b.Enqueue(dm2, aggD, type3, 2, "{}", t0);
    await b.Enqueue(em1, aggE, type3, 1, "{}", t0);
    var pub3 = new ScriptedPublisher((m, n) => m.MessageId == dm1 && n <= 3 ? OutboxPublishResult.Retry("fail-" + n) : OutboxPublishResult.Success);
    var d3 = new OutboxDispatcher(b.Store, pub3, Opts(type3, "W1"));
    await d3.RunOnceAsync(t0);
    await d3.RunOnceAsync(t0.AddSeconds(10));
    var r3 = await d3.RunOnceAsync(t0.AddSeconds(30));
    var row3 = await b.Inspector.GetOutboxRowAsync(dm1);
    Check($"{name}: retries exhausted -> dead-lettered (FAILED) with the last error",
        r3.DeadLettered == 1 && row3 is { Status: OutboxStatus.Failed, Attempts: 3, LastError: "fail-3" });
    await d3.RunOnceAsync(t0.AddSeconds(90));
    Check($"{name}: a dead-lettered message blocks its aggregate while other aggregates are delivered",
        pub3.Calls(dm2) == 0 && pub3.Calls(em1) == 1);
    var requeued = await b.Store.RequeueFailedAsync(dm1);
    await d3.RunOnceAsync(t0.AddSeconds(100));
    await d3.RunOnceAsync(t0.AddSeconds(100));
    Check($"{name}: requeue resumes delivery in the original order",
        requeued && pub3.Delivered.Where(m => m.AggregateId == aggD).Select(m => m.MessageId).SequenceEqual(new[] { dm1, dm2 }));

    // 4) permanent failure dead-letters immediately
    var type4 = NewId("S3T");
    var m4 = NewId("M");
    await b.Enqueue(m4, NewId("AGG"), type4, 1, "{}", t0);
    var d4 = new OutboxDispatcher(b.Store, new ScriptedPublisher((_, _) => OutboxPublishResult.Permanent("bad-payload")), Opts(type4, "W1"));
    await d4.RunOnceAsync(t0);
    var row4 = await b.Inspector.GetOutboxRowAsync(m4);
    Check($"{name}: permanent failure -> FAILED after 1 attempt", row4 is { Status: OutboxStatus.Failed, Attempts: 1, LastError: "bad-payload" });

    // 5) a publisher exception is a retryable failure
    var type5 = NewId("S3T");
    var m5 = NewId("M");
    await b.Enqueue(m5, NewId("AGG"), type5, 1, "{}", t0);
    var d5 = new OutboxDispatcher(b.Store,
        new ScriptedPublisher((_, n) => n == 1 ? throw new InvalidOperationException("boom") : OutboxPublishResult.Success), Opts(type5, "W1"));
    var r5a = await d5.RunOnceAsync(t0);
    var row5a = await b.Inspector.GetOutboxRowAsync(m5);
    Check($"{name}: a publisher exception is recorded and retried",
        r5a.Retried == 1 && row5a is { Status: OutboxStatus.Pending, Attempts: 1 } && row5a.LastError!.Contains("boom", StringComparison.Ordinal));
    var r5b = await d5.RunOnceAsync(t0.AddSeconds(10));
    Check($"{name}: the retry after an exception is delivered", r5b.Sent == 1);

    // 6) lease: a crashed worker's claim expires and the message is re-claimed (at-least-once); the stale claim cannot finalize
    var type6 = NewId("S3T");
    var m6 = NewId("M");
    await b.Enqueue(m6, NewId("AGG"), type6, 1, "{}", t0);
    var filter6 = new OutboxClaimFilter(new[] { type6 });
    var lease = TimeSpan.FromSeconds(30);
    var c1 = await b.Store.ClaimBatchAsync(new OutboxClaimRequest("W1", 5, t0, lease, filter6));
    var c2 = await b.Store.ClaimBatchAsync(new OutboxClaimRequest("W2", 5, t0.AddSeconds(10), lease, filter6));
    var c3 = await b.Store.ClaimBatchAsync(new OutboxClaimRequest("W2", 5, t0.AddSeconds(31), lease, filter6));
    Check($"{name}: a leased message is not claimable by another worker before the lease expires", c1.Count == 1 && c2.Count == 0);
    Check($"{name}: an expired lease is re-claimed with a new token", c3.Count == 1 && c3[0].ClaimToken != c1[0].ClaimToken);
    var lateSent = await b.Store.MarkSentAsync(c1[0], t0.AddSeconds(32));
    var currentSent = await b.Store.MarkSentAsync(c3[0], t0.AddSeconds(32));
    var row6 = await b.Inspector.GetOutboxRowAsync(m6);
    Check($"{name}: the stale claim cannot finalize, the current claim can",
        !lateSent && currentSent && row6 is { Status: OutboxStatus.Sent, Attempts: 1 });

    // 7) two concurrent workers deliver every message exactly once
    var type7 = NewId("S3T");
    var ids7 = Enumerable.Range(0, 20).Select(_ => NewId("M")).ToArray();
    foreach (var id in ids7) await b.Enqueue(id, NewId("AGG"), type7, 1, "{}", t0);
    var pub7 = new ScriptedPublisher((_, _) => OutboxPublishResult.Success);
    async Task Worker(string worker)
    {
        var d = new OutboxDispatcher(b.Store, pub7, Opts(type7, worker));
        for (var i = 0; i < 40 && pub7.UniqueDelivered < 20; i++) await d.RunOnceAsync(t0);
    }
    await Task.WhenAll(Worker("W1"), Worker("W2"));
    Check($"{name}: two concurrent workers deliver all 20 messages exactly once",
        pub7.UniqueDelivered == 20 && ids7.All(id => pub7.Calls(id) == 1));
    var allSent = true;
    foreach (var id in ids7) allSent &= (await b.Inspector.GetOutboxRowAsync(id)) is { Status: OutboxStatus.Sent };
    Check($"{name}: all concurrently delivered rows are SENT", allSent);

    // 8) the claim filter isolates aggregate types
    var otherType = NewId("S3T-OTHER");
    var m8 = NewId("M");
    await b.Enqueue(m8, NewId("AGG"), otherType, 1, "{}", t0);
    var d8 = new OutboxDispatcher(b.Store, new ScriptedPublisher((_, _) => OutboxPublishResult.Success), Opts(type1, "W9"));
    var r8 = await d8.RunOnceAsync(t0);
    var row8 = await b.Inspector.GetOutboxRowAsync(m8);
    Check($"{name}: messages of other aggregate types are left untouched",
        r8.Claimed == 0 && row8 is { Status: OutboxStatus.Pending, Attempts: 0, ClaimToken: null });
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;

static string NewId(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N");

static DateTimeOffset Trunc(DateTimeOffset value) => new(value.Ticks - value.Ticks % 10, TimeSpan.Zero);

static OutboxDispatcherOptions Opts(string aggregateType, string worker) => new()
{
    WorkerId = worker,
    BatchSize = 10,
    MaxAttempts = 3,
    BaseDelay = TimeSpan.FromSeconds(10),
    MaxDelay = TimeSpan.FromSeconds(60),
    Lease = TimeSpan.FromSeconds(30),
    Filter = new OutboxClaimFilter(new[] { aggregateType })
};

internal sealed record OutboxBackend(
    IOutboxStore Store,
    IOutboxInspector Inspector,
    Func<string, string, string, long, string, DateTimeOffset, Task> Enqueue);

internal sealed class ScriptedPublisher : IOutboxPublisher
{
    private readonly Func<OutboxMessage, int, OutboxPublishResult> _script;
    private readonly object _gate = new();
    private readonly List<OutboxMessage> _delivered = new();
    private readonly Dictionary<string, int> _calls = new();

    public ScriptedPublisher(Func<OutboxMessage, int, OutboxPublishResult> script) => _script = script;

    public ValueTask<OutboxPublishResult> PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        int call;
        lock (_gate)
        {
            call = _calls.GetValueOrDefault(message.MessageId) + 1;
            _calls[message.MessageId] = call;
        }

        var result = _script(message, call);
        if (result.Outcome == OutboxPublishOutcome.Success)
            lock (_gate) _delivered.Add(message);
        return ValueTask.FromResult(result);
    }

    public IReadOnlyList<OutboxMessage> Delivered
    {
        get { lock (_gate) return _delivered.ToList(); }
    }

    public int UniqueDelivered
    {
        get { lock (_gate) return _delivered.Select(x => x.MessageId).Distinct().Count(); }
    }

    public int Calls(string messageId)
    {
        lock (_gate) return _calls.GetValueOrDefault(messageId);
    }
}
