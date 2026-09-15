using EIMS.Authority.Recovery;
using EIMS.Identity.Rbac;
using EIMS.Persistence.Recovery;
using EIMS.PilotAssembly.Binding;
using EIMS.PilotAssembly.Core;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("P1P5-CT-01 Wave8 exposes exactly six mutation-bound commands", CatalogHasSixBoundMutations),
    ("P1P5-CT-02 real G03 submit flows P5 to P3 to P1 to P2", RealG03SubmitThroughGateway),
    ("P1P5-CT-03 assignment/person mismatch fails before P1", AssignmentMismatchDenied),
    ("P1P5-CT-04 requested scope cannot grant authority", RequestedScopeCannotGrant),
    ("P1P5-CT-05 unrecovered Product command remains fail closed", UnrecoveredCommandClosed),
    ("P1P5-CT-06 unknown command is rejected", UnknownCommandRejected),
    ("P1P5-CT-07 idempotency key is mandatory", MissingIdempotencyRejected),
    ("P1P5-CT-08 malformed JSON is rejected", MalformedJsonRejected),
    ("P1P5-CT-09 If-Match expected version is mandatory for kernel mutation", MissingExpectedVersionRejected),
    ("P1P5-CT-10 evaluator completion envelope is mapped exactly", EvaluationEnvelopeMapped),
    ("P1P5-CT-11 G04 vote envelope is mapped exactly", VoteEnvelopeMapped),
    ("P1P5-CT-12 G04 final decision envelope is mapped exactly", FinalDecisionEnvelopeMapped),
    ("P1P5-CT-13 client role field cannot replace authoritative assignment role", ClientRoleIgnored),
    ("P1P5-CT-14 exact replay remains delegated to P1 idempotency", G03ReplayDelegatesToP1),
    ("P1P5-CT-15 failed P3 resolution invokes no P1 executor", FailedIdentityInvokesNoExecutor)
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

Task CatalogHasSixBoundMutations()
{
    var catalog = new RecoveredApiCommandCatalogWave8();
    Eq(21, catalog.All.Count);
    Eq(6, catalog.All.Count(x => x.MutationContractRecovered));
    Seq(new[]
    {
        "evaluation-assignments.complete",
        "g04.final-decision",
        "g04.vote",
        "ideas.submit-g04",
        "needs.g03-decision",
        "needs.submit-g03"
    }, catalog.All.Where(x => x.MutationContractRecovered).Select(x => x.CommandName).OrderBy(x => x, StringComparer.Ordinal));
    return Task.CompletedTask;
}

async Task RealG03SubmitThroughGateway()
{
    var store = new TransactionalAuthorityStore(PersistenceContractDescriptor.RecoveryBaseline(), DraftNeed());
    var kernel = new AuthorityKernel(
        new RecoveredApiCommandCatalogWave8(),
        store,
        new RecoveredWave5RuleEvaluator(),
        new BaselineSodEvaluator(),
        new RecoveredWave8MutationPlanner(new StaticG04DecisionRouteProvider(CommitteeRoute())));
    var gateway = Gateway("NEED_OWNER", new AuthorityKernelExecutor(kernel));

    var attempt = new CommandAttempt(
        "needs.submit-g03", "DOMAIN\\user", "CORR-1", 1, "IDEM-1",
        "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"aggregateId\":\"NEED-1\"}");

    var result = await gateway.ExecuteAsync(attempt);
    Eq(200, result.HttpStatus); True(result.StateMutated);
    var after = await store.GetAggregateAsync("NEED-1");
    Eq("PENDING_G03_REVIEW", after!.State); Eq(2L, after.Version);
    Eq(1, store.AuditLog.Count); Eq(1, store.Outbox.Count); Eq(1, store.IdempotencyRecords.Count);
}

async Task AssignmentMismatchDenied()
{
    var directory = new InMemoryIdentityDirectoryStore(
        new[]
        {
            new PersonDirectoryEntry("P-1", "DOMAIN\\user", DirectoryPersonStatus.Active),
            new PersonDirectoryEntry("P-2", "DOMAIN\\other", DirectoryPersonStatus.Active)
        },
        new[] { Assignment("ASG-1", "P-2", "NEED_OWNER") });
    var kernel = new RecordingKernelExecutor();
    var gateway = Gateway(directory, kernel);
    var result = await gateway.ExecuteAsync(Attempt("needs.submit-g03", "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"aggregateId\":\"NEED-1\"}"));
    Eq(403, result.HttpStatus); Eq("P3_ASSIGNMENT_PERSON_MISMATCH", result.Code); Eq(0, kernel.Count);
}

