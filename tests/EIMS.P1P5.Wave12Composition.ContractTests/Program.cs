using EIMS.Authority.Recovery;
using EIMS.Identity.Rbac;
using EIMS.Persistence.Recovery;
using EIMS.PilotAssembly.Binding;
using EIMS.PilotAssembly.Core;

namespace EIMS.P1P5.Wave12Composition.ContractTests;

internal static class Program
{
    public static async Task<int> Main()
    {
        var tests = new List<(string Name, Func<Task> Run)>
        {
            ("P1P5-W12C-01 gateway reaches guarded Execution service and atomically mutates state", ExecutionRealComposition),
            ("P1P5-W12C-02 guarded Execution scope check remains effective when requestedScope is omitted", ExecutionGuardSurvivesGateway),
            ("P1P5-W12C-03 gateway reaches Benefit service and atomically accepts obligation", BenefitRealComposition),
            ("P1P5-W12C-04 Benefit server ownership still defeats a role-correct but wrong owner", BenefitOwnershipSurvivesGateway)
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

    private static async Task ExecutionRealComposition()
    {
        var store = new ExecutionTransactionalStoreWave10(ExecutionThread());
        var ownership = new StaticExecutionOwnershipEvidenceProviderWave10(
            new ExecutionOwnershipEvidenceWave10("EX-1", "P-EXEC", "ASG-EXEC", "UNIT:RND", "EX-OWNER-EV", "1"));
        var inner = new ExecutionServiceWave10(
            store,
            ownership,
            new StaticBenefitAcceptanceEvidenceProviderWave10(),
            new RecoveredApiCommandCatalogWave10());
        var executor = new ExecutionExecutor(new ExecutionServiceWave10Guarded(inner, ownership));
        var gateway = Gateway(
            personId: "P-EXEC",
            assignmentId: "ASG-EXEC",
            role: "EXECUTION_OWNER",
            scopes: new[] { "UNIT:RND" },
            execution: executor);

        var result = await gateway.ExecuteAsync(new CommandAttempt(
            "executions.approve-charter",
            "DOMAIN\\user",
            "CORR-EX",
            1,
            "IDEM-EX",
            "{\"assignmentId\":\"ASG-EXEC\",\"requestedScope\":\"UNIT:RND\",\"executionId\":\"EX-1\"}"));

        Equal(200, result.HttpStatus);
        True(result.StateMutated);
        var after = store.Threads.Single().Execution;
        True(after.CharterApproved);
        Equal(2L, after.Version);
        Equal(1, store.ExecutionAuditLog.Count);
        Equal(1, store.ExecutionOutbox.Count);
        Equal(1, store.ExecutionIdempotencyRecords.Count);
    }

    private static async Task ExecutionGuardSurvivesGateway()
    {
        var store = new ExecutionTransactionalStoreWave10(ExecutionThread());
        var ownership = new StaticExecutionOwnershipEvidenceProviderWave10(
            new ExecutionOwnershipEvidenceWave10("EX-1", "P-EXEC", "ASG-EXEC", "UNIT:RND", "EX-OWNER-EV", "1"));
        var inner = new ExecutionServiceWave10(
            store,
            ownership,
            new StaticBenefitAcceptanceEvidenceProviderWave10(),
            new RecoveredApiCommandCatalogWave10());
        var executor = new ExecutionExecutor(new ExecutionServiceWave10Guarded(inner, ownership));
        var gateway = Gateway(
            personId: "P-EXEC",
            assignmentId: "ASG-EXEC",
            role: "EXECUTION_OWNER",
            scopes: new[] { "UNIT:OTHER" },
            execution: executor);

        var result = await gateway.ExecuteAsync(new CommandAttempt(
            "executions.approve-charter",
            "DOMAIN\\user",
            "CORR-EX-GUARD",
            1,
            "IDEM-EX-GUARD",
            "{\"assignmentId\":\"ASG-EXEC\",\"executionId\":\"EX-1\"}"));

        Equal(403, result.HttpStatus);
        Equal("P1_EXECUTION_SCOPE_CONTEXT_MISMATCH", result.Code);
        False(result.StateMutated);
        Equal(1L, store.Threads.Single().Execution.Version);
        Equal(0, store.ExecutionAuditLog.Count);
    }

    private static async Task BenefitRealComposition()
    {
        var store = new BenefitTransactionalStoreWave11(Benefit());
        var service = BenefitService(store, "P-BEN", "ASG-BEN");
        var gateway = Gateway(
            personId: "P-BEN",
            assignmentId: "ASG-BEN",
            role: "BENEFIT_OWNER",
            scopes: new[] { "UNIT:RND" },
            benefit: new BenefitExecutor(service));

        var result = await gateway.ExecuteAsync(new CommandAttempt(
            "benefits.accept",
            "DOMAIN\\user",
            "CORR-BEN",
            1,
            "IDEM-BEN",
            "{\"assignmentId\":\"ASG-BEN\",\"requestedScope\":\"UNIT:RND\",\"benefitId\":\"BEN-1\"}"));

        Equal(200, result.HttpStatus);
        True(result.StateMutated);
        var after = store.Benefits.Single();
        Equal("BASELINE_REQUIRED", after.State);
        Equal(2L, after.Version);
        Equal("P-BEN", after.AcceptedByPersonId);
        Equal("ASG-BEN", after.AcceptedByAssignmentId);
        Equal(1, store.BenefitAuditLog.Count);
        Equal(1, store.BenefitOutbox.Count);
        Equal(1, store.BenefitIdempotencyRecords.Count);
    }

    private static async Task BenefitOwnershipSurvivesGateway()
    {
        var store = new BenefitTransactionalStoreWave11(Benefit());
        var service = BenefitService(store, "P-BEN", "ASG-BEN");
        var gateway = Gateway(
            personId: "P-OTHER",
            assignmentId: "ASG-OTHER",
            role: "BENEFIT_OWNER",
            scopes: new[] { "UNIT:RND" },
            benefit: new BenefitExecutor(service));

        var result = await gateway.ExecuteAsync(new CommandAttempt(
            "benefits.accept",
            "DOMAIN\\user",
            "CORR-BEN-DENY",
            1,
            "IDEM-BEN-DENY",
            "{\"assignmentId\":\"ASG-OTHER\",\"requestedScope\":\"UNIT:RND\",\"benefitId\":\"BEN-1\"}"));

        Equal(403, result.HttpStatus);
        Equal("P1_BENEFIT_OWNER_CONTEXT_MISMATCH", result.Code);
        False(result.StateMutated);
        Equal("OBLIGATION_PENDING_ACCEPTANCE", store.Benefits.Single().State);
        Equal(0, store.BenefitAuditLog.Count);
    }

    private static BenefitServiceWave11 BenefitService(
        BenefitTransactionalStoreWave11 store,
        string ownerPersonId,
        string ownerAssignmentId) =>
        new(
            store,
            new StaticBenefitOwnershipEvidenceProviderWave11(
                new BenefitOwnershipEvidenceWave11("BEN-1", ownerPersonId, ownerAssignmentId, "UNIT:RND", "BEN-OWNER-EV", "1")),
            new StaticBenefitBaselineTargetEvidenceProviderWave11(),
            new StaticBenefitExecutionOwnerEvidenceProviderWave11(),
            new BenefitOwnerOnlyMeasurementAuthorityProviderWave11(),
            new StaticBenefitKnowledgePublicationEvidenceProviderWave11(),
            new RecoveredApiCommandCatalogWave11());

    private static P1RecoveryCommandGateway Gateway(
        string personId,
        string assignmentId,
        string role,
        IReadOnlyCollection<string> scopes,
        IP1ExecutionExecutor? execution = null,
        IP1BenefitExecutor? benefit = null)
    {
        var directory = new InMemoryIdentityDirectoryStore(
            new[] { new PersonDirectoryEntry(personId, "DOMAIN\\user", DirectoryPersonStatus.Active) },
            new[]
            {
                new RoleAssignmentEntry(
                    assignmentId,
                    personId,
                    role,
                    scopes,
                    DateTimeOffset.UtcNow.AddDays(-1),
                    null,
                    false)
            });

        return new P1RecoveryCommandGateway(
            new WindowsIdentityRbacResolver(directory),
            new NoopKernelExecutor(),
            new NoopEvaluationExecutor(),
            new NoopVoteExecutor(),
            new NoopFinalDecisionExecutor(),
            new RecoveredApiCommandCatalogWave11(),
            portfolio: null,
            execution: execution,
            benefit: benefit);
    }

    private static ExecutionThreadEnvelopeWave10 ExecutionThread()
    {
        var now = DateTimeOffset.Parse("2026-09-16T00:00:00Z");
        return new ExecutionThreadEnvelopeWave10(
            new ExecutionEnvelopeWave10(
                ExecutionId: "EX-1",
                RecommendationId: "REC-1",
                CandidateId: "PC-1",
                IdeaId: "IDEA-1",
                ApprovedIdeaVersion: 7,
                State: "PLANNING",
                Version: 1,
                CharterApproved: false,
                PlanApproved: false,
                ProgressPercent: 0,
                CompletionDossierRef: null,
                CompletionApproved: false,
                CompletionSubmittedByPersonId: null,
                CompletionSubmittedByAssignmentId: null,
                CompletionReviewedByPersonId: null,
                CompletionReviewDecision: null,
                BenefitId: null,
                CreatedAt: now,
                UpdatedAt: now,
                CorrelationId: "CORR-BASE"),
            null);
    }

    private static BenefitEnvelopeWave11 Benefit()
    {
        var now = DateTimeOffset.Parse("2026-09-16T00:00:00Z");
        return new BenefitEnvelopeWave11(
            BenefitId: "BEN-1",
            ExecutionId: "EX-1",
            IdeaId: "IDEA-1",
            ApprovedIdeaVersion: 7,
            State: "OBLIGATION_PENDING_ACCEPTANCE",
            Version: 1,
            BaselineEvidenceRef: null,
            TargetEvidenceRef: null,
            MeasurementPlanRef: null,
            MeasurementDossierRef: null,
            VerificationDossierRef: null,
            AttributionDossierRef: null,
            RealizationDossierRef: null,
            AcceptedByPersonId: null,
            AcceptedByAssignmentId: null,
            MeasuredByPersonId: null,
            VerifiedByPersonId: null,
            KnowledgeId: null,
            CreatedAt: now,
            UpdatedAt: now,
            CorrelationId: "CORR-BASE");
    }

    private static AuthorityResult Success(string correlationId, long? newVersion = 1) =>
        new(200, "OK", true, true, false, newVersion, correlationId, Array.Empty<string>());

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

    private sealed class NoopKernelExecutor : IP1AuthorityKernelExecutor
    {
        public ValueTask<AuthorityResult> ExecuteAsync(AuthorityCommand command, AuthorityActor actor, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Success(command.CorrelationId, command.ExpectedVersion + 1));
    }

    private sealed class NoopEvaluationExecutor : IP1EvaluationCompletionExecutor
    {
        public ValueTask<AuthorityResult> ExecuteAsync(EvaluationCompletionCommand command, AuthorityActor actor, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Success(command.CorrelationId, command.ExpectedIdeaVersion + 1));
    }

    private sealed class NoopVoteExecutor : IP1G04VoteExecutor
    {
        public ValueTask<AuthorityResult> ExecuteAsync(G04VoteCommand command, AuthorityActor actor, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Success(command.CorrelationId, command.ExpectedIdeaVersion));
    }

    private sealed class NoopFinalDecisionExecutor : IP1G04FinalDecisionExecutor
    {
        public ValueTask<AuthorityResult> ExecuteAsync(G04FinalDecisionCommand command, AuthorityActor actor, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Success(command.CorrelationId, command.ExpectedIdeaRevision));
    }
}
