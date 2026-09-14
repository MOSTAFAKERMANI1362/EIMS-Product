using EIMS.Authority.Recovery;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("P1R-CT-01 identity and assignment required", IdentityRequired),
    ("P1R-CT-02 unknown command denied", UnknownCommandDenied),
    ("P1R-CT-03 state-bound product command remains fail closed at rule gate", ProductCatalogFailsClosed),
    ("P1R-CT-04 role denied", RoleDenied),
    ("P1R-CT-05 scope denied", ScopeDenied),
    ("P1R-CT-06 aggregate not found", AggregateNotFound),
    ("P1R-CT-07 optimistic version conflict", VersionConflict),
    ("P1R-CT-08 invalid state denied", InvalidStateDenied),
    ("P1R-CT-09 execution self completion SoD denied", SodSelfCompletionDenied),
    ("P1R-CT-10 G04 member final authority SoD denied", SodG04MemberFinalDenied),
    ("P1R-CT-11 knowledge steward publish SoD denied", SodKnowledgePublishDenied),
    ("P1R-CT-12 benefit owner reward decision SoD denied", SodRewardDenied),
    ("P1R-CT-13 rule failure denied", RuleFailureDenied),
    ("P1R-CT-14 atomic commit writes state audit outbox idempotency", AtomicCommit),
    ("P1R-CT-15 exact idempotent replay does not mutate", IdempotentReplay),
    ("P1R-CT-16 idempotency key payload conflict", IdempotencyConflict),
    ("P1R-CT-17 invalid mutation version denied", InvalidMutationVersion),
    ("P1R-CT-18 audit authority context retained", AuditContextRetained),
    ("P1R-CT-19 catalog recovery layers explicit", CatalogGapExplicit),
    ("P1R-CT-20 state-unbound product command remains fail closed at state gate", StateUnboundProductFailsClosed),
    ("P1R-CT-21 G03 mutation contracts are promoted explicitly", G03MutationContractsPromoted),
    ("P1R-CT-22 explicit event contract gate fails closed", EventGateDenied),
    ("P1R-CT-23 explicit mutation contract gate fails closed", MutationGateDenied),
    ("P1R-CT-24 G03 Need Owner self review SoD denied", SodG03SelfReviewDenied),
    ("P1R-CT-25 G03 submit static event resolves exactly", G03StaticEventBindingResolves),
    ("P1R-CT-26 G03 decision outcome events resolve exactly", G03OutcomeEventBindingResolves),
    ("P1R-CT-27 G03 decision has no missing or unknown outcome fallback", G03OutcomeEventBindingHasNoFallback)
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

static AuthorityActor Actor(params string[] roles) =>
    new("P-001", "LAB\\user", "TEST_PRINCIPAL", "ASG-001", roles, new[] { "UNIT:RND" });

static AuthorityCommand Command(string name = "test.command", long expected = 1, string key = "KEY-1", string body = "{}", string? scope = null) =>
    new(name, "AGG-1", expected, key, "CORR-1", body, scope);

static CommandPolicy TestPolicy(string command = "test.command", string role = "TEST_ROLE", string[]? states = null) =>
    new(command, new[] { role }, states ?? new[] { "READY" }, "TEST-RULESET-1.0", "TestEvent.v1");

static AuthorityKernel Kernel(
    InMemoryAuthorityStore store,
    CommandPolicy? policy = null,
    IRuleEvaluator? rules = null,
    ISodEvaluator? sod = null,
    ICommandMutationPlanner? planner = null)
{
    var catalog = new SinglePolicyCatalog(policy ?? TestPolicy());
    return new AuthorityKernel(catalog, store, rules ?? new PassRuleEvaluator(), sod ?? new BaselineSodEvaluator(), planner ?? new IncrementPlanner());
}

static InMemoryAuthorityStore Store(string state = "READY", long version = 1, string? ownerPerson = null) =>
    new(new AggregateSnapshot("AGG-1", "TEST", state, version, ownerPerson, "EXECUTION_OWNER", "UNIT:RND"));

static async Task IdentityRequired()
{
    var result = await Kernel(Store()).ExecuteAsync(Command(), null);
    Eq(401, result.HttpStatus); Eq("P1_IDENTITY_ASSIGNMENT_REQUIRED", result.Code); False(result.StateMutated);
}

