using System.Security.Claims;
using EIMS.Authority.Recovery;
using EIMS.HrImport;
using EIMS.Identity.Rbac;
using EIMS.Persistence.Recovery;

var now = DateTimeOffset.Parse("2026-09-14T11:00:00Z");
var tests = new List<(string Name, Func<Task> Run)>
{
    ("PLC-CT-01 approved P4 identity without assignment cannot authorize", P4IdentityWithoutAssignmentDenied),
    ("PLC-CT-02 separate assignment enables P3 authority actor", SeparateAssignmentEnablesActor),
    ("PLC-CT-03 forged client headers do not alter Windows identity", ForgedHeadersDoNotAlterIdentity),
    ("PLC-CT-04 P3 exact role accepted by P1 and committed by P2", EndToEndCommit),
    ("PLC-CT-05 end-to-end audit preserves Person Assignment Role and correlation", EndToEndAuditContext),
    ("PLC-CT-06 exact replay is idempotent across P1 and P2", EndToEndReplay),
    ("PLC-CT-07 wrong requested scope stops before mutation", WrongScopeStopsPipeline),
    ("PLC-CT-08 wrong exact assignment role is denied by P1", WrongRoleStopsPipeline),
    ("PLC-CT-09 inactive P4-derived person stops before mutation", InactivePersonStopsPipeline),
    ("PLC-CT-10 duplicate NetworkAccount stops before mutation", DuplicateIdentityStopsPipeline),
    ("PLC-CT-11 invalid P4 batch cannot enter identity projection", InvalidP4CannotProject),
    ("PLC-CT-12 unresolved real Product command remains fail closed", ProductCommandStillFailClosed),
    ("PLC-CT-13 P2 injected fault rolls back full P1 mutation", P2FaultRollsBackComposition),
    ("PLC-CT-14 P4 data cannot synthesize role assignment", P4CannotSynthesizeAssignment),
    ("PLC-CT-15 different assignment scopes are not unioned", NoCrossAssignmentScopeUnion)
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

string Header() => string.Join(',', P4CanonicalContract.Columns) + "\n";
string Row(string person = "P-001", string employee = "E-001", string account = "DOMAIN\\user1", string status = "ACTIVE") =>
    $"{person},{employee},{account},Synthetic User,RND,Research and Development,,{status},2026-09-14\n";

HrImportValidationResult ValidatedP4(string? csv = null)
{
    var service = new HrOrgImportService();
    return service.ValidateCanonicalCsv(csv ?? Header() + Row(), "PLC-BATCH", extractedAtUtc: now);
}

PersonDirectoryEntry ProjectPerson(HrImportValidationResult validation)
{
    if (!validation.IsValid || validation.Records.Count != 1)
        throw new InvalidOperationException("P4 projection requires one valid approved record in this contract fixture.");
    var r = validation.Records.Single();
    return new PersonDirectoryEntry(
        r.PersonId,
        r.NetworkAccount,
        r.EmploymentStatus == CanonicalEmploymentStatus.ACTIVE ? DirectoryPersonStatus.Active : DirectoryPersonStatus.Inactive);
}

RoleAssignmentEntry Assignment(
    string id = "ASG-IDEA-001",
    string person = "P-001",
    string role = "IDEA_OWNER",
    string[]? scopes = null) =>
    new(id, person, role, scopes ?? new[] { "UNIT:RND" }, now.AddDays(-1), null, false);

WindowsIdentityRbacResolver Resolver(
    IEnumerable<PersonDirectoryEntry> persons,
    IEnumerable<RoleAssignmentEntry> assignments) =>
    new(new InMemoryIdentityDirectoryStore(persons, assignments));

AggregateSnapshot Aggregate() =>
    new("AGG-1", "TEST", "READY", 1, "P-OWNER", "TEST_OWNER", "UNIT:RND");

CommandPolicy TestPolicy() =>
    new("test.command", new[] { "IDEA_OWNER" }, new[] { "READY" }, "TEST-RULESET-1.0", "TestCommitted.v1");

AuthorityCommand TestCommand(string key = "PLC-IDEMP-1", string scope = "UNIT:RND", string corr = "PLC-CORR-1") =>
    new("test.command", "AGG-1", 1, key, corr, "{}", scope);

(AuthorityKernel Kernel, TransactionalAuthorityStore Store) KernelWithP2(CommandPolicy? policy = null)
{
    var store = new TransactionalAuthorityStore(PersistenceContractDescriptor.RecoveryBaseline(), Aggregate());
    var kernel = new AuthorityKernel(
        new SinglePolicyCatalog(policy ?? TestPolicy()),
        store,
        new PassRuleEvaluator(),
        new BaselineSodEvaluator(),
        new IncrementPlanner());
    return (kernel, store);
}

async Task<AuthorityActor> ResolveActor(
    PersonDirectoryEntry person,
    IEnumerable<RoleAssignmentEntry> assignments,
    string assignmentId = "ASG-IDEA-001",
    string scope = "UNIT:RND")
{
    var result = await Resolver(new[] { person }, assignments).ResolveAsync(
        new IdentityResolutionRequest(person.NetworkAccount, assignmentId, scope, now));
    True(result.Allowed);
    return result.Actor!;
}

async Task P4IdentityWithoutAssignmentDenied()
{
    var p4 = ValidatedP4();
    True(p4.IsValid);
    var person = ProjectPerson(p4);
    var result = await Resolver(new[] { person }, Array.Empty<RoleAssignmentEntry>()).ResolveAsync(
        new IdentityResolutionRequest(person.NetworkAccount, "ASG-001", "UNIT:RND", now));
    Eq(403, result.HttpStatus); Eq("P3_ASSIGNMENT_NOT_FOUND", result.Code);
}

async Task SeparateAssignmentEnablesActor()
{
    var person = ProjectPerson(ValidatedP4());
    var actor = await ResolveActor(person, new[] { Assignment() });
    Eq("P-001", actor.PersonId); Eq("ASG-IDEA-001", actor.AssignmentId); True(actor.Roles.SequenceEqual(new[] { "IDEA_OWNER" }));
}

Task ForgedHeadersDoNotAlterIdentity()
{
    var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "DOMAIN\\user1") }, "Negotiate");
    var principal = new ClaimsPrincipal(identity);
    var headers = new Dictionary<string, string>
    {
        ["X-EIMS-Pilot-User"] = "DOMAIN\\admin",
        ["X-Role"] = "ADMIN",
        ["X-Scope"] = "GLOBAL"
    };
    Eq("DOMAIN\\user1", WindowsPrincipalIdentitySource.GetAuthenticatedNetworkName(principal, headers)!);
    return Task.CompletedTask;
}

