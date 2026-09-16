using EIMS.Authority.Recovery;
using EIMS.Identity.Rbac;
using EIMS.PilotAssembly.Binding;
using EIMS.PilotAssembly.Core;

namespace EIMS.P1P5.Wave9PortfolioBinding.ContractTests;

internal static class Program
{
    private static readonly string[] PortfolioCommands =
    {
        "portfolio.assign-candidate",
        "portfolio.membership-decision",
        "portfolio.generate-execution-recommendation",
        "portfolio.approve-execution-recommendation",
        "portfolio.bind-approved-baseline",
        "portfolio.request-execution-handoff"
    };

    public static async Task<int> Main()
    {
        var tests = new List<(string Name, Func<Task> Run)>
        {
            ("P1P5-W9-01 binding contract is rebaselined to twelve user mutations", ContractRebaseline),
            ("P1P5-W9-02 Wave9 catalog exposes exactly six Portfolio user mutations", CatalogContainsPortfolioCommands),
            ("P1P5-W9-03 assign candidate envelope maps exactly", AssignCandidateMaps),
            ("P1P5-W9-04 membership decision envelope maps exactly", MembershipDecisionMaps),
            ("P1P5-W9-05 recommendation generation envelope maps exactly", GenerateRecommendationMaps),
            ("P1P5-W9-06 recommendation approval envelope maps exactly", ApproveRecommendationMaps),
            ("P1P5-W9-07 approved baseline binding envelope maps exactly", BindBaselineMaps),
            ("P1P5-W9-08 execution handoff envelope maps exactly", RequestHandoffMaps),
            ("P1P5-W9-09 recovered Portfolio command fails closed when executor is not composed", MissingPortfolioExecutorFailsClosed),
            ("P1P5-W9-10 requested scope cannot grant Portfolio authority", RequestedScopeCannotGrant),
            ("P1P5-W9-11 client role field cannot replace P3-resolved Portfolio role", ClientRoleIgnored),
            ("P1P5-W9-12 system-only eligibility is not exposed through user command gateway", EligibilityNotUserCommand),
            ("P1P5-W9-13 legacy grouped portfolio.assign-accept remains fail closed", LegacyGroupedCommandRemainsClosed),
            ("P1P5-W9-14 If-Match expected thread version is mandatory", MissingExpectedVersionRejected),
            ("P1P5-W9-15 candidateId is mandatory for Portfolio user commands", MissingCandidateRejected),
            ("P1P5-W9-16 failed P3 identity resolution invokes no Portfolio executor", FailedIdentityInvokesNoPortfolio),
            ("P1P5-W9-17 assignment selector type errors fail before Portfolio executor", SelectorTypeErrorDenied),
            ("P1P5-W9-18 all six Portfolio commands preserve idempotency and correlation envelope", CommonEnvelopePreserved)
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

    private static Task ContractRebaseline()
    {
        Equal("P1P5-1.1.0", P1P5BindingContract.Version);
        Equal(12, P1P5BindingContract.RecoveredMutationCommandCount);
        Equal(6, P1P5BindingContract.PortfolioUserCommandCount);
        False(P1P5BindingContract.SystemPortfolioEligibilityExposedAsUserCommand);
        True(P1P5BindingContract.RequiresAuthoritativeP3Directory);
        True(P1P5BindingContract.RequiresDurableP2StoreForPilotActivation);
        False(P1P5BindingContract.ClientRoleHeadersTrusted);
        False(P1P5BindingContract.ClientScopeGrantsTrusted);
        return Task.CompletedTask;
    }

    private static Task CatalogContainsPortfolioCommands()
    {
        var catalog = new RecoveredApiCommandCatalogWave9();
        Equal(RecoveredApiCommandCatalogWave9.Wave9RecoveredMutationCommandCount,
            catalog.All.Count(x => x.MutationContractRecovered));

        var recoveredPortfolio = catalog.All
            .Where(x => x.MutationContractRecovered && x.CommandName.StartsWith("portfolio.", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.CommandName)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Sequence(PortfolioCommands.OrderBy(x => x, StringComparer.Ordinal), recoveredPortfolio);
        return Task.CompletedTask;
    }

    private static async Task AssignCandidateMaps()
    {
        var recorder = new RecordingPortfolioExecutor();
        var result = await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.assign-candidate", "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"candidateId\":\"PC-1\",\"portfolioId\":\"PF-7\"}", 4));

        Equal(200, result.HttpStatus);
        Equal(1, recorder.Count);
        var command = recorder.Last!;
        Equal("portfolio.assign-candidate", command.CommandName);
        Equal("PC-1", command.CandidateId);
        Equal(4L, command.ExpectedThreadVersion);
        Equal("PF-7", command.PortfolioId);
        Equal("UNIT:RND", command.RequestedScope);
    }

    private static async Task MembershipDecisionMaps()
    {
        var recorder = new RecordingPortfolioExecutor();
        await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.membership-decision", "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"candidateId\":\"PC-1\",\"membershipDecision\":\"ACCEPTED\"}", 5));