async Task RequestedScopeCannotGrant()
{
    var kernel = new RecordingKernelExecutor();
    var gateway = Gateway("NEED_OWNER", kernel);
    var result = await gateway.ExecuteAsync(Attempt("needs.submit-g03",
        "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"GLOBAL\",\"aggregateId\":\"NEED-1\"}"));
    Eq(403, result.HttpStatus); Eq("P3_SCOPE_DENIED", result.Code); Eq(0, kernel.Count);
}

async Task UnrecoveredCommandClosed()
{
    var kernel = new RecordingKernelExecutor();
    var gateway = Gateway("PORTFOLIO_MANAGER", kernel);
    var result = await gateway.ExecuteAsync(Attempt("portfolio.assign-accept",
        "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\"}"));
    Eq(503, result.HttpStatus); Eq("P5_COMMAND_NOT_RECOVERED", result.Code); Eq(0, kernel.Count);
}

async Task UnknownCommandRejected()
{
    var result = await Gateway("NEED_OWNER").ExecuteAsync(Attempt("unknown.command", "{}"));
    Eq(404, result.HttpStatus); Eq("P5_COMMAND_UNKNOWN", result.Code);
}

async Task MissingIdempotencyRejected()
{
    var attempt = new CommandAttempt("needs.submit-g03", "DOMAIN\\user", "CORR", 1, null,
        "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"aggregateId\":\"NEED-1\"}");
    var result = await Gateway("NEED_OWNER").ExecuteAsync(attempt);
    Eq(400, result.HttpStatus); Eq("P5_IDEMPOTENCY_KEY_REQUIRED", result.Code);
}

async Task MalformedJsonRejected()
{
    var result = await Gateway("NEED_OWNER").ExecuteAsync(Attempt("needs.submit-g03", "{bad-json"));
    Eq(400, result.HttpStatus); Eq("P5_COMMAND_BODY_INVALID_JSON", result.Code);
}

async Task MissingExpectedVersionRejected()
{
    var attempt = new CommandAttempt("needs.submit-g03", "DOMAIN\\user", "CORR", null, "IDEM",
        "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"aggregateId\":\"NEED-1\"}");
    var result = await Gateway("NEED_OWNER").ExecuteAsync(attempt);
    Eq(400, result.HttpStatus); Eq("P5_EXPECTED_VERSION_REQUIRED", result.Code);
}

async Task EvaluationEnvelopeMapped()
{
    var recorder = new RecordingEvaluationExecutor();
    var gateway = Gateway("IDEA_EVALUATOR", evaluation: recorder);
    var body = "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"ideaId\":\"IDEA-1\",\"planId\":\"PLAN-1\",\"expectedPlanVersion\":2,\"evaluationAssignmentId\":\"EASG-1\",\"expectedAssignmentVersion\":1,\"assessment\":{\"outcome\":\"CONFIRMED\"}}";
    var result = await gateway.ExecuteAsync(Attempt("evaluation-assignments.complete", body, expectedVersion: 7));
    Eq(200, result.HttpStatus); Eq(1, recorder.Count);
    var cmd = recorder.Last!;
    Eq("IDEA-1", cmd.IdeaId); Eq(7L, cmd.ExpectedIdeaVersion); Eq("PLAN-1", cmd.PlanId); Eq(2, cmd.ExpectedPlanVersion);
    Eq("EASG-1", cmd.EvaluationAssignmentId); Eq(1, cmd.ExpectedAssignmentVersion); Eq("UNIT:RND", cmd.RequestedScope!);
    True(cmd.RawAssessmentJson.Contains("CONFIRMED", StringComparison.Ordinal));
}

async Task VoteEnvelopeMapped()
{
    var recorder = new RecordingVoteExecutor();
    var gateway = Gateway("G04_COMMITTEE_MEMBER", vote: recorder);
    var body = "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"planId\":\"PLAN-1\",\"assessmentId\":\"G04-1\",\"expectedCommitteeVersion\":3,\"vote\":\"APPROVE\",\"note\":\"documented approval reason\"}";
    var result = await gateway.ExecuteAsync(Attempt("g04.vote", body, expectedVersion: 7));
    Eq(200, result.HttpStatus); Eq(1, recorder.Count);
    Eq("PLAN-1", recorder.Last!.PlanId); Eq("G04-1", recorder.Last.AssessmentId); Eq(7L, recorder.Last.ExpectedIdeaVersion);
    Eq(3, recorder.Last.ExpectedCommitteeVersion); Eq("APPROVE", recorder.Last.Vote);
}

