using EIMS.Authority.Recovery;
using EIMS.Identity.Rbac;
using EIMS.PilotAssembly.Binding;
using EIMS.PilotAssembly.Core;

namespace EIMS.P1P5.Wave14KnowledgeBinding.ContractTests;

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

    private static readonly string[] KnowledgeCommands =
    {
        "knowledge.create-draft",
        "knowledge.validate",
        "knowledge.publish"
    };

    public static async Task<int> Main()
    {
        var tests = new List<(string Name, Func<Task> Run)>
        {
            ("P1P5-W14-01 contract rebaselines to thirty-two recovered mutations", ContractRebaseline),
            ("P1P5-W14-02 Wave13 catalog exposes three Knowledge mutations", CatalogCounts),
            ("P1P5-W14-03 all three Knowledge commands dispatch through Knowledge executor", KnowledgeDispatches),
            ("P1P5-W14-04 Knowledge envelope fields map exactly", KnowledgeFieldsMap),
            ("P1P5-W14-05 create-draft requires benefitId", CreateRequiresBenefitId),
            ("P1P5-W14-06 validate and publish require knowledgeId", ExistingRequiresKnowledgeId),
            ("P1P5-W14-07 missing Knowledge executor fails closed", MissingKnowledgeExecutor),
            ("P1P5-W14-08 client role cannot replace P3 resolved Knowledge role", ClientRoleIgnored),
            ("P1P5-W14-09 requestedScope cannot grant Knowledge authority", RequestedScopeCannotGrant),
            ("P1P5-W14-10 If-Match remains mandatory for Knowledge", ExpectedVersionRequired),
            ("P1P5-W14-11 selector type errors fail before Knowledge dispatch", SelectorTypeError),
            ("P1P5-W14-12 all six Portfolio commands remain dispatchable", PortfolioRegression),
            ("P1P5-W14-13 all nine Execution commands remain dispatchable", ExecutionRegression),
            ("P1P5-W14-14 all eight Benefit commands remain dispatchable", BenefitRegression),
            ("P1P5-W14-15 Knowledge domain events are not user commands", KnowledgeEventsNotUserCommands),
            ("P1P5-W14-16 idempotency correlation and expectedVersion survive Knowledge mapping", CommonEnvelopePreserved)
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
        Equal(3, P1P5BindingContract.KnowledgeUserCommandCount);
        True(P1P5BindingContract.RequiresAuthoritativeKnowledgeSourceEvidence);
        True(P1P5BindingContract.RequiresServerResolvedKnowledgeAuthorPolicy);
        False(P1P5BindingContract.ClientKnowledgeAuthorClaimsTrusted);
        return Task.CompletedTask;
    }

    private static Task CatalogCounts()
    {
        var catalog = new RecoveredApiCommandCatalogWave13();
        Equal(32, catalog.All.Count(x => x.MutationContractRecovered));
        var knowledge = catalog.All
            .Where(x => x.MutationContractRecovered && x.CommandName.StartsWith("knowledge.", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.CommandName)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Sequence(KnowledgeCommands.OrderBy(x => x, StringComparer.Ordinal), knowledge);
        return Task.CompletedTask;
    }

    private static async Task KnowledgeDispatches()
    {
        foreach (var commandName in KnowledgeCommands)
        {
            var recorder = new RecordingKnowledgeExecutor();
            var role = commandName switch
            {
                "knowledge.create-draft" => "DOMAIN_EXPERT",
                "knowledge.validate" => "KNOWLEDGE_STEWARD",
                _ => "KNOWLEDGE_PUBLISHER"
            };
            var result = await GatewayForRole(role, knowledge: recorder).ExecuteAsync(
                Attempt(commandName, KnowledgeBody(), 9));
            Equal(200, result.HttpStatus);
            Equal(1, recorder.Count);
            Equal(commandName, recorder.Last!.CommandName);
        }
    }

    private static async Task KnowledgeFieldsMap()
    {
        var recorder = new RecordingKnowledgeExecutor();
        var result = await GatewayForRole("KNOWLEDGE_PUBLISHER", knowledge: recorder).ExecuteAsync(
            Attempt("knowledge.publish", KnowledgeBody(), 12));
        Equal(200, result.HttpStatus);
        var command = recorder.Last!;
        Equal("KN-1", command.KnowledgeId);
        Equal("BEN-1", command.BenefitId);
        Equal(12L, command.ExpectedVersion);
        Equal("UNIT:RND", command.RequestedScope);
        Equal("PUBLISH", command.Decision);
        Equal("note", command.Note);
        Equal("PUB-DOS", command.PublicationDossierRef);
    }

    private static async Task CreateRequiresBenefitId()
    {
        var recorder = new RecordingKnowledgeExecutor();
        var body = "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\"}";
        var result = await GatewayForRole("DOMAIN_EXPERT", knowledge: recorder).ExecuteAsync(
            Attempt("knowledge.create-draft", body, 9));
        Equal(400, result.HttpStatus);
        Equal("P5_COMMAND_FIELD_REQUIRED", result.Code);
        Equal(0, recorder.Count);
    }

    private static async Task ExistingRequiresKnowledgeId()
    {
        foreach (var commandName in new[] { "knowledge.validate", "knowledge.publish" })
        {
            var recorder = new RecordingKnowledgeExecutor();
            var body = "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"benefitId\":\"BEN-1\"}";
            var role = commandName == "knowledge.validate" ? "KNOWLEDGE_STEWARD" : "KNOWLEDGE_PUBLISHER";
            var result = await GatewayForRole(role, knowledge: recorder).ExecuteAsync(Attempt(commandName, body, 1));
            Equal(400, result.HttpStatus);
            Equal("P5_COMMAND_FIELD_REQUIRED", result.Code);
            Equal(0, recorder.Count);
        }
    }

    private static async Task MissingKnowledgeExecutor()
    {
        var result = await GatewayForRole("DOMAIN_EXPERT").ExecuteAsync(
            Attempt("knowledge.create-draft", KnowledgeBody(), 9));
        Equal(503, result.HttpStatus);
        Equal("P5_COMMAND_DISPATCH_NOT_BOUND", result.Code);
    }

    private static async Task ClientRoleIgnored()
    {
        var recorder = new RecordingKnowledgeExecutor();
        var body = KnowledgeBody().Replace("}", ",\"role\":\"ADMIN\"}", StringComparison.Ordinal);
        var result = await GatewayForRole("DOMAIN_EXPERT", knowledge: recorder).ExecuteAsync(
            Attempt("knowledge.create-draft", body, 9));
        Equal(200, result.HttpStatus);
        Sequence(new[] { "DOMAIN_EXPERT" }, recorder.LastActor!.Roles);
    }

    private static async Task RequestedScopeCannotGrant()
    {
        var recorder = new RecordingKnowledgeExecutor();
        var body = KnowledgeBody().Replace("\"UNIT:RND\"", "\"GLOBAL\"", StringComparison.Ordinal);
        var result = await GatewayForRole("DOMAIN_EXPERT", knowledge: recorder).ExecuteAsync(
            Attempt("knowledge.create-draft", body, 9));
        Equal(403, result.HttpStatus);
        Equal("P3_SCOPE_DENIED", result.Code);
        Equal(0, recorder.Count);
    }

    private static async Task ExpectedVersionRequired()
    {
        var recorder = new RecordingKnowledgeExecutor();
        var result = await GatewayForRole("DOMAIN_EXPERT", knowledge: recorder).ExecuteAsync(
            Attempt("knowledge.create-draft", KnowledgeBody(), null));
        Equal(400, result.HttpStatus);
        Equal("P5_EXPECTED_VERSION_REQUIRED", result.Code);
        Equal(0, recorder.Count);
    }

    private static async Task SelectorTypeError()
    {
        var recorder = new RecordingKnowledgeExecutor();
        var body = KnowledgeBody().Replace("\"assignmentId\":\"ASG-1\"", "\"assignmentId\":123", StringComparison.Ordinal);
        var result = await GatewayForRole("DOMAIN_EXPERT", knowledge: recorder).ExecuteAsync(
            Attempt("knowledge.create-draft", body, 9));
        Equal(400, result.HttpStatus);
        Equal("P5_COMMAND_FIELD_TYPE_INVALID", result.Code);
        Equal(0, recorder.Count);
    }

    private static async Task PortfolioRegression()
    {
        foreach (var commandName in PortfolioCommands)
        {
            var recorder = new RecordingPortfolioExecutor();
            var result = await GatewayForRole("PORTFOLIO_MANAGER", portfolio: recorder).ExecuteAsync(
                Attempt(commandName, PortfolioBody(), 3));
            Equal(200, result.HttpStatus);
            Equal(1, recorder.Count);
        }
    }

    private static async Task ExecutionRegression()
    {
        foreach (var commandName in ExecutionCommands)
        {
            var recorder = new RecordingExecutionExecutor();
            var role = commandName == "executions.completion-review" ? "EXECUTION_COMPLETION_REVIEWER" : "EXECUTION_OWNER";
            var result = await GatewayForRole(role, execution: recorder).ExecuteAsync(
                Attempt(commandName, ExecutionBody(), 4));
            Equal(200, result.HttpStatus);
            Equal(1, recorder.Count);
        }
    }

    private static async Task BenefitRegression()
    {
        foreach (var commandName in BenefitCommands)
        {
            var recorder = new RecordingBenefitExecutor();
            var role = commandName is "benefits.verify" or "benefits.attribution" ? "BENEFIT_VERIFIER" : "BENEFIT_OWNER";
            var result = await GatewayForRole(role, benefit: recorder).ExecuteAsync(
                Attempt(commandName, BenefitBody(), 5));
            Equal(200, result.HttpStatus);
            Equal(1, recorder.Count);
        }
    }

    private static async Task KnowledgeEventsNotUserCommands()
    {
        foreach (var eventName in new[] { "KnowledgeDraftCreated.v1", "KnowledgeValidated.v1", "KnowledgePublished.v1" })
        {
            var recorder = new RecordingKnowledgeExecutor();
            var result = await GatewayForRole("DOMAIN_EXPERT", knowledge: recorder).ExecuteAsync(
                Attempt(eventName, KnowledgeBody(), 1));
            Equal(404, result.HttpStatus);
            Equal("P5_COMMAND_UNKNOWN", result.Code);
            Equal(0, recorder.Count);
        }
    }

    private static async Task CommonEnvelopePreserved()
    {
        foreach (var commandName in KnowledgeCommands)
        {
            var recorder = new RecordingKnowledgeExecutor();
            var role = commandName switch
            {
                "knowledge.create-draft" => "DOMAIN_EXPERT",
                "knowledge.validate" => "KNOWLEDGE_STEWARD",
                _ => "KNOWLEDGE_PUBLISHER"
            };
            var result = await GatewayForRole(role, knowledge: recorder).ExecuteAsync(
                new CommandAttempt(commandName, "DOMAIN\\user", "CORR-W14", 17, "IDEM-W14", KnowledgeBody()));
            Equal(200, result.HttpStatus);
            Equal(17L, recorder.Last!.ExpectedVersion);
            Equal("IDEM-W14", recorder.Last.IdempotencyKey);
            Equal("CORR-W14", recorder.Last.CorrelationId);
        }
    }

    private static P1RecoveryCommandGateway GatewayForRole(
        string role,
        IP1PortfolioExecutor? portfolio = null,
        IP1ExecutionExecutor? execution = null,
        IP1BenefitExecutor? benefit = null,
        IP1KnowledgeExecutor? knowledge = null)
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
            new RecoveredApiCommandCatalogWave13(),
            portfolio,
            execution,
            benefit,
            knowledge);
    }

    private static CommandAttempt Attempt(string command, string body, long? version) =>
        new(command, "DOMAIN\\user", "CORR", version, "IDEM", body);

    private static string KnowledgeBody() =>
        "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"knowledgeId\":\"KN-1\",\"benefitId\":\"BEN-1\",\"decision\":\"PUBLISH\",\"note\":\"note\",\"publicationDossierRef\":\"PUB-DOS\"}";

    private static string PortfolioBody() =>
        "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"candidateId\":\"PC-1\",\"portfolioId\":\"PF-1\",\"membershipDecision\":\"ACCEPTED\",\"recommendationId\":\"REC-1\",\"governanceDecisionRef\":\"GOV-1\",\"approvedBaselineRef\":\"BL-1\"}";

    private static string ExecutionBody() =>
        "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"executionId\":\"EX-1\",\"progressPercent\":100,\"completionDossierRef\":\"COMP-1\",\"completionDecision\":\"APPROVE\",\"reviewNote\":\"note\"}";

    private static string BenefitBody() =>
        "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"benefitId\":\"BEN-1\",\"baselineEvidenceRef\":\"BASE\",\"targetEvidenceRef\":\"TARGET\",\"measurementPlanRef\":\"PLAN\",\"measurementDossierRef\":\"MEASURE\",\"verificationDossierRef\":\"VERIFY\",\"attributionDossierRef\":\"ATTR\",\"realizationDossierRef\":\"REAL\"}";

    private static AuthorityResult Success(string correlationId, long? newVersion) =>
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
        public ValueTask<AuthorityResult> ExecuteAsync(PortfolioCommandWave9 command, AuthorityActor actor, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(Success(command.CorrelationId, command.ExpectedThreadVersion + 1));
        }
    }

    private sealed class RecordingExecutionExecutor : IP1ExecutionExecutor
    {
        public int Count { get; private set; }
        public ValueTask<AuthorityResult> ExecuteAsync(ExecutionCommandWave10 command, AuthorityActor actor, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(Success(command.CorrelationId, command.ExpectedVersion + 1));
        }
    }

    private sealed class RecordingBenefitExecutor : IP1BenefitExecutor
    {
        public int Count { get; private set; }
        public ValueTask<AuthorityResult> ExecuteAsync(BenefitCommandWave11 command, AuthorityActor actor, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(Success(command.CorrelationId, command.ExpectedVersion + 1));
        }
    }

    private sealed class RecordingKnowledgeExecutor : IP1KnowledgeExecutor
    {
        public int Count { get; private set; }
        public KnowledgeCommandWave13? Last { get; private set; }
        public AuthorityActor? LastActor { get; private set; }
        public ValueTask<AuthorityResult> ExecuteAsync(KnowledgeCommandWave13 command, AuthorityActor actor, CancellationToken cancellationToken = default)
        {
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
