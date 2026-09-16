using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;

namespace EIMS.P1.Wave10.Execution.ContractTests;

internal static class Program
{
    private const string ExecutionId = "EX-1";
    private const string Scope = "UNIT:RND";

    public static async Task<int> Main()
    {
        var tests = new List<(string Name, Func<Task> Run)>
        {
            ("W10-01 catalog recovers nine Execution mutations and keeps prepare closed", CatalogContract),
            ("W10-02 ownership evidence is mandatory", OwnershipEvidenceRequired),
            ("W10-03 exact owner person and assignment are mandatory", ExactOwnerRequired),
            ("W10-04 requested scope cannot widen authority", ScopeCannotWiden),
            ("W10-05 charter approval is independent in PLANNING", CharterApproval),
            ("W10-06 plan baseline approval is independent in PLANNING", PlanApproval),
            ("W10-07 start requires both charter and plan approval", StartRequiresApprovals),
            ("W10-08 start moves PLANNING to ACTIVE", StartMovesActive),
            ("W10-09 progress must be between zero and one hundred", ProgressRange),
            ("W10-10 progress 100 remains ACTIVE", HundredPercentIsNotCompletion),
            ("W10-11 completion submit requires progress 100", SubmitRequiresHundred),
            ("W10-12 completion submit requires dossier evidence", SubmitRequiresDossier),
            ("W10-13 completion submit enters independent review", SubmitEntersReview),
            ("W10-14 owner cannot review own completion", SelfReviewDenied),
            ("W10-15 independent reviewer approve moves to COMPLETED", ReviewApprove),
            ("W10-16 independent reviewer return moves to ACTIVE", ReviewReturn),
            ("W10-17 benefit handoff requires approved completion", BenefitRequiresCompletion),
            ("W10-18 benefit handoff creates exactly one obligation", BenefitHandoffCreatesOne),
            ("W10-19 duplicate benefit handoff is blocked", DuplicateBenefitBlocked),
            ("W10-20 begin closure requires Benefit owner acceptance evidence", ClosureRequiresBenefitAcceptance),
            ("W10-21 accepted Benefit permits closure start", BeginClosure),
            ("W10-22 close requires CLOSURE_IN_PROGRESS", CloseRequiresClosureState),
            ("W10-23 close reaches terminal CLOSED", CloseExecution),
            ("W10-24 exact retry is idempotent before mutable state read", ReplaySafe),
            ("W10-25 changed payload with same key conflicts", ReplayConflict),
            ("W10-26 stale expected version fails closed", StaleVersion),
            ("W10-27 wrong role fails closed", WrongRole),
            ("W10-28 Digital Thread is preserved through Benefit handoff", DigitalThreadPreserved),
            ("W10-29 persistence fault rolls back state audit outbox and idempotency", AtomicRollback),
            ("W10-30 grouped executions.prepare remains unavailable to Wave10 service", PrepareRemainsClosed)
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

    private static Task CatalogContract()
    {
        var catalog = new RecoveredApiCommandCatalogWave10();
        Equal(21, catalog.All.Count(x => x.MutationContractRecovered));
        var executionRecovered = catalog.All
            .Where(x => x.CommandName.StartsWith("executions.", StringComparison.OrdinalIgnoreCase) && x.MutationContractRecovered)
            .Select(x => x.CommandName)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Sequence(new[]
        {
            "executions.approve-charter",
            "executions.approve-plan-baseline",
            "executions.begin-closure",
            "executions.close",
            "executions.completion-review",
            "executions.progress",
            "executions.request-benefit-handoff",
            "executions.start",
            "executions.submit-completion"
        }, executionRecovered);
        True(catalog.TryGet("executions.prepare", out var prepare));
        False(prepare.MutationContractRecovered);
        return Task.CompletedTask;
    }

    private static async Task OwnershipEvidenceRequired()
    {
        var store = Store(Planning());
        var service = Service(store, Array.Empty<ExecutionOwnershipEvidenceWave10>());
        var result = await service.ExecuteAsync(Command("executions.approve-charter", 1), Owner());
        Equal(409, result.HttpStatus);
        Equal("P1_EXECUTION_OWNERSHIP_EVIDENCE_REQUIRED", result.Code);
    }

    private static async Task ExactOwnerRequired()
    {
        var store = Store(Planning());
        var service = Service(store);
        var actor = Owner() with { PersonId = "P-OTHER" };
        var result = await service.ExecuteAsync(Command("executions.approve-charter", 1), actor);
        Equal(403, result.HttpStatus);
        Equal("P1_EXECUTION_OWNER_ASSIGNMENT_MISMATCH", result.Code);
    }

    private static async Task ScopeCannotWiden()
    {
        var store = Store(Planning());
        var service = Service(store);
        var result = await service.ExecuteAsync(Command("executions.approve-charter", 1) with { RequestedScope = "GLOBAL" }, Owner());
        Equal(403, result.HttpStatus);
        Equal("P1_SCOPE_DENIED", result.Code);
    }

    private static async Task CharterApproval()
    {
        var store = Store(Planning());
        var result = await Service(store).ExecuteAsync(Command("executions.approve-charter", 1), Owner());
        Equal(200, result.HttpStatus);
        var after = await store.GetExecutionAsync(ExecutionId);
        True(after!.Execution.CharterApproved);
        False(after.Execution.PlanApproved);
        Equal("PLANNING", after.Execution.State);
    }

    private static async Task PlanApproval()
    {
        var store = Store(Planning());
        var result = await Service(store).ExecuteAsync(Command("executions.approve-plan-baseline", 1), Owner());
        Equal(200, result.HttpStatus);
        var after = await store.GetExecutionAsync(ExecutionId);
        False(after!.Execution.CharterApproved);
        True(after.Execution.PlanApproved);
        Equal("PLANNING", after.Execution.State);
    }

    private static async Task StartRequiresApprovals()
    {
        var store = Store(Planning());
        var result = await Service(store).ExecuteAsync(Command("executions.start", 1), Owner());
        Equal(409, result.HttpStatus);
        Equal("P1_EXECUTION_START_APPROVALS_REQUIRED", result.Code);
    }

    private static async Task StartMovesActive()
    {
        var store = Store(Planning(charter: true, plan: true));
        var result = await Service(store).ExecuteAsync(Command("executions.start", 1), Owner());
        Equal(200, result.HttpStatus);
        Equal("ACTIVE", (await store.GetExecutionAsync(ExecutionId))!.Execution.State);
    }

    private static async Task ProgressRange()
    {
        var store = Store(Active());
        var result = await Service(store).ExecuteAsync(Command("executions.progress", 1) with { ProgressPercent = 101 }, Owner());
        Equal(400, result.HttpStatus);
        Equal("P1_EXECUTION_PROGRESS_INVALID", result.Code);
    }

    private static async Task HundredPercentIsNotCompletion()
    {
        var store = Store(Active());
        var result = await Service(store).ExecuteAsync(Command("executions.progress", 1) with { ProgressPercent = 100 }, Owner());
        Equal(200, result.HttpStatus);
        var after = (await store.GetExecutionAsync(ExecutionId))!.Execution;
        Equal(100, after.ProgressPercent);
        Equal("ACTIVE", after.State);
        False(after.CompletionApproved);
    }

    private static async Task SubmitRequiresHundred()
    {
        var store = Store(Active(progress: 90));
        var result = await Service(store).ExecuteAsync(Command("executions.submit-completion", 1) with { CompletionDossierRef = "DOS-1" }, Owner());
        Equal(409, result.HttpStatus);
        Equal("P1_EXECUTION_PROGRESS_100_REQUIRED", result.Code);
    }

    private static async Task SubmitRequiresDossier()
    {
        var store = Store(Active(progress: 100));
        var result = await Service(store).ExecuteAsync(Command("executions.submit-completion", 1), Owner());
        Equal(400, result.HttpStatus);
        Equal("P1_EXECUTION_COMPLETION_DOSSIER_REQUIRED", result.Code);
    }

    private static async Task SubmitEntersReview()
    {
        var store = Store(Active(progress: 100));
        var result = await Service(store).ExecuteAsync(Command("executions.submit-completion", 1) with { CompletionDossierRef = "DOS-1" }, Owner());
        Equal(200, result.HttpStatus);
        var after = (await store.GetExecutionAsync(ExecutionId))!.Execution;
        Equal("COMPLETION_REVIEW", after.State);
        Equal("DOS-1", after.CompletionDossierRef);
        Equal("P-OWNER", after.CompletionSubmittedByPersonId);
        Equal("ASG-OWNER", after.CompletionSubmittedByAssignmentId);
    }

    private static async Task SelfReviewDenied()
    {
        var store = Store(InReview());
        var service = Service(store);
        var actor = new AuthorityActor("P-OWNER", "DOMAIN\\owner", "WINDOWS", "ASG-REV", new[] { "EXECUTION_COMPLETION_REVIEWER" }, new[] { Scope });
        var result = await service.ExecuteAsync(Command("executions.completion-review", 1) with { CompletionDecision = "APPROVE" }, actor);
        Equal(403, result.HttpStatus);
        Equal("SOD_EXECUTION_SELF_COMPLETION", result.Code);
    }

    private static async Task ReviewApprove()
    {
        var store = Store(InReview());
        var result = await Service(store).ExecuteAsync(Command("executions.completion-review", 1) with { CompletionDecision = "APPROVE" }, Reviewer());
        Equal(200, result.HttpStatus);
        var after = (await store.GetExecutionAsync(ExecutionId))!.Execution;
        Equal("COMPLETED", after.State);
        True(after.CompletionApproved);
        Equal("P-REV", after.CompletionReviewedByPersonId);
    }

    private static async Task ReviewReturn()
    {
        var store = Store(InReview());
        var result = await Service(store).ExecuteAsync(Command("executions.completion-review", 1) with { CompletionDecision = "RETURN", ReviewNote = "Fix evidence" }, Reviewer());
        Equal(200, result.HttpStatus);
        var after = (await store.GetExecutionAsync(ExecutionId))!.Execution;
        Equal("ACTIVE", after.State);
        False(after.CompletionApproved);
        Equal("RETURN", after.CompletionReviewDecision);
    }

    private static async Task BenefitRequiresCompletion()
    {
        var store = Store(Active(progress: 100));
        var result = await Service(store).ExecuteAsync(Command("executions.request-benefit-handoff", 1), Owner());
        Equal(409, result.HttpStatus);
        Equal("P1_EXECUTION_COMPLETION_APPROVAL_REQUIRED", result.Code);
    }

    private static async Task BenefitHandoffCreatesOne()
    {
        var store = Store(Completed());
        var result = await Service(store).ExecuteAsync(Command("executions.request-benefit-handoff", 1), Owner());
        Equal(200, result.HttpStatus);
        var after = await store.GetExecutionAsync(ExecutionId);
        True(after!.BenefitObligation is not null);
        Equal("OBLIGATION_PENDING_ACCEPTANCE", after.BenefitObligation!.State);
        Equal(after.BenefitObligation.BenefitId, after.Execution.BenefitId);
        Sequence(new[] { "BenefitHandoffRequested.v1", "BenefitObligationCreated.v1" }, result.EmittedEvents);
    }

    private static async Task DuplicateBenefitBlocked()
    {
        var seeded = WithBenefit(Completed());
        var store = Store(seeded);
        var result = await Service(store).ExecuteAsync(Command("executions.request-benefit-handoff", 1), Owner());
        Equal(409, result.HttpStatus);
        Equal("P1_EXECUTION_BENEFIT_ALREADY_CREATED", result.Code);
    }

    private static async Task ClosureRequiresBenefitAcceptance()
    {
        var seeded = WithBenefit(Completed());
        var store = Store(seeded);
        var result = await Service(store, benefits: Array.Empty<BenefitAcceptanceEvidenceWave10>())
            .ExecuteAsync(Command("executions.begin-closure", 1), Owner());
        Equal(409, result.HttpStatus);
        Equal("P1_EXECUTION_BENEFIT_ACCEPTANCE_REQUIRED", result.Code);
    }

    private static async Task BeginClosure()
    {
        var seeded = WithBenefit(Completed());
        var store = Store(seeded);
        var benefit = seeded.BenefitObligation!;
        var result = await Service(store, benefits: new[] { AcceptedBenefit(benefit.BenefitId) })
            .ExecuteAsync(Command("executions.begin-closure", 1), Owner());
        Equal(200, result.HttpStatus);
        Equal("CLOSURE_IN_PROGRESS", (await store.GetExecutionAsync(ExecutionId))!.Execution.State);
    }

    private static async Task CloseRequiresClosureState()
    {
        var store = Store(Completed());
        var result = await Service(store).ExecuteAsync(Command("executions.close", 1), Owner());
        Equal(409, result.HttpStatus);
        Equal("P1_EXECUTION_CLOSE_STATE_INVALID", result.Code);
    }

    private static async Task CloseExecution()
    {
        var store = Store(ClosureInProgress());
        var result = await Service(store).ExecuteAsync(Command("executions.close", 1), Owner());
        Equal(200, result.HttpStatus);
        Equal("CLOSED", (await store.GetExecutionAsync(ExecutionId))!.Execution.State);
    }

    private static async Task ReplaySafe()
    {
        var store = Store(Planning());
        var service = Service(store);
        var command = Command("executions.approve-charter", 1, "IDEM-REPLAY");
        var first = await service.ExecuteAsync(command, Owner());
        var replay = await service.ExecuteAsync(command with { CorrelationId = "CORR-RETRY" }, Owner());
        Equal(200, first.HttpStatus);
        Equal(200, replay.HttpStatus);
        True(replay.IdempotentReplay);
        False(replay.StateMutated);
        Equal(1, store.ExecutionAuditLog.Count);
        Equal(1, store.ExecutionOutbox.Count);
        Equal(1, store.ExecutionIdempotencyRecords.Count);
    }

    private static async Task ReplayConflict()
    {
        var store = Store(Active());
        var service = Service(store);
        var first = Command("executions.progress", 1, "IDEM-X") with { ProgressPercent = 20 };
        var changed = first with { ProgressPercent = 30, CorrelationId = "CORR-2" };
        Equal(200, (await service.ExecuteAsync(first, Owner())).HttpStatus);
        var conflict = await service.ExecuteAsync(changed, Owner());
        Equal(409, conflict.HttpStatus);
        Equal("P1_IDEMPOTENCY_CONFLICT", conflict.Code);
    }

    private static async Task StaleVersion()
    {
        var store = Store(Planning());
        var result = await Service(store).ExecuteAsync(Command("executions.approve-charter", 9), Owner());
        Equal(409, result.HttpStatus);
        Equal("P1_EXECUTION_VERSION_CONFLICT", result.Code);
    }

    private static async Task WrongRole()
    {
        var store = Store(Planning());
        var actor = Owner() with { Roles = new[] { "PORTFOLIO_MANAGER" } };
        var result = await Service(store).ExecuteAsync(Command("executions.approve-charter", 1), actor);
        Equal(403, result.HttpStatus);
        Equal("P1_EXECUTION_COMMAND_ROLE_REQUIRED", result.Code);
    }

    private static async Task DigitalThreadPreserved()
    {
        var before = Completed();
        var store = Store(before);
        await Service(store).ExecuteAsync(Command("executions.request-benefit-handoff", 1), Owner());
        var after = await store.GetExecutionAsync(ExecutionId);
        Equal(before.Execution.ExecutionId, after!.Execution.ExecutionId);
        Equal(before.Execution.RecommendationId, after.Execution.RecommendationId);
        Equal(before.Execution.CandidateId, after.Execution.CandidateId);
        Equal(before.Execution.IdeaId, after.Execution.IdeaId);
        Equal(before.Execution.ApprovedIdeaVersion, after.Execution.ApprovedIdeaVersion);
        Equal(before.Execution.IdeaId, after.BenefitObligation!.IdeaId);
        Equal(before.Execution.ApprovedIdeaVersion, after.BenefitObligation.ApprovedIdeaVersion);
    }

    private static async Task AtomicRollback()
    {
        var store = Store(Planning());
        store.FaultPoint = ExecutionWave10PersistenceFaultPoint.BeforeCommitPublish;
        var service = Service(store);
        var threw = false;
        try
        {
            await service.ExecuteAsync(Command("executions.approve-charter", 1), Owner());
        }
        catch (PersistenceAtomicityException)
        {
            threw = true;
        }
        True(threw);
        var after = await store.GetExecutionAsync(ExecutionId);
        False(after!.Execution.CharterApproved);
        Equal(1L, after.Execution.Version);
        Equal(0, store.ExecutionAuditLog.Count);
        Equal(0, store.ExecutionOutbox.Count);
        Equal(0, store.ExecutionIdempotencyRecords.Count);
    }

    private static async Task PrepareRemainsClosed()
    {
        var store = Store(Planning());
        var result = await Service(store).ExecuteAsync(Command("executions.prepare", 1), Owner());
        Equal(503, result.HttpStatus);
        Equal("P1_EXECUTION_COMMAND_NOT_BOUND", result.Code);
    }

    private static ExecutionServiceWave10 Service(
        ExecutionTransactionalStoreWave10 store,
        ExecutionOwnershipEvidenceWave10[]? ownership = null,
        BenefitAcceptanceEvidenceWave10[]? benefits = null) =>
        new(
            store,
            new StaticExecutionOwnershipEvidenceProviderWave10(ownership ?? new[] { Ownership() }),
            new StaticBenefitAcceptanceEvidenceProviderWave10(benefits ?? Array.Empty<BenefitAcceptanceEvidenceWave10>()),
            new RecoveredApiCommandCatalogWave10());

    private static ExecutionTransactionalStoreWave10 Store(ExecutionThreadEnvelopeWave10 thread) => new(thread);

    private static ExecutionOwnershipEvidenceWave10 Ownership() =>
        new(ExecutionId, "P-OWNER", "ASG-OWNER", Scope, "EXEC-OWNER-REGISTRY:EX-1", "1");

    private static BenefitAcceptanceEvidenceWave10 AcceptedBenefit(string benefitId) =>
        new(benefitId, true, "P-BEN", "ASG-BEN", "BENEFIT-ACCEPTANCE:" + benefitId, "1");

    private static AuthorityActor Owner() =>
        new("P-OWNER", "DOMAIN\\owner", "WINDOWS", "ASG-OWNER", new[] { "EXECUTION_OWNER" }, new[] { Scope });

    private static AuthorityActor Reviewer() =>
        new("P-REV", "DOMAIN\\reviewer", "WINDOWS", "ASG-REV", new[] { "EXECUTION_COMPLETION_REVIEWER" }, new[] { Scope });

    private static ExecutionCommandWave10 Command(string name, long version, string key = "IDEM") =>
        new(name, ExecutionId, version, key, "CORR", Scope);

    private static ExecutionThreadEnvelopeWave10 Planning(bool charter = false, bool plan = false) =>
        Thread("PLANNING", charter, plan, 0, false, null, null, null);

    private static ExecutionThreadEnvelopeWave10 Active(int progress = 0) =>
        Thread("ACTIVE", true, true, progress, false, null, null, null);

    private static ExecutionThreadEnvelopeWave10 InReview() =>
        Thread("COMPLETION_REVIEW", true, true, 100, false, "DOS-1", "P-OWNER", "ASG-OWNER");

    private static ExecutionThreadEnvelopeWave10 Completed() =>
        Thread("COMPLETED", true, true, 100, true, "DOS-1", "P-OWNER", "ASG-OWNER") with
        {
            Execution = Thread("COMPLETED", true, true, 100, true, "DOS-1", "P-OWNER", "ASG-OWNER").Execution with
            {
                CompletionReviewedByPersonId = "P-REV",
                CompletionReviewDecision = "APPROVE"
            }
        };

    private static ExecutionThreadEnvelopeWave10 ClosureInProgress() =>
        WithBenefit(Completed()) with
        {
            Execution = WithBenefit(Completed()).Execution with { State = "CLOSURE_IN_PROGRESS" }
        };

    private static ExecutionThreadEnvelopeWave10 WithBenefit(ExecutionThreadEnvelopeWave10 thread)
    {
        var benefit = new BenefitObligationEnvelopeWave10(
            "BEN-1", ExecutionId, thread.Execution.IdeaId, thread.Execution.ApprovedIdeaVersion,
            "OBLIGATION_PENDING_ACCEPTANCE", 1, DateTimeOffset.Parse("2026-09-16T00:00:00Z"), "SEED");
        return thread with
        {
            Execution = thread.Execution with { BenefitId = benefit.BenefitId },
            BenefitObligation = benefit
        };
    }

    private static ExecutionThreadEnvelopeWave10 Thread(
        string state,
        bool charter,
        bool plan,
        int progress,
        bool completionApproved,
        string? dossier,
        string? submittedPerson,
        string? submittedAssignment)
    {
        var now = DateTimeOffset.Parse("2026-09-16T00:00:00Z");
        return new ExecutionThreadEnvelopeWave10(
            new ExecutionEnvelopeWave10(
                ExecutionId,
                "REC-1",
                "PIC-1",
                "IDEA-1",
                7,
                state,
                1,
                charter,
                plan,
                progress,
                dossier,
                completionApproved,
                submittedPerson,
                submittedAssignment,
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

    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException("Sequences differ.");
    }
}
