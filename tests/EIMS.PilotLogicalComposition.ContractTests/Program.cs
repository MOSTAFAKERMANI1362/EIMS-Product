using System.Security.Claims;
using System.Text.Json;
using EIMS.Authority.Recovery;
using EIMS.HrImport;
using EIMS.Identity.Rbac;
using EIMS.Persistence.Recovery;

var now = DateTimeOffset.Parse("2026-09-14T11:00:00Z");
var tests = new List<(string Name, Func<Task> Run)>
{
    ("PLC2-CT-01 valid P4 owner and reviewer identities project to P3", P4ProjectsOwnerAndReviewer),
    ("PLC2-CT-02 separate assignments resolve exact G03 actors", SeparateAssignmentsResolveActors),
    ("PLC2-CT-03 forged client headers do not alter Windows identity", ForgedHeadersDoNotAlterIdentity),
    ("PLC2-CT-04 real G03 submit flows P4 to P3 to P1 to P2", RealSubmitEndToEnd),
    ("PLC2-CT-05 real G03 approve flows through recovered product path", RealApproveEndToEnd),
    ("PLC2-CT-06 two-step audit preserves distinct authority contexts", TwoStepAuditContexts),
    ("PLC2-CT-07 approve decision preserves reviewer authority context", ApproveDecisionContext),
    ("PLC2-CT-08 real G03 return routes to owner and retains note", RealReturnEndToEnd),
    ("PLC2-CT-09 submit exact replay is idempotent", SubmitReplay),
    ("PLC2-CT-10 approve exact replay is idempotent", ApproveReplay),
    ("PLC2-CT-11 omitted requested scope cannot bypass aggregate scope", OmittedScopeCannotBypassAggregateScope),
    ("PLC2-CT-12 forged requested scope cannot override aggregate scope", ForgedRequestedScopeDenied),
    ("PLC2-CT-13 Need Owner self-review remains denied after P3 resolution", NeedOwnerSelfReviewDenied),
    ("PLC2-CT-14 inactive P4 identity stops before mutation", InactiveIdentityDenied),
    ("PLC2-CT-15 duplicate NetworkAccount stops before mutation", DuplicateIdentityDenied),
    ("PLC2-CT-16 wrong assignment role stops real G03 command", WrongRoleDenied),
    ("PLC2-CT-17 P4 identity data cannot synthesize Role Scope Assignment", P4CannotSynthesizeAssignment),
    ("PLC2-CT-18 P2 decision-stage fault rolls back full product mutation", DecisionStageFaultRollsBack),
    ("PLC2-CT-19 non-G03 Product command remains fail closed", NonG03ProductRemainsClosed),
    ("PLC2-CT-20 assignment scopes are never unioned across assignments", NoCrossAssignmentScopeUnion)
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
string Row(
    string person,
    string employee,
    string account,
    string name,
    string status = "ACTIVE") =>
    $"{person},{employee},{account},{name},RND,Research and Development,,{status},2026-09-14\n";

HrImportValidationResult ValidatedP4(string? csv = null)
{
    var service = new HrOrgImportService();
    var content = csv ?? Header()
        + Row("P-OWNER", "E-OWNER", "DOMAIN\\owner", "Need Owner")
        + Row("P-REVIEW", "E-REVIEW", "DOMAIN\\reviewer", "Need Reviewer");
    return service.ValidateCanonicalCsv(content, "PLC2-BATCH", extractedAtUtc: now);
}

IReadOnlyCollection<PersonDirectoryEntry> ProjectPersons(HrImportValidationResult validation)
{
    if (!validation.IsValid || validation.Records.Count == 0)
        throw new InvalidOperationException("P4 projection requires a valid canonical batch.");

    return Array.AsReadOnly(validation.Records.Select(r => new PersonDirectoryEntry(
        r.PersonId,
        r.NetworkAccount,
        r.EmploymentStatus == CanonicalEmploymentStatus.ACTIVE
            ? DirectoryPersonStatus.Active
            : DirectoryPersonStatus.Inactive)).ToArray());
}

PersonDirectoryEntry Person(IReadOnlyCollection<PersonDirectoryEntry> persons, string personId) =>
    persons.Single(x => string.Equals(x.PersonId, personId, StringComparison.OrdinalIgnoreCase));

RoleAssignmentEntry Assignment(
    string id,
    string person,
    string role,
    string[]? scopes = null) =>
    new(id, person, role, scopes ?? new[] { "UNIT:RND" }, now.AddDays(-1), null, false);

RoleAssignmentEntry OwnerAssignment(string[]? scopes = null) =>
    Assignment("ASG-OWNER", "P-OWNER", "NEED_OWNER", scopes);

RoleAssignmentEntry ReviewerAssignment(string person = "P-REVIEW", string id = "ASG-REVIEW", string[]? scopes = null) =>
    Assignment(id, person, "NEED_REVIEWER", scopes);

WindowsIdentityRbacResolver Resolver(
    IEnumerable<PersonDirectoryEntry> persons,
    IEnumerable<RoleAssignmentEntry> assignments) =>
    new(new InMemoryIdentityDirectoryStore(persons, assignments));

async Task<AuthorityActor> ResolveActor(
    IReadOnlyCollection<PersonDirectoryEntry> persons,
    IEnumerable<RoleAssignmentEntry> assignments,
    string networkAccount,
    string assignmentId,
    string? requestedScope = "UNIT:RND")
{
    var result = await Resolver(persons, assignments).ResolveAsync(
        new IdentityResolutionRequest(networkAccount, assignmentId, requestedScope, now));
    True(result.Allowed);
    return result.Actor!;
}

Dictionary<string, string> GoodNeedFacts() => new(StringComparer.OrdinalIgnoreCase)
{
    ["title"] = "کاهش توقف اضطراری خط نورد",
    ["owner"] = "سرپرست نورد",
    ["current"] = "میانگین توقف اضطراری ماهانه ۱۸ ساعت است.",
    ["desired"] = "توقف اضطراری ماهانه باید به کمتر از ۶ ساعت برسد.",
    ["gap"] = "کاهش حداقل ۱۲ ساعت توقف در ماه لازم است."
};

AggregateSnapshot DraftNeed()
{
    return new AggregateSnapshot(
        "NEED-1",
        "Need",
        "DRAFT",
        1,
        "P-OWNER",
        "NEED_OWNER",
        "UNIT:RND",
        GoodNeedFacts(),
        "NEED_OWNER");
}

AggregateSnapshot PendingNeed()
{
    var facts = GoodNeedFacts();
    facts["g03ReviewStatus"] = "PENDING";
    return new AggregateSnapshot(
        "NEED-1",
        "Need",
        "PENDING_G03_REVIEW",
        2,
        "P-OWNER",
        "NEED_OWNER",
        "UNIT:RND",
        facts,
        "NEED_REVIEWER");
}

AuthorityCommand Submit(string key = "PLC2-SUBMIT", string corr = "PLC2-CORR-SUBMIT", string? scope = "UNIT:RND") =>
    new("needs.submit-g03", "NEED-1", 1, key, corr, "{}", scope);

AuthorityCommand Approve(string key = "PLC2-APPROVE", string corr = "PLC2-CORR-APPROVE", string? scope = "UNIT:RND") =>
    new(
        "needs.g03-decision",
        "NEED-1",
        2,
        key,
        corr,
        "{\"decision\":\"APPROVE\",\"definitionComplete\":\"YES\",\"measurable\":\"YES\",\"solutionBiasFree\":\"YES\"}",
        scope);

AuthorityCommand Return(string note, string key = "PLC2-RETURN", string corr = "PLC2-CORR-RETURN", string? scope = "UNIT:RND") =>
    new(
        "needs.g03-decision",
        "NEED-1",
        2,
        key,
        corr,
        $"{{\"decision\":\"RETURN\",\"note\":{JsonSerializer.Serialize(note)}}}",
        scope);

(AuthorityKernel Kernel, TransactionalAuthorityStore Store) ProductKernel(AggregateSnapshot initial)
{
    var store = new TransactionalAuthorityStore(PersistenceContractDescriptor.RecoveryBaseline(), initial);
    var kernel = new AuthorityKernel(
        new RecoveredApiCommandCatalog(),
        store,
        new RecoveredG03RuleEvaluator(),
        new BaselineSodEvaluator(),
        new RecoveredG03MutationPlanner());
    return (kernel, store);
}

async Task<(IReadOnlyCollection<PersonDirectoryEntry> Persons, AuthorityActor Owner, AuthorityActor Reviewer)> Actors()
{
    var persons = ProjectPersons(ValidatedP4());
    var assignments = new[] { OwnerAssignment(), ReviewerAssignment() };
    var owner = await ResolveActor(persons, assignments, "DOMAIN\\owner", "ASG-OWNER");
    var reviewer = await ResolveActor(persons, assignments, "DOMAIN\\reviewer", "ASG-REVIEW");
    return (persons, owner, reviewer);
}

async Task P4ProjectsOwnerAndReviewer()
{
    var validation = ValidatedP4();
    True(validation.IsValid);
    Eq(2, validation.Records.Count);
    var persons = ProjectPersons(validation);
    Eq(2, persons.Count);
    Eq(DirectoryPersonStatus.Active, Person(persons, "P-OWNER").Status);
    Eq(DirectoryPersonStatus.Active, Person(persons, "P-REVIEW").Status);
}

async Task SeparateAssignmentsResolveActors()
{
    var (_, owner, reviewer) = await Actors();
    Eq("P-OWNER", owner.PersonId); Eq("ASG-OWNER", owner.AssignmentId); Seq(new[] { "NEED_OWNER" }, owner.Roles);
    Eq("P-REVIEW", reviewer.PersonId); Eq("ASG-REVIEW", reviewer.AssignmentId); Seq(new[] { "NEED_REVIEWER" }, reviewer.Roles);
    Seq(new[] { "UNIT:RND" }, owner.Scopes); Seq(new[] { "UNIT:RND" }, reviewer.Scopes);
}

Task ForgedHeadersDoNotAlterIdentity()
{
    var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "DOMAIN\\owner") }, "Negotiate");
    var principal = new ClaimsPrincipal(identity);
    var headers = new Dictionary<string, string>
    {
        ["X-EIMS-Pilot-User"] = "DOMAIN\\admin",
        ["X-Role"] = "ADMIN",
        ["X-Scope"] = "GLOBAL"
    };
    Eq("DOMAIN\\owner", WindowsPrincipalIdentitySource.GetAuthenticatedNetworkName(principal, headers)!);
    return Task.CompletedTask;
}