static async Task UnknownCommandDenied()
{
    var result = await Kernel(Store()).ExecuteAsync(Command("unknown"), Actor("TEST_ROLE"));
    Eq(404, result.HttpStatus); Eq("P1_COMMAND_UNKNOWN", result.Code);
}

static async Task ProductCatalogFailsClosed()
{
    var kernel = new AuthorityKernel(new RecoveredApiCommandCatalog(), Store(), new PassRuleEvaluator(), new BaselineSodEvaluator(), new IncrementPlanner());
    var result = await kernel.ExecuteAsync(Command("g04.final-decision"), Actor("IDEA_DECISION"));
    Eq(503, result.HttpStatus); Eq("P1_RULE_CONTRACT_NOT_RECOVERED", result.Code); False(result.StateMutated);
}

static async Task RoleDenied()
{
    var result = await Kernel(Store()).ExecuteAsync(Command(), Actor("OTHER_ROLE"));
    Eq(403, result.HttpStatus); Eq("P1_ROLE_SCOPE_DENIED", result.Code);
}

static async Task ScopeDenied()
{
    var result = await Kernel(Store()).ExecuteAsync(Command(scope: "UNIT:FIN"), Actor("TEST_ROLE"));
    Eq(403, result.HttpStatus); Eq("P1_ROLE_SCOPE_DENIED", result.Code);
}

static async Task AggregateNotFound()
{
    var result = await Kernel(new InMemoryAuthorityStore()).ExecuteAsync(Command(), Actor("TEST_ROLE"));
    Eq(404, result.HttpStatus); Eq("P1_AGGREGATE_NOT_FOUND", result.Code);
}

static async Task VersionConflict()
{
    var result = await Kernel(Store()).ExecuteAsync(Command(expected: 7), Actor("TEST_ROLE"));
    Eq(409, result.HttpStatus); Eq("P1_VERSION_CONFLICT", result.Code);
}

static async Task InvalidStateDenied()
{
    var result = await Kernel(Store("DRAFT")).ExecuteAsync(Command(), Actor("TEST_ROLE"));
    Eq(409, result.HttpStatus); Eq("P1_STATE_TRANSITION_DENIED", result.Code);
}

static async Task SodSelfCompletionDenied()
{
    var policy = TestPolicy("executions.completion-review", "EXECUTION_COMPLETION_REVIEWER");
    var actor = new AuthorityActor("P-001", "LAB\\user", "TEST_PRINCIPAL", "ASG-001", new[] { "EXECUTION_COMPLETION_REVIEWER", "EXECUTION_OWNER" }, new[] { "UNIT:RND" });
    var result = await Kernel(Store(ownerPerson: "P-001"), policy).ExecuteAsync(Command("executions.completion-review"), actor);
    Eq(403, result.HttpStatus); Eq("SOD_EXECUTION_SELF_COMPLETION", result.Code); False(result.StateMutated);
}

static async Task SodG04MemberFinalDenied()
{
    var policy = TestPolicy("g04.final-decision", "IDEA_DECISION");
    var actor = new AuthorityActor("P-001", "LAB\\user", "TEST_PRINCIPAL", "ASG-001", new[] { "IDEA_DECISION", "G04_COMMITTEE_MEMBER" }, new[] { "UNIT:RND" });
    var result = await Kernel(Store(), policy).ExecuteAsync(Command("g04.final-decision"), actor);
    Eq(403, result.HttpStatus); Eq("SOD_G04_MEMBER_NOT_FINAL_AUTHORITY", result.Code);
}

static async Task SodKnowledgePublishDenied()
{
    var policy = TestPolicy("knowledge.publish", "KNOWLEDGE_PUBLISHER");
    var actor = new AuthorityActor("P-001", "LAB\\user", "TEST_PRINCIPAL", "ASG-001", new[] { "KNOWLEDGE_PUBLISHER", "KNOWLEDGE_STEWARD" }, new[] { "UNIT:RND" });
    var result = await Kernel(Store(), policy).ExecuteAsync(Command("knowledge.publish"), actor);
    Eq(403, result.HttpStatus); Eq("SOD_KNOWLEDGE_STEWARD_NOT_PUBLISHER", result.Code);
}