        Equal("ACCEPTED", recorder.Last!.MembershipDecision);
        Equal(5L, recorder.Last.ExpectedThreadVersion);
    }

    private static async Task GenerateRecommendationMaps()
    {
        var recorder = new RecordingPortfolioExecutor();
        await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.generate-execution-recommendation", "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"candidateId\":\"PC-1\",\"recommendationId\":\"REC-1\"}", 6));

        Equal("REC-1", recorder.Last!.RecommendationId);
    }

    private static async Task ApproveRecommendationMaps()
    {
        var recorder = new RecordingPortfolioExecutor();
        await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.approve-execution-recommendation", "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"candidateId\":\"PC-1\",\"recommendationId\":\"REC-1\",\"governanceDecisionRef\":\"GOV-42\"}", 7));

        Equal("REC-1", recorder.Last!.RecommendationId);
        Equal("GOV-42", recorder.Last.GovernanceDecisionRef);
    }

    private static async Task BindBaselineMaps()
    {
        var recorder = new RecordingPortfolioExecutor();
        await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.bind-approved-baseline", "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"candidateId\":\"PC-1\",\"recommendationId\":\"REC-1\",\"approvedBaselineRef\":\"BL-9\"}", 8));

        Equal("REC-1", recorder.Last!.RecommendationId);
        Equal("BL-9", recorder.Last.ApprovedBaselineRef);
    }

    private static async Task RequestHandoffMaps()
    {
        var recorder = new RecordingPortfolioExecutor();
        await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.request-execution-handoff", "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"candidateId\":\"PC-1\",\"recommendationId\":\"REC-1\"}", 9));

        Equal("REC-1", recorder.Last!.RecommendationId);
        Equal(9L, recorder.Last.ExpectedThreadVersion);
    }

    private static async Task MissingPortfolioExecutorFailsClosed()
    {
        var result = await GatewayForRole("PORTFOLIO_MANAGER", portfolio: null).ExecuteAsync(
            Attempt("portfolio.assign-candidate", "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"candidateId\":\"PC-1\",\"portfolioId\":\"PF-1\"}", 1));

        Equal(503, result.HttpStatus);
        Equal("P5_COMMAND_DISPATCH_NOT_BOUND", result.Code);
        False(result.StateMutated);
    }

    private static async Task RequestedScopeCannotGrant()
    {
        var recorder = new RecordingPortfolioExecutor();
        var result = await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.assign-candidate", "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"GLOBAL\",\"candidateId\":\"PC-1\",\"portfolioId\":\"PF-1\"}", 1));

        Equal(403, result.HttpStatus);
        Equal("P3_SCOPE_DENIED", result.Code);
        Equal(0, recorder.Count);
    }

    private static async Task ClientRoleIgnored()
    {
        var recorder = new RecordingPortfolioExecutor();
        var result = await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.assign-candidate", "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"candidateId\":\"PC-1\",\"portfolioId\":\"PF-1\",\"role\":\"ADMIN\"}", 1));

        Equal(200, result.HttpStatus);
        Sequence(new[] { "PORTFOLIO_MANAGER" }, recorder.LastActor!.Roles);
    }

    private static async Task EligibilityNotUserCommand()
    {
        var recorder = new RecordingPortfolioExecutor();
        var result = await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.evaluate-eligibility-from-approved-idea", "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"candidateId\":\"PC-1\"}", 1));

        Equal(404, result.HttpStatus);
        Equal("P5_COMMAND_UNKNOWN", result.Code);
        Equal(0, recorder.Count);
    }

    private static async Task LegacyGroupedCommandRemainsClosed()
    {
        var recorder = new RecordingPortfolioExecutor();
        var result = await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.assign-accept", "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"candidateId\":\"PC-1\"}", 1));

        Equal(503, result.HttpStatus);
        Equal("P5_COMMAND_NOT_RECOVERED", result.Code);
        Equal(0, recorder.Count);
    }

    private static async Task MissingExpectedVersionRejected()
    {
        var recorder = new RecordingPortfolioExecutor();
        var attempt = new CommandAttempt(
            "portfolio.assign-candidate",
            "DOMAIN\\user",
            "CORR",
            null,
            "IDEM",
            "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"candidateId\":\"PC-1\",\"portfolioId\":\"PF-1\"}");

        var result = await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(attempt);
        Equal(400, result.HttpStatus);
        Equal("P5_EXPECTED_VERSION_REQUIRED", result.Code);
        Equal(0, recorder.Count);
    }

    private static async Task MissingCandidateRejected()
    {
        var recorder = new RecordingPortfolioExecutor();
        var result = await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.assign-candidate", "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"portfolioId\":\"PF-1\"}", 1));

        Equal(400, result.HttpStatus);
        Equal("P5_COMMAND_FIELD_REQUIRED", result.Code);
        Equal(0, recorder.Count);
    }

    private static async Task FailedIdentityInvokesNoPortfolio()
    {
        var recorder = new RecordingPortfolioExecutor();
        var directory = new InMemoryIdentityDirectoryStore(
            Array.Empty<PersonDirectoryEntry>(),
            Array.Empty<RoleAssignmentEntry>());

        var result = await GatewayForDirectory(directory, recorder).ExecuteAsync(
            Attempt("portfolio.assign-candidate", "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"candidateId\":\"PC-1\",\"portfolioId\":\"PF-1\"}", 1));

        Equal(403, result.HttpStatus);
        Equal("P3_PERSON_NOT_MAPPED", result.Code);
        Equal(0, recorder.Count);
    }

    private static async Task SelectorTypeErrorDenied()
    {
        var recorder = new RecordingPortfolioExecutor();
        var result = await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.assign-candidate", "{\"assignmentId\":123,\"requestedScope\":\"UNIT:RND\",\"candidateId\":\"PC-1\",\"portfolioId\":\"PF-1\"}", 1));

        Equal(400, result.HttpStatus);
        Equal("P5_COMMAND_FIELD_TYPE_INVALID", result.Code);
        Equal(0, recorder.Count);
    }

    private static async Task CommonEnvelopePreserved()
    {
        foreach (var commandName in PortfolioCommands)
        {
            var recorder = new RecordingPortfolioExecutor();
            var body = "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"candidateId\":\"PC-1\",\"portfolioId\":\"PF-1\",\"membershipDecision\":\"ACCEPTED\",\"recommendationId\":\"REC-1\",\"governanceDecisionRef\":\"GOV-1\",\"approvedBaselineRef\":\"BL-1\"}";
            var result = await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
                new CommandAttempt(commandName, "DOMAIN\\user", "CORR-W9", 12, "IDEM-W9", body));

            Equal(200, result.HttpStatus);
            Equal("IDEM-W9", recorder.Last!.IdempotencyKey);
            Equal("CORR-W9", recorder.Last.CorrelationId);
            Equal(12L, recorder.Last.ExpectedThreadVersion);
        }
    }

    private static P1RecoveryCommandGateway GatewayForRole(string role, IP1PortfolioExecutor? portfolio)
    {
        var directory = new InMemoryIdentityDirectoryStore(
            new[] { new PersonDirectoryEntry("P-1", "DOMAIN\\user", DirectoryPersonStatus.Active) },
            new[]
            {
                new RoleAssignmentEntry(
                    "ASG-1",
                    "P-1",
                    role,
                    new[] { "UNIT:RND" },
                    DateTimeOffset.UtcNow.AddDays(-1),
                    null,
                    false)
            });

        return GatewayForDirectory(directory, portfolio);
    }

    private static P1RecoveryCommandGateway GatewayForDirectory(IIdentityDirectoryStore directory, IP1PortfolioExecutor? portfolio) =>
        new(
            new WindowsIdentityRbacResolver(directory),
            new NoopKernelExecutor(),
            new NoopEvaluationExecutor(),
            new NoopVoteExecutor(),
            new NoopFinalDecisionExecutor(),
            new RecoveredApiCommandCatalogWave9(),
            portfolio);

    private static CommandAttempt Attempt(string command, string body, long? expectedVersion, string? key = "IDEM") =>
        new(command, "DOMAIN\\user", "CORR", expectedVersion, key, body);

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

    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException("Sequences differ.");
    }

    private sealed class RecordingPortfolioExecutor : IP1PortfolioExecutor
    {
        public int Count { get; private set; }
        public PortfolioCommandWave9? Last { get; private set; }
        public AuthorityActor? LastActor { get; private set; }

        public ValueTask<AuthorityResult> ExecuteAsync(
            PortfolioCommandWave9 command,
            AuthorityActor actor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++;
            Last = command;
            LastActor = actor;
            return ValueTask.FromResult(Success(command.CorrelationId, command.ExpectedThreadVersion + 1));
        }
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