async Task RealSubmitEndToEnd()
{
    var (_, owner, _) = await Actors();
    var (kernel, store) = ProductKernel(DraftNeed());
    var result = await kernel.ExecuteAsync(Submit(), owner);
    Eq(200, result.HttpStatus); True(result.StateMutated); Seq(new[] { "NeedSubmittedForG03Review.v1" }, result.EmittedEvents);
    var need = (await store.GetAggregateAsync("NEED-1"))!;
    Eq("PENDING_G03_REVIEW", need.State); Eq(2L, need.Version); Eq("PENDING", Fact(need, "g03ReviewStatus")); Eq("NEED_REVIEWER", need.WorkRoutingRole!);
    Eq(0, store.DomainDecisions.Count); Eq(1, store.AuditLog.Count); Eq(1, store.Outbox.Count); Eq(1, store.IdempotencyRecords.Count);
}

async Task RealApproveEndToEnd()
{
    var (_, owner, reviewer) = await Actors();
    var (kernel, store) = ProductKernel(DraftNeed());
    Eq(200, (await kernel.ExecuteAsync(Submit(), owner)).HttpStatus);
    var result = await kernel.ExecuteAsync(Approve(), reviewer);
    Eq(200, result.HttpStatus); True(result.StateMutated); Seq(new[] { "NeedApprovedForIdeation.v1" }, result.EmittedEvents);
    var need = (await store.GetAggregateAsync("NEED-1"))!;
    Eq("READY_FOR_IDEATION", need.State); Eq(3L, need.Version); Eq("APPROVED", Fact(need, "g03ReviewStatus")); Eq("IDEA_OWNER", need.WorkRoutingRole!);
    Eq(1, store.DomainDecisions.Count); Eq(2, store.AuditLog.Count); Eq(2, store.Outbox.Count); Eq(2, store.IdempotencyRecords.Count);
}