static async Task SodRewardDenied()
{
    var policy = TestPolicy("rewards.decide", "REWARD_COMMITTEE");
    var actor = new AuthorityActor("P-001", "LAB\\user", "TEST_PRINCIPAL", "ASG-001", new[] { "REWARD_COMMITTEE", "BENEFIT_OWNER" }, new[] { "UNIT:RND" });
    var result = await Kernel(Store(), policy).ExecuteAsync(Command("rewards.decide"), actor);
    Eq(403, result.HttpStatus); Eq("SOD_BENEFIT_OWNER_NOT_REWARD_COMMITTEE", result.Code);
}

static async Task RuleFailureDenied()
{
    var result = await Kernel(Store(), rules: new FailRuleEvaluator()).ExecuteAsync(Command(), Actor("TEST_ROLE"));
    Eq(422, result.HttpStatus); Eq("RULE_BLOCKED", result.Code); False(result.StateMutated);
}

static async Task AtomicCommit()
{
    var store = Store();
    var result = await Kernel(store).ExecuteAsync(Command(), Actor("TEST_ROLE"));
    Eq(200, result.HttpStatus); True(result.Allowed); True(result.StateMutated); Eq(2L, result.NewVersion ?? -1L);
    Eq(2L, (await store.GetAggregateAsync("AGG-1"))!.Version);
    Eq(1, store.Audits.Count); Eq(1, store.Outbox.Count); Eq(1, store.Idempotency.Count);
}

static async Task IdempotentReplay()
{
    var store = Store();
    var kernel = Kernel(store);
    var first = await kernel.ExecuteAsync(Command(), Actor("TEST_ROLE"));
    var second = await kernel.ExecuteAsync(Command(), Actor("TEST_ROLE"));
    True(first.StateMutated); True(second.IdempotentReplay); False(second.StateMutated); Eq(2L, second.NewVersion ?? -1L);
    Eq(2L, (await store.GetAggregateAsync("AGG-1"))!.Version);
    Eq(1, store.Audits.Count); Eq(1, store.Outbox.Count); Eq(1, store.Idempotency.Count);
}

static async Task IdempotencyConflict()
{
    var store = Store();
    var kernel = Kernel(store);
    await kernel.ExecuteAsync(Command(body: "{\"x\":1}"), Actor("TEST_ROLE"));
    var result = await kernel.ExecuteAsync(Command(expected: 2, body: "{\"x\":2}"), Actor("TEST_ROLE"));
    Eq(409, result.HttpStatus); Eq("P1_IDEMPOTENCY_CONFLICT", result.Code); False(result.StateMutated);
}

static async Task InvalidMutationVersion()
{
    var result = await Kernel(Store(), planner: new BadVersionPlanner()).ExecuteAsync(Command(), Actor("TEST_ROLE"));
    Eq(500, result.HttpStatus); Eq("P1_MUTATION_VERSION_INVALID", result.Code); False(result.StateMutated);
}

static async Task AuditContextRetained()
{
    var store = Store();
    await Kernel(store).ExecuteAsync(Command(), Actor("TEST_ROLE"));
    var audit = store.Audits.Single();
    Eq("P-001", audit.PersonId); Eq("LAB\\user", audit.NetworkIdentity); Eq("TEST_PRINCIPAL", audit.IdentitySource); Eq("ASG-001", audit.Assignment);
    True(audit.Roles.Contains("TEST_ROLE")); Eq("TEST-RULESET-1.0", audit.RuleSet); Eq("CORR-1", audit.CorrelationId); Eq(2L, audit.EntityVersion);
}

