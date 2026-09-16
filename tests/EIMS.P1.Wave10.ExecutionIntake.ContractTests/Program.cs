using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;

namespace EIMS.P1.Wave10.ExecutionIntake.ContractTests;

internal static class Program
{
    private const string ExecutionId = "EX-HANDOFF-1";

    public static async Task<int> Main()
    {
        var tests = new List<(string Name, Func<Task> Run)>
        {
            ("W10-I01 authoritative handoff materializes PLANNING execution", ValidHandoffMaterializes),
            ("W10-I02 exact handoff replay is idempotent", ExactReplayIdempotent),
            ("W10-I03 conflicting handoff thread fails closed", ConflictingThreadDenied),
            ("W10-I04 invalid event identity is rejected", InvalidEventRejected),
            ("W10-I05 event payload cannot contradict server source evidence", EventPayloadMismatchDenied),
            ("W10-I06 intake persistence fault rolls back state audit and idempotency", IntakeRollback),
            ("W10-I07 guarded service requires actor scope to contain ownership scope even without requestedScope", GuardedScopeRequired),
            ("W10-I08 materialized execution can enter owner lifecycle only through server ownership evidence", MaterializedExecutionUsesOwnershipEvidence)
        };

        var passed = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                await run();
                passed++;
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL {name}: {ex.Message}");
            }
        }

        Console.WriteLine($"RESULT {passed}/{tests.Count} PASS");
        return passed == tests.Count ? 0 : 1;
    }

    private static async Task ValidHandoffMaterializes()
    {
        var store = new ExecutionRuntimeStoreWave10();
        var result = await Handler(store, Source()).HandleAsync(Event());
        Equal(200, result.HttpStatus);
        Equal("P1_EXECUTION_HANDOFF_MATERIALIZED", result.Code);
        True(result.StateMutated);
        Equal(0, result.EmittedEvents.Count);

        var thread = await store.GetExecutionAsync(ExecutionId);
        True(thread is not null);
        Equal("PLANNING", thread!.Execution.State);
        Equal(1L, thread.Execution.Version);
        Equal("REC-1", thread.Execution.RecommendationId);
        Equal("PIC-1", thread.Execution.CandidateId);
        Equal("IDEA-1", thread.Execution.IdeaId);
        Equal(7L, thread.Execution.ApprovedIdeaVersion);
        False(thread.Execution.CharterApproved);
        False(thread.Execution.PlanApproved);
        Equal(0, thread.Execution.ProgressPercent);
        Equal(1, store.AuditLog.Count);
        Equal(0, store.Outbox.Count);
        Equal(1, store.IdempotencyRecords.Count);
    }

    private static async Task ExactReplayIdempotent()
    {
        var store = new ExecutionRuntimeStoreWave10();
        var handler = Handler(store, Source());
        var first = await handler.HandleAsync(Event());
        var replay = await handler.HandleAsync(Event() with { CorrelationId = "CORR-RETRY" });
        Equal(200, first.HttpStatus);
        Equal(200, replay.HttpStatus);
        True(replay.IdempotentReplay);
        False(replay.StateMutated);
        Equal(1, store.Threads.Count);
        Equal(1, store.AuditLog.Count);
        Equal(1, store.IdempotencyRecords.Count);
    }

    private static async Task ConflictingThreadDenied()
    {
        var store = new ExecutionRuntimeStoreWave10();
        Equal(200, (await Handler(store, Source()).HandleAsync(Event())).HttpStatus);

        var conflicting = Source() with { CandidateId = "PIC-CONFLICT", EvidenceRef = "PORTFOLIO:CONFLICT" };
        var secondEvent = Event() with
        {
            MessageId = "MSG-2",
            CorrelationId = "CORR-2",
            Payload = new Dictionary<string,string>
            {
                ["executionId"] = ExecutionId,
                ["recommendationId"] = "REC-1",
                ["ideaId"] = "IDEA-1",
                ["approvedIdeaVersion"] = "7",
                ["state"] = "PLANNING"
            }
        };
        var conflict = await Handler(store, conflicting).HandleAsync(secondEvent);
        Equal(409, conflict.HttpStatus);
        Equal("P1_EXECUTION_HANDOFF_THREAD_CONFLICT", conflict.Code);
        Equal(1, store.Threads.Count);
    }

    private static async Task InvalidEventRejected()
    {
        var store = new ExecutionRuntimeStoreWave10();
        var result = await Handler(store, Source()).HandleAsync(Event() with { EventName = "ExecutionHandoffRequested.v1" });
        Equal(400, result.HttpStatus);
        Equal("P1_EXECUTION_HANDOFF_EVENT_INVALID", result.Code);
        Equal(0, store.Threads.Count);
    }

    private static async Task EventPayloadMismatchDenied()
    {
        var store = new ExecutionRuntimeStoreWave10();
        var badPayload = new Dictionary<string,string>
        {
            ["executionId"] = ExecutionId,
            ["recommendationId"] = "REC-WRONG",
            ["ideaId"] = "IDEA-1",
            ["approvedIdeaVersion"] = "7",
            ["state"] = "PLANNING"
        };
        var result = await Handler(store, Source()).HandleAsync(Event() with { Payload = badPayload });
        Equal(409, result.HttpStatus);
        Equal("P1_EXECUTION_HANDOFF_EVENT_SOURCE_MISMATCH", result.Code);
        Equal(0, store.Threads.Count);
    }

    private static async Task IntakeRollback()
    {
        var store = new ExecutionRuntimeStoreWave10
        {
            IntakeFaultPoint = ExecutionIntakeWave10PersistenceFaultPoint.BeforeCommitPublish
        };
        var threw = false;
        try
        {
            await Handler(store, Source()).HandleAsync(Event());
        }
        catch (PersistenceAtomicityException)
        {
            threw = true;
        }

        True(threw);
        Equal(0, store.Threads.Count);
        Equal(0, store.AuditLog.Count);
        Equal(0, store.IdempotencyRecords.Count);
        Equal(0, store.Outbox.Count);
    }

    private static async Task GuardedScopeRequired()
    {
        var store = new ExecutionRuntimeStoreWave10(PlanningThread());
        var ownership = new StaticExecutionOwnershipEvidenceProviderWave10(Ownership());
        var guarded = Guarded(store, ownership);
        var actorWithoutScope = Owner() with { Scopes = new[] { "UNIT:OTHER" } };
        var command = new ExecutionCommandWave10(
            "executions.approve-charter",
            ExecutionId,
            1,
            "IDEM-SCOPE",
            "CORR-SCOPE",
            RequestedScope: null);

        var result = await guarded.ExecuteAsync(command, actorWithoutScope);
        Equal(403, result.HttpStatus);
        Equal("P1_EXECUTION_SCOPE_CONTEXT_MISMATCH", result.Code);
        False((await store.GetExecutionAsync(ExecutionId))!.Execution.CharterApproved);
    }

    private static async Task MaterializedExecutionUsesOwnershipEvidence()
    {
        var store = new ExecutionRuntimeStoreWave10();
        Equal(200, (await Handler(store, Source()).HandleAsync(Event())).HttpStatus);

        var noOwnership = new StaticExecutionOwnershipEvidenceProviderWave10();
        var guardedMissing = Guarded(store, noOwnership);
        var denied = await guardedMissing.ExecuteAsync(
            new ExecutionCommandWave10("executions.approve-charter", ExecutionId, 1, "IDEM-1", "CORR-1", "UNIT:RND"),
            Owner());
        Equal(409, denied.HttpStatus);
        Equal("P1_EXECUTION_OWNERSHIP_EVIDENCE_REQUIRED", denied.Code);

        var ownership = new StaticExecutionOwnershipEvidenceProviderWave10(Ownership());
        var allowed = await Guarded(store, ownership).ExecuteAsync(
            new ExecutionCommandWave10("executions.approve-charter", ExecutionId, 1, "IDEM-2", "CORR-2", "UNIT:RND"),
            Owner());
        Equal(200, allowed.HttpStatus);
        True((await store.GetExecutionAsync(ExecutionId))!.Execution.CharterApproved);
    }

    private static ExecutionHandoffEventHandlerWave10 Handler(
        ExecutionRuntimeStoreWave10 store,
        ExecutionHandoffSourceEvidenceWave10 source) =>
        new(store, store, new StaticExecutionHandoffSourceProviderWave10(source));

    private static ExecutionServiceWave10Guarded Guarded(
        IExecutionWave10Store store,
        IExecutionOwnershipEvidenceProviderWave10 ownership)
    {
        var inner = new ExecutionServiceWave10(
            store,
            ownership,
            new StaticBenefitAcceptanceEvidenceProviderWave10(),
            new RecoveredApiCommandCatalogWave10());
        return new ExecutionServiceWave10Guarded(inner, ownership);
    }

    private static ExecutionHandoffSourceEvidenceWave10 Source() =>
        new(
            ExecutionId,
            "REC-1",
            "PIC-1",
            "IDEA-1",
            7,
            "PLANNING",
            1,
            "PORTFOLIO-THREAD:PIC-1",
            "9");

    private static OutboxEnvelope Event() =>
        new(
            "MSG-1",
            "ExecutionCreatedFromRecommendation.v1",
            ExecutionId,
            1,
            "CORR-1",
            DateTimeOffset.Parse("2026-09-16T00:00:00Z"),
            new Dictionary<string,string>
            {
                ["executionId"] = ExecutionId,
                ["recommendationId"] = "REC-1",
                ["ideaId"] = "IDEA-1",
                ["approvedIdeaVersion"] = "7",
                ["state"] = "PLANNING"
            });

    private static ExecutionOwnershipEvidenceWave10 Ownership() =>
        new(ExecutionId, "P-OWNER", "ASG-OWNER", "UNIT:RND", "EXEC-OWNER-REGISTRY:" + ExecutionId, "1");

    private static AuthorityActor Owner() =>
        new("P-OWNER", "DOMAIN\\owner", "WINDOWS", "ASG-OWNER", new[] { "EXECUTION_OWNER" }, new[] { "UNIT:RND" });

    private static ExecutionThreadEnvelopeWave10 PlanningThread()
    {
        var now = DateTimeOffset.Parse("2026-09-16T00:00:00Z");
        return new ExecutionThreadEnvelopeWave10(
            new ExecutionEnvelopeWave10(
                ExecutionId,
                "REC-1",
                "PIC-1",
                "IDEA-1",
                7,
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
                "SEED"),
            null);
    }

    private static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    private static void False(bool value)
    {
        if (value) throw new InvalidOperationException("Expected false.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
    }
}