async Task TwoStepAuditContexts()
{
    var (_, owner, reviewer) = await Actors();
    var (kernel, store) = ProductKernel(DraftNeed());
    await kernel.ExecuteAsync(Submit(), owner);
    await kernel.ExecuteAsync(Approve(), reviewer);
    var audits = store.AuditLog.ToArray();
    Eq(2, audits.Length);
    Eq("P-OWNER", audits[0].PersonId); Eq("ASG-OWNER", audits[0].Assignment); True(audits[0].Roles.Contains("NEED_OWNER"));
    Eq("P-REVIEW", audits[1].PersonId); Eq("ASG-REVIEW", audits[1].Assignment); True(audits[1].Roles.Contains("NEED_REVIEWER"));
    Eq("PLC2-CORR-SUBMIT", audits[0].CorrelationId); Eq("PLC2-CORR-APPROVE", audits[1].CorrelationId);
}

async Task ApproveDecisionContext()
{
    var (_, owner, reviewer) = await Actors();
    var (kernel, store) = ProductKernel(DraftNeed());
    await kernel.ExecuteAsync(Submit(), owner);
    await kernel.ExecuteAsync(Approve(), reviewer);
    var decision = store.DomainDecisions.Single();
    Eq("G03ReviewDecision", decision.DecisionType); Eq("APPROVE", decision.Outcome);
    Eq("P-REVIEW", decision.PersonId); Eq("ASG-REVIEW", decision.AssignmentId); Eq(3L, decision.EntityVersion);
    Eq("PLC2-CORR-APPROVE", decision.CorrelationId);
    Eq(store.AuditLog.Last().Timestamp, decision.Timestamp);
}