async Task FinalDecisionEnvelopeMapped()
{
    var recorder = new RecordingFinalExecutor();
    var gateway = Gateway("IDEA_DECISION", finalDecision: recorder);
    var body = "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"ideaId\":\"IDEA-1\",\"expectedStateMutationVersion\":10,\"planId\":\"PLAN-1\",\"expectedPlanVersion\":3,\"assessmentId\":\"G04-1\",\"expectedAssessmentDecisionVersion\":0,\"outcome\":\"HOLD\",\"reasonCode\":\"G04_EVIDENCE_INCOMPLETE\",\"decisionComment\":\"evidence requires additional verification\",\"reviewDate\":\"2026-09-20T09:00:00Z\"}";
    var result = await gateway.ExecuteAsync(Attempt("g04.final-decision", body, expectedVersion: 7));
    Eq(200, result.HttpStatus); Eq(1, recorder.Count);
    var cmd = recorder.Last!;
    Eq("IDEA-1", cmd.IdeaId); Eq(7L, cmd.ExpectedIdeaRevision); Eq(10L, cmd.ExpectedStateMutationVersion);
    Eq("HOLD", cmd.Outcome); Eq("G04_EVIDENCE_INCOMPLETE", cmd.ReasonCode); True(cmd.ReviewDate.HasValue);
}

async Task ClientRoleIgnored()
{
    var kernel = new RecordingKernelExecutor();
    var gateway = Gateway("NEED_OWNER", kernel);
    var body = "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"aggregateId\":\"NEED-1\",\"role\":\"ADMIN\"}";
    var result = await gateway.ExecuteAsync(Attempt("needs.submit-g03", body));
    Eq(200, result.HttpStatus); Eq(1, kernel.Count); Seq(new[] { "NEED_OWNER" }, kernel.LastActor!.Roles);
}

async Task G03ReplayDelegatesToP1()
{
    var store = new TransactionalAuthorityStore(PersistenceContractDescriptor.RecoveryBaseline(), DraftNeed());
    var kernel = new AuthorityKernel(
        new RecoveredApiCommandCatalogWave8(), store, new RecoveredWave5RuleEvaluator(), new BaselineSodEvaluator(),
        new RecoveredWave8MutationPlanner(new StaticG04DecisionRouteProvider(CommitteeRoute())));
    var gateway = Gateway("NEED_OWNER", new AuthorityKernelExecutor(kernel));
    var attempt = Attempt("needs.submit-g03",
        "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"aggregateId\":\"NEED-1\"}", 1, "IDEM-REPLAY");
    var first = await gateway.ExecuteAsync(attempt);
    var replay = await gateway.ExecuteAsync(attempt);
    Eq(200, first.HttpStatus); Eq(200, replay.HttpStatus); True(first.StateMutated); False(replay.StateMutated);
    Eq(1, store.AuditLog.Count); Eq(1, store.Outbox.Count); Eq(1, store.IdempotencyRecords.Count);
}

async Task FailedIdentityInvokesNoExecutor()
{
    var directory = new InMemoryIdentityDirectoryStore(Array.Empty<PersonDirectoryEntry>(), Array.Empty<RoleAssignmentEntry>());
    var kernel = new RecordingKernelExecutor();
    var gateway = Gateway(directory, kernel);
    var result = await gateway.ExecuteAsync(Attempt("needs.submit-g03",
        "{\"assignmentId\":\"ASG-1\",\"requestedScope\":\"UNIT:RND\",\"aggregateId\":\"NEED-1\"}"));
    Eq(403, result.HttpStatus); Eq("P3_PERSON_NOT_MAPPED", result.Code); Eq(0, kernel.Count);
}

P1RecoveryCommandGateway Gateway(
    string role,
    IP1AuthorityKernelExecutor? kernel = null,
    IP1EvaluationCompletionExecutor? evaluation = null,
    IP1G04VoteExecutor? vote = null,
    IP1G04FinalDecisionExecutor? finalDecision = null)
{
    var directory = new InMemoryIdentityDirectoryStore(
        new[] { new PersonDirectoryEntry("P-1", "DOMAIN\\user", DirectoryPersonStatus.Active) },
        new[] { Assignment("ASG-1", "P-1", role) });
    return Gateway(directory, kernel, evaluation, vote, finalDecision);
}

