using EIMS.Authority.Recovery;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("P1R-CT-01 identity required", IdentityRequired),
    ("P1R-CT-02 unknown command denied", UnknownCommandDenied),
    ("P1R-CT-03 recovered product catalog remains fail closed", ProductCatalogFailsClosed),
    ("P1R-CT-04 role denied", RoleDenied),
    ("P1R-CT-05 scope denied", ScopeDenied),
    ("P1R-CT-06 aggregate not found", AggregateNotFound),
    ("P1R-CT-07 optimistic version conflict", VersionConflict),
    ("P1R-CT-08 invalid state denied", InvalidStateDenied),
    ("P1R-CT-09 SoD self completion denied", SodSelfCompletionDenied),
    ("P1R-CT-10 rule failure denied", RuleFailureDenied),
    ("P1R-CT-11 atomic commit writes state audit outbox idempotency", AtomicCommit),
    ("P1R-CT-12 exact idempotent replay does not mutate", IdempotentReplay),
    ("P1R-CT-13 idempotency key payload conflict", IdempotencyConflict),
    ("P1R-CT-14 invalid mutation version denied", InvalidMutationVersion),
    ("P1R-CT-15 audit authority context retained", AuditContextRetained),
    ("P1R-CT-16 catalog recovery gap explicit 21 of 28", CatalogGapExplicit)
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
    new("P-001", "LAB\\user", "TEST_PRINCIPAL", roles, new[] { "UNIT:RND" });

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
    Eq(401, result.HttpStatus); Eq("P1_IDENTITY_REQUIRED", result.Code); False(result.StateMutated);
}

static async Task UnknownCommandDenied()
{
    var result = await Kernel(Store()).ExecuteAsync(Command("unknown"), Actor("TEST_ROLE"));
    Eq(404, result.HttpStatus); Eq("P1_COMMAND_UNKNOWN", result.Code);
}

static async Task ProductCatalogFailsClosed()
{
    var store = Store();
    var kernel = new AuthorityKernel(new RecoveredApiCommandCatalog(), store, new PassRuleEvaluator(), new BaselineSodEvaluator(), new IncrementPlanner());
    var result = await kernel.ExecuteAsync(Command("g04.final-decision"), Actor("IDEA_DECISION"));
    Eq(503, result.HttpStatus); Eq("P1_STATE_CONTRACT_NOT_RECOVERED", result.Code); False(result.StateMutated);
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
    var store = new InMemoryAuthorityStore();
    var result = await Kernel(store).ExecuteAsync(Command(), Actor("TEST_ROLE"));
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
    var actor = new AuthorityActor("P-001", "LAB\\user", "TEST_PRINCIPAL", new[] { "EXECUTION_COMPLETION_REVIEWER", "EXECUTION_OWNER" }, new[] { "UNIT:RND" });
    var result = await Kernel(Store(ownerPerson: "P-001"), policy).ExecuteAsync(Command("executions.completion-review"), actor);
    Eq(403, result.HttpStatus); Eq("SOD_EXECUTION_SELF_COMPLETION", result.Code); False(result.StateMutated);
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
    Eq(200, result.HttpStatus); True(result.Allowed); True(result.StateMutated); Eq(2L, result.NewVersion);
    Eq(2L, (await store.GetAggregateAsync("AGG-1"))!.Version);
    Eq(1, store.Audits.Count); Eq(1, store.Outbox.Count); Eq(1, store.Idempotency.Count);
}

static async Task IdempotentReplay()
{
    var store = Store();
    var kernel = Kernel(store);
    var first = await kernel.ExecuteAsync(Command(), Actor("TEST_ROLE"));
    var second = await kernel.ExecuteAsync(Command(), Actor("TEST_ROLE"));
    True(first.StateMutated); True(second.IdempotentReplay); False(second.StateMutated);
    Eq(2L, (await store.GetAggregateAsync("AGG-1"))!.Version);
    Eq(1, store.Audits.Count); Eq(1, store.Outbox.Count);
}

static async Task IdempotencyConflict()
{
    var store = Store();
    var kernel = Kernel(store);
    await kernel.ExecuteAsync(Command(body: "{\"x\":1}"), Actor("TEST_ROLE"));
    var result = await kernel.ExecuteAsync(Command(expected: 2, body: "{\"x\":2}"), Actor("TEST_ROLE"));
    Eq(409, result.HttpStatus);
    // Version check precedes replay lookup by design, so use a fresh store record to prove key/payload conflict.
    var conflictStore = Store();
    conflictStore.SeedIdempotency(new IdempotencyRecord("test.command", "AGG-1", "KEY-1", "DIFFERENT", AuthorityResult.Deny(409, "OLD", "OLD")));
    var conflict = await Kernel(conflictStore).ExecuteAsync(Command(), Actor("TEST_ROLE"));
    Eq(409, conflict.HttpStatus); Eq("P1_IDEMPOTENCY_CONFLICT", conflict.Code);
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
    Eq("P-001", audit.PersonId); Eq("LAB\\user", audit.NetworkIdentity); Eq("TEST_PRINCIPAL", audit.IdentitySource);
    True(audit.Roles.Contains("TEST_ROLE")); Eq("TEST-RULESET-1.0", audit.RuleSet); Eq("CORR-1", audit.CorrelationId);
}

static Task CatalogGapExplicit()
{
    var catalog = new RecoveredApiCommandCatalog();
    Eq(21, catalog.All.Count); Eq(28, RecoveredApiCommandCatalog.CompletionReviewDeclaredCommandCount); False(catalog.IsCatalogComplete);
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

    public void SeedIdempotency(IdempotencyRecord record) => _idempotency[Key(record.CommandName, record.AggregateId, record.IdempotencyKey)] = record;

    public ValueTask<AggregateSnapshot?> GetAggregateAsync(string aggregateId, CancellationToken cancellationToken = default)
    {
        lock (_sync) return ValueTask.FromResult(_aggregates.TryGetValue(aggregateId, out var a) ? a : null);
    }

    public ValueTask<IdempotencyRecord?> GetIdempotencyAsync(string commandName, string aggregateId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        lock (_sync) return ValueTask.FromResult(_idempotency.TryGetValue(Key(commandName, aggregateId, idempotencyKey), out var r) ? r : null);
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