async Task RealReturnEndToEnd()
{
    const string note = "لطفاً خط مبنای توقف و مقدار شکاف را دقیق‌تر ثبت کنید.";
    var (_, owner, reviewer) = await Actors();
    var (kernel, store) = ProductKernel(DraftNeed());
    await kernel.ExecuteAsync(Submit(), owner);
    var result = await kernel.ExecuteAsync(Return(note), reviewer);
    Eq(200, result.HttpStatus); Seq(new[] { "NeedReturnedFromG03Review.v1" }, result.EmittedEvents);
    var need = (await store.GetAggregateAsync("NEED-1"))!;
    Eq("DRAFT", need.State); Eq("RETURNED", Fact(need, "g03ReviewStatus")); Eq("NEED_OWNER", need.WorkRoutingRole!);
    var decision = store.DomainDecisions.Single(); Eq("RETURN", decision.Outcome); Eq(note, decision.Note!);
    False(store.Outbox.Last().EventName.Contains(note, StringComparison.Ordinal));
}

async Task SubmitReplay()
{
    var (_, owner, _) = await Actors();
    var (kernel, store) = ProductKernel(DraftNeed());
    var command = Submit();
    var first = await kernel.ExecuteAsync(command, owner);
    var replay = await kernel.ExecuteAsync(command, owner);
    True(first.StateMutated); True(replay.IdempotentReplay); False(replay.StateMutated);
    Eq(1, store.AuditLog.Count); Eq(1, store.Outbox.Count); Eq(1, store.IdempotencyRecords.Count); Eq(0, store.DomainDecisions.Count);
}

