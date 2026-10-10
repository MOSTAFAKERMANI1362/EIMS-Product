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
            ("P1P5-W9R-01 global binding version advances without changing six Portfolio commands", ContractCompatibility),
            ("P1P5-W9R-02 Wave9 catalog still contains exactly six Portfolio mutations", CatalogCompatibility),
            ("P1P5-W9R-03 all six Portfolio command envelopes still map", PortfolioMappings),
            ("P1P5-W9R-04 missing Portfolio executor remains fail closed", MissingExecutorFailsClosed),
            ("P1P5-W9R-05 requested scope cannot grant Portfolio authority", RequestedScopeCannotGrant),
            ("P1P5-W9R-06 client role is ignored in favor of P3 role", ClientRoleIgnored),
            ("P1P5-W9R-07 system eligibility remains outside user gateway", EligibilityNotUserCommand),
            ("P1P5-W9R-08 legacy grouped assign-accept remains fail closed", LegacyGroupedClosed),
            ("P1P5-W9R-09 If-Match remains mandatory", ExpectedVersionRequired),
            ("P1P5-W9R-10 candidateId remains mandatory", CandidateRequired),
            ("P1P5-W9R-11 failed identity resolution invokes no Portfolio executor", FailedIdentityNoDispatch),
            ("P1P5-W9R-12 selector type errors fail before Portfolio dispatch", SelectorTypeError)
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

    private static Task ContractCompatibility()
    {
        Equal(true, System.Version.Parse(P1P5BindingContract.Version["P1P5-".Length..]) >= new System.Version(1, 2, 0));
        Equal(true, P1P5BindingContract.RecoveredMutationCommandCount >= 29);
        Equal(6, P1P5BindingContract.PortfolioUserCommandCount);
        False(P1P5BindingContract.SystemPortfolioEligibilityExposedAsUserCommand);
        return Task.CompletedTask;
    }

    private static Task CatalogCompatibility()
    {
        var catalog = new RecoveredApiCommandCatalogWave9();
        var recovered = catalog.All
            .Where(x => x.MutationContractRecovered && x.CommandName.StartsWith("portfolio.", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.CommandName)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Sequence(PortfolioCommands.OrderBy(x => x, StringComparer.Ordinal), recovered);
        return Task.CompletedTask;
    }

    private static async Task PortfolioMappings()
    {
        foreach (var commandName in PortfolioCommands)
        {
            var recorder = new RecordingPortfolioExecutor();
            var result = await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
                new CommandAttempt(commandName, "DOMAIN\\user", "CORR-W9", 12, "IDEM-W9", Body()));

            Equal(200, result.HttpStatus);
            Equal(1, recorder.Count);
            Equal(commandName, recorder.Last!.CommandName);
            Equal("PC-1", recorder.Last.CandidateId);
            Equal(12L, recorder.Last.ExpectedThreadVersion);
            Equal("IDEM-W9", recorder.Last.IdempotencyKey);
            Equal("CORR-W9", recorder.Last.CorrelationId);
            Equal("UNIT:RND", recorder.Last.RequestedScope);
        }
    }

    private static async Task MissingExecutorFailsClosed()
    {
        var result = await GatewayForRole("PORTFOLIO_MANAGER", null).ExecuteAsync(
            Attempt("portfolio.assign-candidate", Body(), 1));
        Equal(503, result.HttpStatus);
        Equal("P5_COMMAND_DISPATCH_NOT_BOUND", result.Code);
    }

    private static async Task RequestedScopeCannotGrant()
    {
        var recorder = new RecordingPortfolioExecutor();
        var result = await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.assign-candidate", Body().Replace("\"UNIT:RND\"", "\"GLOBAL\"", StringComparison.Ordinal), 1));
        Equal(403, result.HttpStatus);
        Equal("P3_SCOPE_DENIED", result.Code);
        Equal(0, recorder.Count);
    }

    private static async Task ClientRoleIgnored()
    {
        var recorder = new RecordingPortfolioExecutor();
        var body = Body().Replace("}", ",\"role\":\"ADMIN\"}", StringComparison.Ordinal);
        var result = await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.assign-candidate", body, 1));
        Equal(200, result.HttpStatus);
        Sequence(new[] { "PORTFOLIO_MANAGER" }, recorder.LastActor!.Roles);
    }

    private static async Task EligibilityNotUserCommand()
    {
        var recorder = new RecordingPortfolioExecutor();
        var result = await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.evaluate-eligibility-from-approved-idea", Body(), 1));
        Equal(404, result.HttpStatus);
        Equal("P5_COMMAND_UNKNOWN", result.Code);
        Equal(0, recorder.Count);
    }

    private static async Task LegacyGroupedClosed()
    {
        var recorder = new RecordingPortfolioExecutor();
        var result = await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.assign-accept", Body(), 1));
        Equal(503, result.HttpStatus);
        Equal("P5_COMMAND_NOT_RECOVERED", result.Code);
        Equal(0, recorder.Count);
    }

    private static async Task ExpectedVersionRequired()
    {
        var recorder = new RecordingPortfolioExecutor();
        var result = await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.assign-candidate", Body(), null));
        Equal(400, result.HttpStatus);
        Equal("P5_EXPECTED_VERSION_REQUIRED", result.Code);
        Equal(0, recorder.Count);
    }

    private static async Task CandidateRequired()
    {
        var recorder = new RecordingPortfolioExecutor();
        var body = "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"portfolioId\":\"PF-1\"}";
        var result = await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.assign-candidate", body, 1));
        Equal(400, result.HttpStatus);
        Equal("P5_COMMAND_FIELD_REQUIRED", result.Code);
        Equal(0, recorder.Count);
    }

    private static async Task FailedIdentityNoDispatch()
    {
        var recorder = new RecordingPortfolioExecutor();
        var directory = new InMemoryIdentityDirectoryStore(
            Array.Empty<PersonDirectoryEntry>(),
            Array.Empty<RoleAssignmentEntry>());
        var result = await GatewayForDirectory(directory, recorder).ExecuteAsync(
            Attempt("portfolio.assign-candidate", Body(), 1));
        Equal(403, result.HttpStatus);
        Equal("P3_PERSON_NOT_MAPPED", result.Code);
        Equal(0, recorder.Count);
    }

    private static async Task SelectorTypeError()
    {
        var recorder = new RecordingPortfolioExecutor();
        var body = Body().Replace("\"assignmentId\":\"ASG-1\"", "\"assignmentId\":123", StringComparison.Ordinal);
        var result = await GatewayForRole("PORTFOLIO_MANAGER", recorder).ExecuteAsync(
            Attempt("portfolio.assign-candidate", body, 1));
        Equal(400, result.HttpStatus);
        Equal("P5_COMMAND_FIELD_TYPE_INVALID", result.Code);
        Equal(0, recorder.Count);
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

    private static string Body() =>
        "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"candidateId\":\"PC-1\",\"portfolioId\":\"PF-1\",\"membershipDecision\":\"ACCEPTED\",\"recommendationId\":\"REC-1\",\"governanceDecisionRef\":\"GOV-1\",\"approvedBaselineRef\":\"BL-1\"}";

    private static CommandAttempt Attempt(string command, string body, long? expectedVersion) =>
        new(command, "DOMAIN\\user", "CORR", expectedVersion, "IDEM", body);

    private static AuthorityResult Success(string correlationId, long? newVersion = 1) =>
        new(200, "OK", true, true, false, newVersion, correlationId, Array.Empty<string>());

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

        public ValueTask<AuthorityResult> ExecuteAsync(PortfolioCommandWave9 command, AuthorityActor actor, CancellationToken cancellationToken = default)
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