static Task CatalogGapExplicit()
{
    var catalog = new RecoveredApiCommandCatalog();
    Eq(21, catalog.All.Count); Eq(28, RecoveredApiCommandCatalog.CompletionReviewDeclaredCommandCount); False(catalog.IsCatalogComplete);
    Eq(6, RecoveredApiCommandCatalog.Wave1StateBoundCommandCount);
    Eq(2, RecoveredApiCommandCatalog.Wave2RuleBoundCommandCount);
    Eq(2, RecoveredApiCommandCatalog.Wave3EventBoundCommandCount);
    Eq(2, RecoveredApiCommandCatalog.Wave4MutationBoundCommandCount);
    Eq(6, catalog.All.Count(x => x.StateContractRecovered));
    Eq(2, catalog.All.Count(x => x.RuleContractRecovered));
    Eq(2, catalog.All.Count(x => x.EventContractRecovered));
    Eq(2, catalog.All.Count(x => x.MutationContractRecovered));
    True(catalog.All.Where(x => x.EventContractRecovered).All(x => x.EventBinding is not null && x.EventBinding.IsValid));
    True(catalog.All.Where(x => x.MutationContractRecovered).Select(x => x.CommandName).OrderBy(x => x).SequenceEqual(new[] { "needs.g03-decision", "needs.submit-g03" }));
    return Task.CompletedTask;
}

static async Task StateUnboundProductFailsClosed()
{
    var kernel = new AuthorityKernel(new RecoveredApiCommandCatalog(), Store(), new PassRuleEvaluator(), new BaselineSodEvaluator(), new IncrementPlanner());
    var result = await kernel.ExecuteAsync(Command("g04.vote"), Actor("G04_COMMITTEE_MEMBER"));
    Eq(503, result.HttpStatus); Eq("P1_STATE_CONTRACT_NOT_RECOVERED", result.Code); False(result.StateMutated);
}

static Task G03MutationContractsPromoted()
{
    var catalog = new RecoveredApiCommandCatalog();
    True(catalog.TryGet("needs.submit-g03", out var submit));
    True(catalog.TryGet("needs.g03-decision", out var decision));
    True(submit.StateContractRecovered && submit.RuleContractRecovered && submit.EventContractRecovered && submit.MutationContractRecovered);
    True(decision.StateContractRecovered && decision.RuleContractRecovered && decision.EventContractRecovered && decision.MutationContractRecovered);
    return Task.CompletedTask;
}

static async Task EventGateDenied()
{
    var policy = TestPolicy() with { EventContractRecovered = false, MutationContractRecovered = false };
    var result = await Kernel(Store(), policy).ExecuteAsync(Command(), Actor("TEST_ROLE"));
    Eq(503, result.HttpStatus); Eq("P1_EVENT_CONTRACT_NOT_RECOVERED", result.Code); False(result.StateMutated);
}

static async Task MutationGateDenied()
{
    var policy = TestPolicy() with { EventContractRecovered = true, MutationContractRecovered = false };
    var result = await Kernel(Store(), policy).ExecuteAsync(Command(), Actor("TEST_ROLE"));
    Eq(503, result.HttpStatus); Eq("P1_MUTATION_CONTRACT_NOT_RECOVERED", result.Code); False(result.StateMutated);
}

static async Task SodG03SelfReviewDenied()
{
    var policy = TestPolicy("needs.g03-decision", "NEED_REVIEWER", new[] { "PENDING_G03_REVIEW" });
    var store = Store("PENDING_G03_REVIEW", ownerPerson: "P-001");
    var result = await Kernel(store, policy).ExecuteAsync(Command("needs.g03-decision"), Actor("NEED_REVIEWER"));
    Eq(403, result.HttpStatus); Eq("SOD_G03_NEED_OWNER_SELF_REVIEW", result.Code); False(result.StateMutated);
}

static Task G03StaticEventBindingResolves()
{
    var catalog = new RecoveredApiCommandCatalog();
    True(catalog.TryGet("needs.submit-g03", out var policy));
    True(policy.EventContractRecovered);
    True(policy.EventBinding is not null && policy.EventBinding.IsValid);
    Eq("STATIC", policy.EventBinding!.Kind);
    Eq("NeedSubmittedForG03Review.v1", policy.ResolveEventName()!);
    return Task.CompletedTask;
}

static Task G03OutcomeEventBindingResolves()
{
    var catalog = new RecoveredApiCommandCatalog();
    True(catalog.TryGet("needs.g03-decision", out var policy));
    True(policy.EventContractRecovered);
    True(policy.EventBinding is not null && policy.EventBinding.IsValid);
    Eq("OUTCOME", policy.EventBinding!.Kind);
    Eq("NeedApprovedForIdeation.v1", policy.ResolveEventName("APPROVE")!);
    Eq("NeedReturnedFromG03Review.v1", policy.ResolveEventName("return")!);
    return Task.CompletedTask;
}