async Task ApproveReplay()
{
    var (_, owner, reviewer) = await Actors();
    var (kernel, store) = ProductKernel(DraftNeed());
    await kernel.ExecuteAsync(Submit(), owner);
    var command = Approve();
    var first = await kernel.ExecuteAsync(command, reviewer);
    var replay = await kernel.ExecuteAsync(command, reviewer);
    True(first.StateMutated); True(replay.IdempotentReplay); False(replay.StateMutated);
    Eq(1, store.DomainDecisions.Count); Eq(2, store.AuditLog.Count); Eq(2, store.Outbox.Count); Eq(2, store.IdempotencyRecords.Count);
}

async Task OmittedScopeCannotBypassAggregateScope()
{
    var persons = ProjectPersons(ValidatedP4());
    var finOwner = OwnerAssignment(new[] { "UNIT:FIN" });
    var actor = await ResolveActor(persons, new[] { finOwner }, "DOMAIN\\owner", "ASG-OWNER", requestedScope: null);
    var (kernel, store) = ProductKernel(DraftNeed());
    var result = await kernel.ExecuteAsync(Submit(scope: null), actor);
    Eq(403, result.HttpStatus); Eq("P1_ROLE_SCOPE_DENIED", result.Code); await AssertPristine(store, 1);
}

async Task ForgedRequestedScopeDenied()
{
    var persons = ProjectPersons(ValidatedP4());
    var broadOwner = OwnerAssignment(new[] { "UNIT:RND", "UNIT:FIN" });
    var actor = await ResolveActor(persons, new[] { broadOwner }, "DOMAIN\\owner", "ASG-OWNER", requestedScope: "UNIT:FIN");
    var (kernel, store) = ProductKernel(DraftNeed());
    var result = await kernel.ExecuteAsync(Submit(scope: "UNIT:FIN"), actor);
    Eq(403, result.HttpStatus); Eq("P1_ROLE_SCOPE_DENIED", result.Code); await AssertPristine(store, 1);
}

async Task NeedOwnerSelfReviewDenied()
{
    var persons = ProjectPersons(ValidatedP4());
    var selfReview = ReviewerAssignment(person: "P-OWNER", id: "ASG-SELF-REVIEW");
    var actor = await ResolveActor(persons, new[] { selfReview }, "DOMAIN\\owner", "ASG-SELF-REVIEW");
    var (kernel, store) = ProductKernel(PendingNeed());
    var result = await kernel.ExecuteAsync(Approve(), actor);
    Eq(403, result.HttpStatus); Eq("SOD_G03_NEED_OWNER_SELF_REVIEW", result.Code); await AssertPristine(store, 2);
}

async Task InactiveIdentityDenied()
{
    var validation = ValidatedP4(Header() + Row("P-OWNER", "E-OWNER", "DOMAIN\\owner", "Need Owner", "INACTIVE"));
    True(validation.IsValid);
    var persons = ProjectPersons(validation);
    var result = await Resolver(persons, new[] { OwnerAssignment() }).ResolveAsync(
        new IdentityResolutionRequest("DOMAIN\\owner", "ASG-OWNER", "UNIT:RND", now));
    Eq(403, result.HttpStatus); Eq("P3_PERSON_INACTIVE", result.Code);
    var (_, store) = ProductKernel(DraftNeed()); await AssertPristine(store, 1);
}