async Task EndToEndCommit()
{
    var actor = await ResolveActor(ProjectPerson(ValidatedP4()), new[] { Assignment() });
    var (kernel, store) = KernelWithP2();
    var result = await kernel.ExecuteAsync(TestCommand(), actor);
    Eq(200, result.HttpStatus); True(result.StateMutated); Eq(2L, (await store.GetAggregateAsync("AGG-1"))!.Version);
    Eq(1, store.AuditLog.Count); Eq(1, store.Outbox.Count); Eq(1, store.IdempotencyRecords.Count);
}

async Task EndToEndAuditContext()
{
    var actor = await ResolveActor(ProjectPerson(ValidatedP4()), new[] { Assignment() });
    var (kernel, store) = KernelWithP2();
    await kernel.ExecuteAsync(TestCommand(), actor);
    var audit = store.AuditLog.Single();
    Eq("P-001", audit.PersonId); Eq("DOMAIN\\user1", audit.NetworkIdentity); Eq("ASG-IDEA-001", audit.Assignment!);
    True(audit.Roles.SequenceEqual(new[] { "IDEA_OWNER" })); Eq("PLC-CORR-1", audit.CorrelationId); Eq(2L, audit.EntityVersion);
}

async Task EndToEndReplay()
{
    var actor = await ResolveActor(ProjectPerson(ValidatedP4()), new[] { Assignment() });
    var (kernel, store) = KernelWithP2();
    var command = TestCommand();
    var first = await kernel.ExecuteAsync(command, actor);
    var second = await kernel.ExecuteAsync(command, actor);
    True(first.StateMutated); True(second.IdempotentReplay); False(second.StateMutated);
    Eq(1, store.AuditLog.Count); Eq(1, store.Outbox.Count); Eq(1, store.IdempotencyRecords.Count);
}

async Task WrongScopeStopsPipeline()
{
    var person = ProjectPerson(ValidatedP4());
    var p3 = await Resolver(new[] { person }, new[] { Assignment(scopes: new[] { "UNIT:RND" }) }).ResolveAsync(
        new IdentityResolutionRequest(person.NetworkAccount, "ASG-IDEA-001", "UNIT:FIN", now));
    Eq(403, p3.HttpStatus); Eq("P3_SCOPE_DENIED", p3.Code);
    var (_, store) = KernelWithP2();
    await AssertUnchanged(store);
}

async Task WrongRoleStopsPipeline()
{
    var person = ProjectPerson(ValidatedP4());
    var actor = await ResolveActor(person, new[] { Assignment(role: "FINANCIAL_ASSESSOR") });
    var (kernel, store) = KernelWithP2();
    var result = await kernel.ExecuteAsync(TestCommand(), actor);
    Eq(403, result.HttpStatus); Eq("P1_ROLE_SCOPE_DENIED", result.Code); await AssertUnchanged(store);
}

