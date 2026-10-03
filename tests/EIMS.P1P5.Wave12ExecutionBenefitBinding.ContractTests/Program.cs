using EIMS.Authority.Recovery;
using EIMS.Identity.Rbac;
using EIMS.PilotAssembly.Binding;
using EIMS.PilotAssembly.Core;

namespace EIMS.P1P5.Wave12ExecutionBenefitBinding.ContractTests;

internal static class Program
{
    private static readonly string[] ExecutionCommands =
    {
        "executions.approve-charter",
        "executions.approve-plan-baseline",
        "executions.start",
        "executions.progress",
        "executions.submit-completion",
        "executions.completion-review",
        "executions.request-benefit-handoff",
        "executions.begin-closure",
        "executions.close"
    };

    private static readonly string[] BenefitCommands =
    {
        "benefits.accept",
        "benefits.set-baseline",
        "benefits.approve-measurement-plan",
        "benefits.measure",
        "benefits.verify",
        "benefits.attribution",
        "benefits.realize",
        "benefits.close"
    };

    public static async Task<int> Main()
    {
        var tests = new List<(string Name, Func<Task> Run)>
        {
            ("P1P5-W12-01 contract rebaselines to thirty-two recovered mutations", ContractRebaseline),
            ("P1P5-W12-02 Wave11 catalog exposes nine Execution and eight Benefit mutations", CatalogCounts),
            ("P1P5-W12-03 all nine Execution commands dispatch through Execution executor", ExecutionDispatches),
            ("P1P5-W12-04 Execution envelope maps optional lifecycle fields exactly", ExecutionFieldsMap),
            ("P1P5-W12-05 all eight Benefit commands dispatch through Benefit executor", BenefitDispatches),
            ("P1P5-W12-06 Benefit envelope maps evidence references exactly", BenefitFieldsMap),
            ("P1P5-W12-07 missing Execution executor fails closed", MissingExecutionExecutorFailsClosed),
            ("P1P5-W12-08 missing Benefit executor fails closed", MissingBenefitExecutorFailsClosed),
            ("P1P5-W12-09 executionId is mandatory", MissingExecutionIdRejected),
            ("P1P5-W12-10 benefitId is mandatory", MissingBenefitIdRejected),
            ("P1P5-W12-11 If-Match expected version remains mandatory", MissingExpectedVersionRejected),
            ("P1P5-W12-12 client role field cannot replace P3 resolved role", ClientRoleIgnored),
            ("P1P5-W12-13 requestedScope cannot grant authority", RequestedScopeCannotGrant),
            ("P1P5-W12-14 Execution system intake is not a user command", ExecutionIntakeNotUserCommand),
            ("P1P5-W12-15 Benefit system intake is not a user command", BenefitIntakeNotUserCommand),
            ("P1P5-W12-16 legacy executions.prepare remains fail closed", LegacyExecutionPrepareClosed),
            ("P1P5-W12-17 production Execution executor requires guarded facade", ExecutionExecutorIsGuarded),
            ("P1P5-W12-18 selector type errors fail before domain executor", SelectorTypeErrorDenied),
            ("P1P5-W12-19 idempotency correlation and expectedVersion survive all seventeen mappings", CommonEnvelopePreserved)
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
        Equal("P1P5-1.3.0", P1P5BindingContract.Version);
        Equal(32, P1P5BindingContract.RecoveredMutationCommandCount);
        Equal(6, P1P5BindingContract.PortfolioUserCommandCount);
        Equal(9, P1P5BindingContract.ExecutionUserCommandCount);
        Equal(8, P1P5BindingContract.BenefitUserCommandCount);
        False(P1P5BindingContract.SystemPortfolioEligibilityExposedAsUserCommand);
        False(P1P5BindingContract.SystemExecutionHandoffExposedAsUserCommand);
        False(P1P5BindingContract.SystemBenefitObligationIntakeExposedAsUserCommand);
        True(P1P5BindingContract.RequiresGuardedExecutionService);
        True(P1P5BindingContract.RequiresAuthoritativeP3Directory);
        False(P1P5BindingContract.ClientRoleHeadersTrusted);
        False(P1P5BindingContract.ClientScopeGrantsTrusted);
        False(P1P5BindingContract.ClientOwnershipClaimsTrusted);
        return Task.CompletedTask;
    }

    private static Task CatalogCounts()
    {
        var catalog = new RecoveredApiCommandCatalogWave11();
        Equal(RecoveredApiCommandCatalogWave11.Wave11RecoveredMutationCommandCount,
            catalog.All.Count(x => x.MutationContractRecovered));
        Equal(29, catalog.All.Count(x => x.MutationContractRecovered));

        var execution = catalog.All
            .Where(x => x.MutationContractRecovered && x.CommandName.StartsWith("executions.", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.CommandName)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var benefit = catalog.All
            .Where(x => x.MutationContractRecovered && x.CommandName.StartsWith("benefits.", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.CommandName)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Sequence(ExecutionCommands.OrderBy(x => x, StringComparer.Ordinal), execution);
        Sequence(BenefitCommands.OrderBy(x => x, StringComparer.Ordinal), benefit);
        return Task.CompletedTask;
    }

    private static async Task ExecutionDispatches()
    {
        foreach (var commandName in ExecutionCommands)
        {
            var execution = new RecordingExecutionExecutor();
            var result = await GatewayForRole("EXECUTION_OWNER", execution: execution).ExecuteAsync(
                Attempt(commandName, ExecutionBody(), 11));

            Equal(200, result.HttpStatus);
            Equal(1, execution.Count);
            Equal(commandName, execution.Last!.CommandName);
            Equal("EX-1", execution.Last.ExecutionId);
        }
    }

    private static async Task ExecutionFieldsMap()
    {
        var execution = new RecordingExecutionExecutor();
        await GatewayForRole("EXECUTION_COMPLETION_REVIEWER", execution: execution).ExecuteAsync(
            Attempt("executions.completion-review", ExecutionBody(), 12));

        var command = execution.Last!;
        Equal(12L, command.ExpectedVersion);
        Equal("UNIT:RND", command.RequestedScope);
        Equal(100, command.ProgressPercent);
        Equal("COMP-1", command.CompletionDossierRef);
        Equal("APPROVE", command.CompletionDecision);
        Equal("review-note", command.ReviewNote);
    }

    private static async Task BenefitDispatches()
    {
        foreach (var commandName in BenefitCommands)
        {
            var benefit = new RecordingBenefitExecutor();
            var result = await GatewayForRole("BENEFIT_OWNER", benefit: benefit).ExecuteAsync(
                Attempt(commandName, BenefitBody(), 21));

            Equal(200, result.HttpStatus);
            Equal(1, benefit.Count);
            Equal(commandName, benefit.Last!.CommandName);
            Equal("BEN-1", benefit.Last.BenefitId);
        }
    }

    private static async Task BenefitFieldsMap()
    {
        var benefit = new RecordingBenefitExecutor();
        await GatewayForRole("BENEFIT_OWNER", benefit: benefit).ExecuteAsync(
            Attempt("benefits.set-baseline", BenefitBody(), 22));

        var command = benefit.Last!;
        Equal(22L, command.ExpectedVersion);
        Equal("UNIT:RND", command.RequestedScope);
        Equal("BASE-1", command.BaselineEvidenceRef);
        Equal("TARGET-1", command.TargetEvidenceRef);
        Equal("PLAN-1", command.MeasurementPlanRef);
        Equal("MEASURE-1", command.MeasurementDossierRef);
        Equal("VERIFY-1", command.VerificationDossierRef);
        Equal("ATTR-1", command.AttributionDossierRef);
        Equal("REAL-1", command.RealizationDossierRef);
    }

    private static async Task MissingExecutionExecutorFailsClosed()
    {
        var result = await GatewayForRole("EXECUTION_OWNER").ExecuteAsync(
            Attempt("executions.approve-charter", ExecutionBody(), 1));
        Equal(503, result.HttpStatus);
        Equal("P5_COMMAND_DISPATCH_NOT_BOUND", result.Code);
        False(result.StateMutated);
    }

    private static async Task MissingBenefitExecutorFailsClosed()
    {
        var result = await GatewayForRole("BENEFIT_OWNER").ExecuteAsync(
            Attempt("benefits.accept", BenefitBody(), 1));
        Equal(503, result.HttpStatus);
        Equal("P5_COMMAND_DISPATCH_NOT_BOUND", result.Code);
        False(result.StateMutated);
    }

    private static async Task MissingExecutionIdRejected()
    {
        var execution = new RecordingExecutionExecutor();
        var body = "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\"}";
        var result = await GatewayForRole("EXECUTION_OWNER", execution: execution).ExecuteAsync(
            Attempt("executions.start", body, 1));
        Equal(400, result.HttpStatus);
        Equal("P5_COMMAND_FIELD_REQUIRED", result.Code);
        Equal(0, execution.Count);
    }

    private static async Task MissingBenefitIdRejected()
    {
        var benefit = new RecordingBenefitExecutor();
        var body = "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\"}";
        var result = await GatewayForRole("BENEFIT_OWNER", benefit: benefit).ExecuteAsync(
            Attempt("benefits.accept", body, 1));
        Equal(400, result.HttpStatus);
        Equal("P5_COMMAND_FIELD_REQUIRED", result.Code);
        Equal(0, benefit.Count);
    }

    private static async Task MissingExpectedVersionRejected()
    {
        var execution = new RecordingExecutionExecutor();
        var result = await GatewayForRole("EXECUTION_OWNER", execution: execution).ExecuteAsync(
            Attempt("executions.start", ExecutionBody(), null));
        Equal(400, result.HttpStatus);
        Equal("P5_EXPECTED_VERSION_REQUIRED", result.Code);
        Equal(0, execution.Count);
    }

    private static async Task ClientRoleIgnored()
    {
        var execution = new RecordingExecutionExecutor();
        var body = ExecutionBody().Replace("}", ",\"role\":\"ADMIN\"}", StringComparison.Ordinal);
        var result = await GatewayForRole("EXECUTION_OWNER", execution: execution).ExecuteAsync(
            Attempt("executions.start", body, 2));
        Equal(200, result.HttpStatus);
        Sequence(new[] { "EXECUTION_OWNER" }, execution.LastActor!.Roles);
    }

    private static async Task RequestedScopeCannotGrant()
    {
        var benefit = new RecordingBenefitExecutor();
        var body = BenefitBody().Replace("\"UNIT:RND\"", "\"GLOBAL\"", StringComparison.Ordinal);
        var result = await GatewayForRole("BENEFIT_OWNER", benefit: benefit).ExecuteAsync(
            Attempt("benefits.accept", body, 1));
        Equal(403, result.HttpStatus);
        Equal("P3_SCOPE_DENIED", result.Code);
        Equal(0, benefit.Count);
    }

    private static async Task ExecutionIntakeNotUserCommand()
    {
        var execution = new RecordingExecutionExecutor();
        var result = await GatewayForRole("EXECUTION_OWNER", execution: execution).ExecuteAsync(
            Attempt("ExecutionCreatedFromRecommendation.v1", ExecutionBody(), 1));
        Equal(404, result.HttpStatus);
        Equal("P5_COMMAND_UNKNOWN", result.Code);
        Equal(0, execution.Count);
    }

    private static async Task BenefitIntakeNotUserCommand()
    {
        var benefit = new RecordingBenefitExecutor();
        var result = await GatewayForRole("BENEFIT_OWNER", benefit: benefit).ExecuteAsync(
            Attempt("BenefitObligationCreated.v1", BenefitBody(), 1));
        Equal(404, result.HttpStatus);
        Equal("P5_COMMAND_UNKNOWN", result.Code);
        Equal(0, benefit.Count);
    }

    private static async Task LegacyExecutionPrepareClosed()
    {
        var execution = new RecordingExecutionExecutor();
        var result = await GatewayForRole("EXECUTION_OWNER", execution: execution).ExecuteAsync(
            Attempt("executions.prepare", ExecutionBody(), 1));
        Equal(503, result.HttpStatus);
        Equal("P5_COMMAND_NOT_RECOVERED", result.Code);
        Equal(0, execution.Count);
    }

    private static Task ExecutionExecutorIsGuarded()
    {
        var constructor = typeof(ExecutionExecutor).GetConstructors().Single();
        var parameters = constructor.GetParameters();
        Equal(1, parameters.Length);
        Equal(typeof(ExecutionServiceWave10Guarded), parameters[0].ParameterType);
        return Task.CompletedTask;
    }

    private static async Task SelectorTypeErrorDenied()
    {
        var benefit = new RecordingBenefitExecutor();
        var body = BenefitBody().Replace("\"assignmentId\":\"ASG-1\"", "\"assignmentId\":123", StringComparison.Ordinal);
        var result = await GatewayForRole("BENEFIT_OWNER", benefit: benefit).ExecuteAsync(
            Attempt("benefits.accept", body, 1));
        Equal(400, result.HttpStatus);
        Equal("P5_COMMAND_FIELD_TYPE_INVALID", result.Code);
        Equal(0, benefit.Count);
    }

    private static async Task CommonEnvelopePreserved()
    {
        foreach (var commandName in ExecutionCommands)
        {
            var execution = new RecordingExecutionExecutor();
            var result = await GatewayForRole("EXECUTION_OWNER", execution: execution).ExecuteAsync(
                new CommandAttempt(commandName, "DOMAIN\\user", "CORR-W12", 31, "IDEM-W12", ExecutionBody()));
            Equal(200, result.HttpStatus);
            Equal("IDEM-W12", execution.Last!.IdempotencyKey);
            Equal("CORR-W12", execution.Last.CorrelationId);
            Equal(31L, execution.Last.ExpectedVersion);
        }

        foreach (var commandName in BenefitCommands)
        {
            var benefit = new RecordingBenefitExecutor();
            var result = await GatewayForRole("BENEFIT_OWNER", benefit: benefit).ExecuteAsync(
                new CommandAttempt(commandName, "DOMAIN\\user", "CORR-W12", 41, "IDEM-W12", BenefitBody()));
            Equal(200, result.HttpStatus);
            Equal("IDEM-W12", benefit.Last!.IdempotencyKey);
            Equal("CORR-W12", benefit.Last.CorrelationId);
            Equal(41L, benefit.Last.ExpectedVersion);
        }
    }

    private static P1RecoveryCommandGateway GatewayForRole(
        string role,
        IP1ExecutionExecutor? execution = null,
        IP1BenefitExecutor? benefit = null)
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

    private static CommandAttempt Attempt(string command, string body, long? expectedVersion) =>
        new(command, "DOMAIN\\user", "CORR", expectedVersion, "IDEM", body);

    private static string ExecutionBody() =>
        "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"executionId\":\"EX-1\",\"progressPercent\":100,\"completionDossierRef\":\"COMP-1\",\"completionDecision\":\"APPROVE\",\"reviewNote\":\"review-note\"}";

    private static string BenefitBody() =>
        "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"benefitId\":\"BEN-1\",\"baselineEvidenceRef\":\"BASE-1\",\"targetEvidenceRef\":\"TARGET-1\",\"measurementPlanRef\":\"PLAN-1\",\"measurementDossierRef\":\"MEASURE-1\",\"verificationDossierRef\":\"VERIFY-1\",\"attributionDossierRef\":\"ATTR-1\",\"realizationDossierRef\":\"REAL-1\"}";

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

    private sealed class RecordingExecutionExecutor : IP1ExecutionExecutor
    {
        public int Count { get; private set; }
        public ExecutionCommandWave10? Last { get; private set; }
        public AuthorityActor? LastActor { get; private set; }

        public ValueTask<AuthorityResult> ExecuteAsync(
            ExecutionCommandWave10 command,
            AuthorityActor actor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++;
            Last = command;
            LastActor = actor;
            return ValueTask.FromResult(Success(command.CorrelationId, command.ExpectedVersion + 1));
        }
    }

    private sealed class RecordingBenefitExecutor : IP1BenefitExecutor
    {
        public int Count { get; private set; }
        public BenefitCommandWave11? Last { get; private set; }
        public AuthorityActor? LastActor { get; private set; }

        public ValueTask<AuthorityResult> ExecuteAsync(
            BenefitCommandWave11 command,
            AuthorityActor actor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++;
            Last = command;
            LastActor = actor;
            return ValueTask.FromResult(Success(command.CorrelationId, command.ExpectedVersion + 1));
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