static Task G03OutcomeEventBindingHasNoFallback()
{
    var catalog = new RecoveredApiCommandCatalog();
    True(catalog.TryGet("needs.g03-decision", out var policy));
    True(policy.ResolveEventName() is null);
    True(policy.ResolveEventName("HOLD") is null);
    True(policy.ResolveEventName("  ") is null);
    return Task.CompletedTask;
}

static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
static void Eq<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
}

sealed class SinglePolicyCatalog(CommandPolicy policy) : ICommandPolicyCatalog
{
    public IReadOnlyCollection<CommandPolicy> All { get; } = new[] { policy };
    public bool TryGet(string commandName, out CommandPolicy found)
    {
        if (string.Equals(commandName, policy.CommandName, StringComparison.OrdinalIgnoreCase)) { found = policy; return true; }
        found = null!; return false;
    }
}

sealed class IncrementPlanner : ICommandMutationPlanner
{
    public ValueTask<MutationPlan?> PlanAsync(AuthorityCommand command, AuthorityActor actor, AggregateSnapshot aggregate, CommandPolicy policy, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<MutationPlan?>(new MutationPlan(aggregate with { Version = aggregate.Version + 1 }, policy.EventName));
}

sealed class BadVersionPlanner : ICommandMutationPlanner
{
    public ValueTask<MutationPlan?> PlanAsync(AuthorityCommand command, AuthorityActor actor, AggregateSnapshot aggregate, CommandPolicy policy, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<MutationPlan?>(new MutationPlan(aggregate with { Version = aggregate.Version + 2 }, policy.EventName));
}

sealed class FailRuleEvaluator : IRuleEvaluator
{
    public ValueTask<RuleEvaluation> EvaluateAsync(AuthorityCommand command, AuthorityActor actor, AggregateSnapshot aggregate, CommandPolicy policy, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(RuleEvaluation.Fail("RULE_BLOCKED"));
}

sealed class InMemoryAuthorityStore : IAuthorityStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, AggregateSnapshot> _aggregates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IdempotencyRecord> _idempotency = new(StringComparer.Ordinal);
    public List<AuditEnvelope> Audits { get; } = new();
    public List<OutboxEnvelope> Outbox { get; } = new();
    public IReadOnlyCollection<IdempotencyRecord> Idempotency => _idempotency.Values;

    public InMemoryAuthorityStore(params AggregateSnapshot[] aggregates)
    {
        foreach (var a in aggregates) _aggregates[a.AggregateId] = a;
    }

    public ValueTask<AggregateSnapshot?> GetAggregateAsync(string aggregateId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            AggregateSnapshot? value = _aggregates.TryGetValue(aggregateId, out var a) ? a : null;
            return ValueTask.FromResult(value);
        }
    }

    public ValueTask<IdempotencyRecord?> GetIdempotencyAsync(string commandName, string aggregateId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            IdempotencyRecord? value = _idempotency.TryGetValue(Key(commandName, aggregateId, idempotencyKey), out var r) ? r : null;
            return ValueTask.FromResult(value);
        }
    }

    public ValueTask<AuthorityResult> CommitAsync(MutationRequest request, MutationCommit commit, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_aggregates.TryGetValue(request.Before.AggregateId, out var current) || current.Version != request.Before.Version)
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P1_VERSION_CONFLICT", request.Command.CorrelationId));

            var result = new AuthorityResult(200, "P1_COMMITTED", true, true, false, commit.After.Version,
                request.Command.CorrelationId, new[] { commit.Outbox.EventName });
            _aggregates[commit.After.AggregateId] = commit.After;
            Audits.Add(commit.Audit);
            Outbox.Add(commit.Outbox);
            _idempotency[Key(request.Command.CommandName, request.Command.AggregateId, request.Command.IdempotencyKey)] =
                new IdempotencyRecord(request.Command.CommandName, request.Command.AggregateId, request.Command.IdempotencyKey, request.IdempotencyFingerprint, result);
            return ValueTask.FromResult(result);
        }
    }

    private static string Key(string command, string aggregate, string key) => $"{command}|{aggregate}|{key}";
}