async Task InactivePersonStopsPipeline()
{
    var person = ProjectPerson(ValidatedP4(Header() + Row(status: "INACTIVE")));
    var result = await Resolver(new[] { person }, new[] { Assignment() }).ResolveAsync(
        new IdentityResolutionRequest(person.NetworkAccount, "ASG-IDEA-001", "UNIT:RND", now));
    Eq(403, result.HttpStatus); Eq("P3_PERSON_INACTIVE", result.Code);
    var (_, store) = KernelWithP2(); await AssertUnchanged(store);
}

async Task DuplicateIdentityStopsPipeline()
{
    var person = ProjectPerson(ValidatedP4());
    var duplicate = person with { PersonId = "P-002" };
    var result = await Resolver(new[] { person, duplicate }, new[] { Assignment() }).ResolveAsync(
        new IdentityResolutionRequest(person.NetworkAccount, "ASG-IDEA-001", "UNIT:RND", now));
    Eq(409, result.HttpStatus); Eq("P3_NETWORK_IDENTITY_NOT_UNIQUE", result.Code);
    var (_, store) = KernelWithP2(); await AssertUnchanged(store);
}

Task InvalidP4CannotProject()
{
    var invalid = ValidatedP4(Header() + Row(account: "not-a-domain-account"));
    False(invalid.IsValid);
    var threw = false;
    try { _ = ProjectPerson(invalid); } catch (InvalidOperationException) { threw = true; }
    True(threw);
    return Task.CompletedTask;
}

async Task ProductCommandStillFailClosed()
{
    var actor = await ResolveActor(ProjectPerson(ValidatedP4()), new[] { Assignment(role: "IDEA_DECISION") });
    var store = new TransactionalAuthorityStore(PersistenceContractDescriptor.RecoveryBaseline(), Aggregate());
    var kernel = new AuthorityKernel(
        new RecoveredApiCommandCatalog(), store, new PassRuleEvaluator(), new BaselineSodEvaluator(), new IncrementPlanner());
    var command = new AuthorityCommand("g04.final-decision", "AGG-1", 1, "PROD-1", "PROD-CORR", "{}", "UNIT:RND");
    var result = await kernel.ExecuteAsync(command, actor);
    Eq(503, result.HttpStatus); Eq("P1_STATE_CONTRACT_NOT_RECOVERED", result.Code); await AssertUnchanged(store);
}

async Task P2FaultRollsBackComposition()
{
    var actor = await ResolveActor(ProjectPerson(ValidatedP4()), new[] { Assignment() });
    var (kernel, store) = KernelWithP2();
    store.FaultPoint = PersistenceFaultPoint.AfterOutboxStaged;
    var threw = false;
    try { await kernel.ExecuteAsync(TestCommand(), actor); } catch (PersistenceAtomicityException) { threw = true; }
    True(threw); await AssertUnchanged(store);
}

Task P4CannotSynthesizeAssignment()
{
    var p4 = ValidatedP4();
    True(p4.IsValid);
    var names = typeof(HrOrgRecord).GetProperties().Select(x => x.Name).ToArray();
    False(names.Any(x => x.Contains("Role", StringComparison.OrdinalIgnoreCase)));
    False(names.Any(x => x.Contains("Scope", StringComparison.OrdinalIgnoreCase)));
    False(names.Any(x => x.Contains("Assignment", StringComparison.OrdinalIgnoreCase)));
    return Task.CompletedTask;
}

async Task NoCrossAssignmentScopeUnion()
{
    var person = ProjectPerson(ValidatedP4());
    var assignments = new[]
    {
        Assignment("ASG-RND", role: "IDEA_OWNER", scopes: new[] { "UNIT:RND" }),
        Assignment("ASG-FIN", role: "FINANCIAL_ASSESSOR", scopes: new[] { "UNIT:FIN" })
    };
    var result = await Resolver(new[] { person }, assignments).ResolveAsync(
        new IdentityResolutionRequest(person.NetworkAccount, "ASG-RND", "UNIT:FIN", now));
    Eq(403, result.HttpStatus); Eq("P3_SCOPE_DENIED", result.Code);
}

async Task AssertUnchanged(TransactionalAuthorityStore store)
{
    Eq(1L, (await store.GetAggregateAsync("AGG-1"))!.Version);
    Eq(0, store.AuditLog.Count); Eq(0, store.Outbox.Count); Eq(0, store.IdempotencyRecords.Count);
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
