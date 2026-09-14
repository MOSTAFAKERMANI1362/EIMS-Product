using System.Text.Json;
using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;
using EIMS.PilotAssembly.Core;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: EIMS.G03.Mutation.Recovery.ContractTests <P1-wave4-g03-mutation-json>");
    return 2;
}

using var wave4Doc = JsonDocument.Parse(File.ReadAllText(args[0]));
var wave4 = wave4Doc.RootElement;
var tests = new List<(string Name, Func<Task> Run)>
{
    ("P1W4-CT-01 Wave 4 artifact identity and provenance", Wave4Artifact),
    ("P1W4-CT-02 runtime promotes exactly two G03 mutation commands", MutationPromotion),
    ("P1W4-CT-03 submit mutates Need exactly", SubmitMutation),
    ("P1W4-CT-04 submit preserves owner and scope", SubmitPreservesAuthorityFacts),
    ("P1W4-CT-05 submit emits exact event without decision record", SubmitEventNoDecision),
    ("P1W4-CT-06 approve mutates Need exactly", ApproveMutation),
    ("P1W4-CT-07 approve decision is stamped server-side", ApproveDecisionStamp),
    ("P1W4-CT-08 approve controls are retained in immutable decision facts", ApproveDecisionFacts),
    ("P1W4-CT-09 return mutates Need exactly", ReturnMutation),
    ("P1W4-CT-10 return note is retained only in decision record", ReturnNoteRetention),
    ("P1W4-CT-11 client cannot override state review routing or event", ClientCannotOverrideAuthoritativeMutation),
    ("P1W4-CT-12 workflow routing does not change owner role or actor RBAC", RoutingHasNoRbacEffect),
    ("P1W4-CT-13 Need Owner self-review remains denied", SelfReviewDenied),
    ("P1W4-CT-14 unknown G03 outcome remains denied", UnknownOutcomeDenied),
    ("P1W4-CT-15 short return note remains denied", ShortReturnDenied),
    ("P1W4-CT-16 exact idempotent replay creates no duplicate evidence", IdempotentReplay),
    ("P1W4-CT-17 stale optimistic version is denied", VersionConflict),
    ("P1W4-CT-18 rollback after state staging is atomic", () => RollbackAt(PersistenceFaultPoint.AfterStateStaged)),
    ("P1W4-CT-19 rollback after decision staging is atomic", () => RollbackAt(PersistenceFaultPoint.AfterDecisionStaged)),
    ("P1W4-CT-20 rollback after audit staging is atomic", () => RollbackAt(PersistenceFaultPoint.AfterAuditStaged)),
    ("P1W4-CT-21 rollback after outbox staging is atomic", () => RollbackAt(PersistenceFaultPoint.AfterOutboxStaged)),
    ("P1W4-CT-22 rollback after idempotency staging is atomic", () => RollbackAt(PersistenceFaultPoint.AfterIdempotencyStaged)),
    ("P1W4-CT-23 duplicate decision id is rejected", DuplicateDecisionIdDenied),
    ("P1W4-CT-24 forged decision authority is rejected", DecisionAuthorityMismatchDenied),
    ("P1W4-CT-25 non-G03 Product commands remain fail closed", NonG03RemainsClosed),
    ("P1W4-CT-26 P5 command gateway remains fail closed", P5StillFailClosed)
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

Task Wave4Artifact()
{
    Eq("EIMS-P1-RECOVERY-WAVE4-G03-MUTATION-REBASELINE-1.0", S(wave4,"schema"));
    Eq("APPROVED_REBASELINE_FOR_P1_MUTATION_BINDING", S(wave4,"status"));
    Eq("RECOVERY_REBASELINE_ACCEPTANCE", S(wave4,"decisionClass"));
    Eq("ACR-P0-003", S(wave4.GetProperty("sourceDecision"),"acrId"));
    False(wave4.GetProperty("sourceDecision").GetProperty("historicalOriginalMutationCatalogRecovered").GetBoolean());
    False(wave4.GetProperty("frozenProduct").GetProperty("modifiedByThisRebaseline").GetBoolean());
    False(wave4.GetProperty("runtimeSafety").GetProperty("p5CommandGatewayBound").GetBoolean());
    return Task.CompletedTask;
}

Task MutationPromotion()
{
    var catalog = new RecoveredApiCommandCatalog();
    Eq(2, RecoveredApiCommandCatalog.Wave4MutationBoundCommandCount);
    var promoted = catalog.All.Where(x => x.MutationContractRecovered).Select(x => x.CommandName).OrderBy(x => x).ToArray();
    Seq(new[]{"needs.g03-decision","needs.submit-g03"}, promoted);
    return Task.CompletedTask;
}

async Task SubmitMutation()
{
    var store = Store(DraftNeed());
    var result = await Kernel(store).ExecuteAsync(Submit(), Owner());
    Eq(200, result.HttpStatus); True(result.StateMutated); Eq(2L, result.NewVersion!.Value);
    var after = (await store.GetAggregateAsync("NEED-1"))!;
    Eq("PENDING_G03_REVIEW", after.State); Eq(2L, after.Version); Eq("PENDING", Fact(after,"g03ReviewStatus")); Eq("NEED_REVIEWER", after.WorkRoutingRole!);
}

async Task SubmitPreservesAuthorityFacts()
{
    var before = DraftNeed(); var store = Store(before);
    await Kernel(store).ExecuteAsync(Submit(), Owner());
    var after = (await store.GetAggregateAsync("NEED-1"))!;
    Eq(before.OwnerPersonId!, after.OwnerPersonId!); Eq(before.OwnerRole!, after.OwnerRole!); Eq(before.Scope!, after.Scope!);
}

async Task SubmitEventNoDecision()
{
    var store = Store(DraftNeed());
    var result = await Kernel(store).ExecuteAsync(Submit(), Owner());
    Seq(new[]{"NeedSubmittedForG03Review.v1"}, result.EmittedEvents.ToArray());
    Eq(0, store.DomainDecisions.Count); Eq(1, store.AuditLog.Count); Eq(1, store.Outbox.Count); Eq(1, store.IdempotencyRecords.Count);
}

async Task ApproveMutation()
{
    var store = Store(PendingNeed());
    var result = await Kernel(store).ExecuteAsync(Approve(), Reviewer());
    Eq(200, result.HttpStatus); True(result.StateMutated);
    var after = (await store.GetAggregateAsync("NEED-1"))!;
    Eq("READY_FOR_IDEATION", after.State); Eq(3L, after.Version); Eq("APPROVED", Fact(after,"g03ReviewStatus")); Eq("IDEA_OWNER", after.WorkRoutingRole!);
    Seq(new[]{"NeedApprovedForIdeation.v1"}, result.EmittedEvents.ToArray());
}

async Task ApproveDecisionStamp()
{
    var store = Store(PendingNeed());
    await Kernel(store).ExecuteAsync(Approve(), Reviewer());
    var d = store.DomainDecisions.Single();
    Eq("G03ReviewDecision", d.DecisionType); Eq("APPROVE", d.Outcome); Eq("NEED-1", d.AggregateId); Eq(3L, d.EntityVersion);
    Eq("P-REVIEW", d.PersonId); Eq("ASG-REVIEW", d.AssignmentId); Eq("CORR-APPROVE", d.CorrelationId);
    True(d.DecisionId.StartsWith("DEC-", StringComparison.Ordinal)); True(d.Timestamp <= DateTimeOffset.UtcNow);
}

async Task ApproveDecisionFacts()
{
    var store = Store(PendingNeed());
    await Kernel(store).ExecuteAsync(Approve(), Reviewer());
    var f = store.DomainDecisions.Single().Facts!;
    Eq("YES", f["definitionComplete"]); Eq("YES", f["measurable"]); Eq("YES", f["solutionBiasFree"]);
}

async Task ReturnMutation()
{
    var store = Store(PendingNeed());
    var result = await Kernel(store).ExecuteAsync(Return(), Reviewer());
    Eq(200, result.HttpStatus);
    var after = (await store.GetAggregateAsync("NEED-1"))!;
    Eq("DRAFT", after.State); Eq(3L, after.Version); Eq("RETURNED", Fact(after,"g03ReviewStatus")); Eq("NEED_OWNER", after.WorkRoutingRole!);
    Seq(new[]{"NeedReturnedFromG03Review.v1"}, result.EmittedEvents.ToArray());
}

async Task ReturnNoteRetention()
{
    const string note = "لطفاً خط مبنای عددی توقف را دقیق‌تر ثبت کنید.";
    var store = Store(PendingNeed());
    await Kernel(store).ExecuteAsync(Return(note), Reviewer());
    var d = store.DomainDecisions.Single(); Eq(note, d.Note!);
    var outbox = store.Outbox.Single(); Eq("NeedReturnedFromG03Review.v1", outbox.EventName); False(outbox.EventName.Contains(note, StringComparison.Ordinal));
}

async Task ClientCannotOverrideAuthoritativeMutation()
{
    var body = "{\"decision\":\"APPROVE\",\"definitionComplete\":\"YES\",\"measurable\":\"YES\",\"solutionBiasFree\":\"YES\",\"state\":\"HACKED\",\"g03ReviewStatus\":\"RETURNED\",\"workRoutingRole\":\"ADMIN\",\"eventName\":\"Forged.v1\"}";
    var store = Store(PendingNeed());
    var cmd = new AuthorityCommand("needs.g03-decision","NEED-1",2,"K-FORGE","C-FORGE",body,"UNIT:RND");
    var result = await Kernel(store).ExecuteAsync(cmd, Reviewer());
    Eq(200, result.HttpStatus);
    var after = (await store.GetAggregateAsync("NEED-1"))!;
    Eq("READY_FOR_IDEATION", after.State); Eq("APPROVED", Fact(after,"g03ReviewStatus")); Eq("IDEA_OWNER", after.WorkRoutingRole!);
    Eq("NeedApprovedForIdeation.v1", store.Outbox.Single().EventName);
}

async Task RoutingHasNoRbacEffect()
{
    var actor = Reviewer(); var store = Store(PendingNeed());
    await Kernel(store).ExecuteAsync(Approve(), actor);
    var after = (await store.GetAggregateAsync("NEED-1"))!;
    Eq("NEED_OWNER", after.OwnerRole!); Eq("IDEA_OWNER", after.WorkRoutingRole!);
    Seq(new[]{"NEED_REVIEWER"}, actor.Roles.ToArray());
}

async Task SelfReviewDenied()
{
    var ownerReviewer = new AuthorityActor("P-OWNER","DOMAIN\\owner","WINDOWS_PRINCIPAL","ASG-X",new[]{"NEED_REVIEWER"},new[]{"UNIT:RND"});
    var store = Store(PendingNeed());
    var r = await Kernel(store).ExecuteAsync(Approve(), ownerReviewer);
    Eq(403, r.HttpStatus); Eq("SOD_G03_NEED_OWNER_SELF_REVIEW", r.Code); False(r.StateMutated); Eq(0, store.DomainDecisions.Count);
}

async Task UnknownOutcomeDenied()
{
    var store = Store(PendingNeed());
    var cmd = new AuthorityCommand("needs.g03-decision","NEED-1",2,"K-HOLD","C-HOLD","{\"decision\":\"HOLD\"}","UNIT:RND");
    var r = await Kernel(store).ExecuteAsync(cmd, Reviewer());
    Eq(422, r.HttpStatus); Eq("G03_D02_DECISION_INVALID", r.Code); Eq(0, store.DomainDecisions.Count);
}

async Task ShortReturnDenied()
{
    var store = Store(PendingNeed());
    var r = await Kernel(store).ExecuteAsync(Return("اصلاح شود"), Reviewer());
    Eq(422, r.HttpStatus); Eq("G03_D04_RETURN_NOTE_TOO_SHORT", r.Code); Eq(0, store.DomainDecisions.Count);
}

async Task IdempotentReplay()
{
    var store = Store(PendingNeed()); var kernel = Kernel(store); var cmd = Approve();
    var first = await kernel.ExecuteAsync(cmd, Reviewer());
    var second = await kernel.ExecuteAsync(cmd, Reviewer());
    True(first.StateMutated); True(second.IdempotentReplay); False(second.StateMutated);
    Eq(1, store.DomainDecisions.Count); Eq(1, store.AuditLog.Count); Eq(1, store.Outbox.Count); Eq(1, store.IdempotencyRecords.Count);
}

async Task VersionConflict()
{
    var store = Store(PendingNeed()); var kernel = Kernel(store);
    await kernel.ExecuteAsync(Approve(), Reviewer());
    var stale = Return() with { IdempotencyKey = "K-STALE", CorrelationId = "C-STALE" };
    var r = await kernel.ExecuteAsync(stale, Reviewer());
    Eq(409, r.HttpStatus); Eq("P1_VERSION_CONFLICT", r.Code); Eq(1, store.DomainDecisions.Count);
}

async Task RollbackAt(PersistenceFaultPoint point)
{
    var store = Store(PendingNeed()); store.FaultPoint = point;
    var threw = false;
    try { await Kernel(store).ExecuteAsync(Approve(), Reviewer()); }
    catch (PersistenceAtomicityException) { threw = true; }
    True(threw);
    var after = (await store.GetAggregateAsync("NEED-1"))!;
    Eq("PENDING_G03_REVIEW", after.State); Eq(2L, after.Version); Eq("PENDING", Fact(after,"g03ReviewStatus"));
    Eq(0, store.DomainDecisions.Count); Eq(0, store.AuditLog.Count); Eq(0, store.Outbox.Count); Eq(0, store.IdempotencyRecords.Count);
}

async Task DuplicateDecisionIdDenied()
{
    var a1 = PendingNeed() with { AggregateId = "N1" };
    var a2 = PendingNeed() with { AggregateId = "N2" };
    var store = new TransactionalAuthorityStore(PersistenceContractDescriptor.RecoveryBaseline(), a1, a2);
    var actor = Reviewer();
    var c1 = new AuthorityCommand("test","N1",2,"K1","C1","{}");
    var c2 = new AuthorityCommand("test","N2",2,"K2","C2","{}");
    var p = new CommandPolicy("test",new[]{"NEED_REVIEWER"},new[]{"PENDING_G03_REVIEW"},"R","E");
    var r1 = await store.CommitAsync(Request(c1,actor,a1,p), Commit(c1,actor,a1,"DEC-SAME"));
    Eq(200, r1.HttpStatus);
    var r2 = await store.CommitAsync(Request(c2,actor,a2,p), Commit(c2,actor,a2,"DEC-SAME"));
    Eq(409, r2.HttpStatus); Eq("P2_DUPLICATE_DECISION_ID", r2.Code); Eq(1, store.DomainDecisions.Count);
}

async Task DecisionAuthorityMismatchDenied()
{
    var before = PendingNeed(); var store = Store(before); var actor = Reviewer();
    var cmd = new AuthorityCommand("test","NEED-1",2,"KX","CX","{}");
    var p = new CommandPolicy("test",new[]{"NEED_REVIEWER"},new[]{"PENDING_G03_REVIEW"},"R","E");
    var commit = Commit(cmd,actor,before,"DEC-X");
    commit = commit with { Decisions = new[]{commit.Decisions!.Single() with { PersonId = "FORGED" }} };
    var r = await store.CommitAsync(Request(cmd,actor,before,p),commit);
    Eq(500, r.HttpStatus); Eq("P2_DECISION_AUTHORITY_MISMATCH", r.Code); Eq(0, store.DomainDecisions.Count);
}

async Task NonG03RemainsClosed()
{
    var store = Store(PendingNeed());
    var cmd = new AuthorityCommand("g04.vote","NEED-1",2,"KG","CG","{}");
    var r = await Kernel(store).ExecuteAsync(cmd,new AuthorityActor("P-G04","DOMAIN\\g04","WINDOWS_PRINCIPAL","ASG-G04",new[]{"G04_COMMITTEE_MEMBER"},new[]{"UNIT:RND"}));
    Eq(503, r.HttpStatus); Eq("P1_STATE_CONTRACT_NOT_RECOVERED", r.Code); False(r.StateMutated);
}

async Task P5StillFailClosed()
{
    ICommandGateway gateway = new FailClosedCommandGateway();
    var r = await gateway.ExecuteAsync(new CommandAttempt("needs.submit-g03","DOMAIN\\owner","C",1,"K","{}"));
    Eq(503, r.HttpStatus); Eq("P5_COMMAND_GATEWAY_NOT_BOUND", r.Code); False(r.StateMutated);
}

static AuthorityKernel Kernel(TransactionalAuthorityStore store) =>
    new(new RecoveredApiCommandCatalog(), store, new RecoveredG03RuleEvaluator(), new BaselineSodEvaluator(), new RecoveredG03MutationPlanner());

static TransactionalAuthorityStore Store(params AggregateSnapshot[] aggregates) =>
    new(PersistenceContractDescriptor.RecoveryBaseline(), aggregates);

static AuthorityActor Owner() => new("P-OWNER","DOMAIN\\owner","WINDOWS_PRINCIPAL","ASG-OWNER",new[]{"NEED_OWNER"},new[]{"UNIT:RND"});
static AuthorityActor Reviewer() => new("P-REVIEW","DOMAIN\\reviewer","WINDOWS_PRINCIPAL","ASG-REVIEW",new[]{"NEED_REVIEWER"},new[]{"UNIT:RND"});

static Dictionary<string,string> GoodFacts() => new(StringComparer.OrdinalIgnoreCase)
{
    ["title"]="کاهش توقف اضطراری خط نورد",
    ["owner"]="سرپرست نورد",
    ["current"]="میانگین توقف اضطراری ماهانه ۱۸ ساعت است.",
    ["desired"]="توقف اضطراری ماهانه باید به کمتر از ۶ ساعت برسد.",
    ["gap"]="کاهش حداقل ۱۲ ساعت توقف در ماه لازم است."
};

static AggregateSnapshot DraftNeed() => new("NEED-1","Need","DRAFT",1,"P-OWNER","NEED_OWNER","UNIT:RND",GoodFacts(),"NEED_OWNER");
static AggregateSnapshot PendingNeed()
{
    var f = GoodFacts(); f["g03ReviewStatus"]="PENDING";
    return new("NEED-1","Need","PENDING_G03_REVIEW",2,"P-OWNER","NEED_OWNER","UNIT:RND",f,"NEED_REVIEWER");
}

static AuthorityCommand Submit() => new("needs.submit-g03","NEED-1",1,"K-SUBMIT","CORR-SUBMIT","{}","UNIT:RND");
static AuthorityCommand Approve() => new("needs.g03-decision","NEED-1",2,"K-APPROVE","CORR-APPROVE","{\"decision\":\"APPROVE\",\"definitionComplete\":\"YES\",\"measurable\":\"YES\",\"solutionBiasFree\":\"YES\"}","UNIT:RND");
static AuthorityCommand Return(string note = "لطفاً خط مبنای عددی توقف را دقیق‌تر ثبت کنید.") => new("needs.g03-decision","NEED-1",2,"K-RETURN","CORR-RETURN",$"{{\"decision\":\"RETURN\",\"note\":{JsonSerializer.Serialize(note)}}}","UNIT:RND");

static MutationRequest Request(AuthorityCommand c, AuthorityActor a, AggregateSnapshot before, CommandPolicy p) => new(c,a,before,p,AuthorityKernel.Fingerprint(c));
static MutationCommit Commit(AuthorityCommand c, AuthorityActor a, AggregateSnapshot before, string decisionId)
{
    var after = before with { Version = before.Version + 1 };
    var now = DateTimeOffset.UtcNow;
    var audit = new AuditEnvelope("AUD-"+decisionId,a.PersonId,a.NetworkIdentity,a.IdentitySource,a.Roles,a.AssignmentId,after.AggregateId,after.Version,"R",now,c.CorrelationId,c.CommandName);
    var outbox = new OutboxEnvelope("MSG-"+decisionId,"E",after.AggregateId,after.Version,c.CorrelationId,now);
    var d = new DomainDecisionEnvelope(decisionId,"G03ReviewDecision","APPROVE",after.AggregateId,after.Version,a.PersonId,a.AssignmentId,now,c.CorrelationId);
    return new MutationCommit(after,audit,outbox,new[]{d});
}

static string Fact(AggregateSnapshot a, string key) => a.RuleFacts!.First(x => string.Equals(x.Key,key,StringComparison.OrdinalIgnoreCase)).Value;
static string S(JsonElement e, string p) => e.GetProperty(p).GetString() ?? string.Empty;
static void True(bool v) { if (!v) throw new InvalidOperationException("Expected true."); }
static void False(bool v) { if (v) throw new InvalidOperationException("Expected false."); }
static void Eq<T>(T expected, T actual) where T:notnull { if (!EqualityComparer<T>.Default.Equals(expected,actual)) throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'."); }
static void Seq<T>(IEnumerable<T> expected, IEnumerable<T> actual) { if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("Sequences differ."); }