async Task DuplicateIdentityDenied()
{
    var persons = ProjectPersons(ValidatedP4());
    var owner = Person(persons, "P-OWNER");
    var duplicate = owner with { PersonId = "P-DUP" };
    var result = await Resolver(persons.Concat(new[] { duplicate }), new[] { OwnerAssignment() }).ResolveAsync(
        new IdentityResolutionRequest("DOMAIN\\owner", "ASG-OWNER", "UNIT:RND", now));
    Eq(409, result.HttpStatus); Eq("P3_NETWORK_IDENTITY_NOT_UNIQUE", result.Code);
    var (_, store) = ProductKernel(DraftNeed()); await AssertPristine(store, 1);
}

async Task WrongRoleDenied()
{
    var persons = ProjectPersons(ValidatedP4());
    var wrong = Assignment("ASG-WRONG", "P-OWNER", "FINANCIAL_ASSESSOR");
    var actor = await ResolveActor(persons, new[] { wrong }, "DOMAIN\\owner", "ASG-WRONG");
    var (kernel, store) = ProductKernel(DraftNeed());
    var result = await kernel.ExecuteAsync(Submit(), actor);
    Eq(403, result.HttpStatus); Eq("P1_ROLE_SCOPE_DENIED", result.Code); await AssertPristine(store, 1);
}

Task P4CannotSynthesizeAssignment()
{
    var validation = ValidatedP4();
    True(validation.IsValid);
    var names = typeof(HrOrgRecord).GetProperties().Select(x => x.Name).ToArray();
    False(names.Any(x => x.Contains("Role", StringComparison.OrdinalIgnoreCase)));
    False(names.Any(x => x.Contains("Scope", StringComparison.OrdinalIgnoreCase)));
    False(names.Any(x => x.Contains("Assignment", StringComparison.OrdinalIgnoreCase)));
    return Task.CompletedTask;
}

async Task DecisionStageFaultRollsBack()
{
    var (_, _, reviewer) = await Actors();
    var (kernel, store) = ProductKernel(PendingNeed());
    store.FaultPoint = PersistenceFaultPoint.AfterDecisionStaged;
    var threw = false;
    try { await kernel.ExecuteAsync(Approve(), reviewer); }
    catch (PersistenceAtomicityException) { threw = true; }
    True(threw); await AssertPristine(store, 2);
}

async Task NonG03ProductRemainsClosed()
{
    var (_, _, reviewer) = await Actors();
    var (kernel, store) = ProductKernel(PendingNeed());
    var command = new AuthorityCommand("g04.vote", "NEED-1", 2, "PLC2-G04", "PLC2-CORR-G04", "{}", "UNIT:RND");
    var result = await kernel.ExecuteAsync(command, reviewer);
    Eq(503, result.HttpStatus); Eq("P1_STATE_CONTRACT_NOT_RECOVERED", result.Code); await AssertPristine(store, 2);
}

async Task NoCrossAssignmentScopeUnion()
{
    var persons = ProjectPersons(ValidatedP4());
    var assignments = new[]
    {
        OwnerAssignment(new[] { "UNIT:RND" }),
        Assignment("ASG-FIN", "P-OWNER", "FINANCIAL_ASSESSOR", new[] { "UNIT:FIN" })
    };
    var result = await Resolver(persons, assignments).ResolveAsync(
        new IdentityResolutionRequest("DOMAIN\\owner", "ASG-OWNER", "UNIT:FIN", now));
    Eq(403, result.HttpStatus); Eq("P3_SCOPE_DENIED", result.Code);
}

async Task AssertPristine(TransactionalAuthorityStore store, long expectedVersion)
{
    var aggregate = (await store.GetAggregateAsync("NEED-1"))!;
    Eq(expectedVersion, aggregate.Version);
    Eq(0, store.DomainDecisions.Count); Eq(0, store.AuditLog.Count); Eq(0, store.Outbox.Count); Eq(0, store.IdempotencyRecords.Count);
}

static string Fact(AggregateSnapshot aggregate, string key) =>
    aggregate.RuleFacts!.First(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
static void Eq<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
}
static void Seq<T>(IEnumerable<T> expected, IEnumerable<T> actual)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException("Sequences differ.");
}