P1RecoveryCommandGateway Gateway(
    IIdentityDirectoryStore directory,
    IP1AuthorityKernelExecutor? kernel = null,
    IP1EvaluationCompletionExecutor? evaluation = null,
    IP1G04VoteExecutor? vote = null,
    IP1G04FinalDecisionExecutor? finalDecision = null) =>
    new(
        new WindowsIdentityRbacResolver(directory),
        kernel ?? new RecordingKernelExecutor(),
        evaluation ?? new RecordingEvaluationExecutor(),
        vote ?? new RecordingVoteExecutor(),
        finalDecision ?? new RecordingFinalExecutor(),
        new RecoveredApiCommandCatalogWave8());

RoleAssignmentEntry Assignment(string id, string person, string role) =>
    new(id, person, role, new[] { "UNIT:RND" }, DateTimeOffset.UtcNow.AddDays(-1), null, false);

CommandAttempt Attempt(string command, string body, long? expectedVersion = 1, string? key = "IDEM") =>
    new(command, "DOMAIN\\user", "CORR", expectedVersion, key, body);

AggregateSnapshot DraftNeed() =>
    new("NEED-1", "Need", "DRAFT", 1, "P-1", "NEED_OWNER", "UNIT:RND",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = "Need title",
            ["owner"] = "Need owner",
            ["current"] = "Current measurable condition is documented.",
            ["desired"] = "Desired measurable condition is documented.",
            ["gap"] = "Gap is documented and measurable."
        }, "NEED_OWNER");

G04DecisionRouteMetadata CommitteeRoute() => new("G04_COMMITTEE", "COMMITTEE", "MAJORITY", "GOV-1", "1.0");

AuthorityResult Success(string code) => new(200, code, true, true, false, 1, "CORR", Array.Empty<string>());

void Seq<T>(IEnumerable<T> expected, IEnumerable<T> actual)
{
    if (!expected.SequenceEqual(actual))
        throw new Exception($"Sequences differ: expected [{string.Join(",", expected)}], actual [{string.Join(",", actual)}]");
}

void True(bool value) { if (!value) throw new Exception("Expected true"); }
void False(bool value) { if (value) throw new Exception("Expected false"); }
void Eq<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected '{expected}', actual '{actual}'");
}

sealed class RecordingKernelExecutor : IP1AuthorityKernelExecutor
{
    public int Count { get; private set; }
    public AuthorityCommand? Last { get; private set; }
    public AuthorityActor? LastActor { get; private set; }
    public ValueTask<AuthorityResult> ExecuteAsync(AuthorityCommand command, AuthorityActor actor, CancellationToken cancellationToken = default)
    {
        Count++; Last = command; LastActor = actor;
        return ValueTask.FromResult(new AuthorityResult(200, "RECORDED", true, true, false, command.ExpectedVersion + 1, command.CorrelationId, Array.Empty<string>()));
    }
}

sealed class RecordingEvaluationExecutor : IP1EvaluationCompletionExecutor
{
    public int Count { get; private set; }
    public EvaluationCompletionCommand? Last { get; private set; }
    public ValueTask<AuthorityResult> ExecuteAsync(EvaluationCompletionCommand command, AuthorityActor actor, CancellationToken cancellationToken = default)
    {
        Count++; Last = command;
        return ValueTask.FromResult(new AuthorityResult(200, "RECORDED", true, true, false, command.ExpectedIdeaVersion, command.CorrelationId, Array.Empty<string>()));
    }
}

sealed class RecordingVoteExecutor : IP1G04VoteExecutor
{
    public int Count { get; private set; }
    public G04VoteCommand? Last { get; private set; }
    public ValueTask<AuthorityResult> ExecuteAsync(G04VoteCommand command, AuthorityActor actor, CancellationToken cancellationToken = default)
    {
        Count++; Last = command;
        return ValueTask.FromResult(new AuthorityResult(200, "RECORDED", true, true, false, command.ExpectedIdeaVersion, command.CorrelationId, Array.Empty<string>()));
    }
}

sealed class RecordingFinalExecutor : IP1G04FinalDecisionExecutor
{
    public int Count { get; private set; }
    public G04FinalDecisionCommand? Last { get; private set; }
    public ValueTask<AuthorityResult> ExecuteAsync(G04FinalDecisionCommand command, AuthorityActor actor, CancellationToken cancellationToken = default)
    {
        Count++; Last = command;
        return ValueTask.FromResult(new AuthorityResult(200, "RECORDED", true, true, false, command.ExpectedIdeaRevision, command.CorrelationId, Array.Empty<string>()));
    }
}
