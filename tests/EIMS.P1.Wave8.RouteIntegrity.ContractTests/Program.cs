using System.Text.Json;
using EIMS.Authority.Recovery;

if (args.Length != 2 || args.Any(x => !File.Exists(x)))
{
    Console.Error.WriteLine("Usage: EIMS.P1.Wave8.RouteIntegrity.ContractTests <route-addendum-json> <wave8-route-rebaseline-json>");
    return 2;
}

using var acrDoc = JsonDocument.Parse(File.ReadAllText(args[0]));
using var waveDoc = JsonDocument.Parse(File.ReadAllText(args[1]));
var acr = acrDoc.RootElement;
var wave = waveDoc.RootElement;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("W8R-01 ACR route addendum is approved for P1 implementation", ArtifactIdentity),
    ("W8R-02 committee route metadata validates", CommitteeRouteValid),
    ("W8R-03 individual route metadata validates", IndividualRouteValid),
    ("W8R-04 incomplete or incoherent route metadata fails validation", InvalidRoutesFail),
    ("W8R-05 Wave8 Idea submission planner persists server route metadata", PlannerPersistsRoute),
    ("W8R-06 invalid route provider fails Idea submission closed", InvalidProviderFailsClosed),
    ("W8R-07 final required evaluation freezes Plan route into G04Assessment", EvaluationCompletionCopiesRoute),
    ("W8R-08 voting rejects assessment with missing route", VotingMissingRouteDenied),
    ("W8R-09 voting rejects individual route", IndividualVotingDenied),
    ("W8R-10 voting rejects governance profile drift", GovernanceDriftDenied),
    ("W8R-11 valid committee route produces frozen matching snapshot", CommitteeRouteSucceeds),
    ("W8R-12 existing snapshot route/profile mismatch fails closed", SnapshotMismatchDenied),
    ("W8R-13 Wave8 safety boundaries remain fail closed downstream", SafetyBoundaries)
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

Task ArtifactIdentity()
{
    Eq("ACR-P0-007-ROUTE-INTEGRITY", S(acr, "addendumId"));
    Eq("APPROVED_FOR_P1_IMPLEMENTATION", S(acr, "status"));
    Eq("EIMS-P1-RECOVERY-WAVE8-ROUTE-INTEGRITY-REBASELINE-1.0", S(wave, "schema"));
    Eq("ROUTE_INTEGRITY_HARDENING_READY_FOR_CONTRACT_TEST", S(wave, "status"));
    return Task.CompletedTask;
}

Task CommitteeRouteValid()
{
    True(CommitteeRoute().IsValid);
    return Task.CompletedTask;
}

Task IndividualRouteValid()
{
    True(new G04DecisionRouteMetadata("UNIT_RND_DECISION", "INDIVIDUAL", "INDIVIDUAL_GOVERNANCE_DECISION", "GOV-I", "1.0").IsValid);
    return Task.CompletedTask;
}

Task InvalidRoutesFail()
{
    False(new G04DecisionRouteMetadata("G04_COMMITTEE", "COMMITTEE", "INDIVIDUAL_GOVERNANCE_DECISION", "GOV-1", "1.0").IsValid);
    False(new G04DecisionRouteMetadata("G04_COMMITTEE", "INDIVIDUAL", "MAJORITY", "GOV-1", "1.0").IsValid);
    False(new G04DecisionRouteMetadata("", "COMMITTEE", "MAJORITY", "GOV-1", "1.0").IsValid);
    return Task.CompletedTask;
}

async Task PlannerPersistsRoute()
{
    var catalog = new RecoveredApiCommandCatalogWave7();
    True(catalog.TryGet("ideas.submit-g04", out var policy));
    var planner = new RecoveredWave8MutationPlanner(new StaticG04DecisionRouteProvider(CommitteeRoute()));
    var idea = Idea();
    var actor = Actor("IDEA_OWNER");
    var command = new AuthorityCommand("ideas.submit-g04", idea.AggregateId, idea.Version, "K-P", "CORR-P", "{}", idea.Scope);
    var plan = await planner.PlanAsync(command, actor, idea, policy);
    True(plan is not null && plan.EvaluationPlanIntent is not null);
    Eq("G04_COMMITTEE", plan!.EvaluationPlanIntent!.DecisionRoute!);
    Eq("COMMITTEE", plan.EvaluationPlanIntent.DecisionRouteKind!);
    Eq("MAJORITY", plan.EvaluationPlanIntent.DecisionMethod!);
    Eq("GOV-1", plan.EvaluationPlanIntent.GovernanceProfileId!);
}

async Task InvalidProviderFailsClosed()
{
    var catalog = new RecoveredApiCommandCatalogWave7();
    True(catalog.TryGet("ideas.submit-g04", out var policy));
    var invalid = new G04DecisionRouteMetadata("G04_COMMITTEE", "INDIVIDUAL", "MAJORITY", "GOV-1", "1.0");
    var planner = new RecoveredWave8MutationPlanner(new StaticG04DecisionRouteProvider(invalid));
    var idea = Idea();
    var plan = await planner.PlanAsync(
        new AuthorityCommand("ideas.submit-g04", idea.AggregateId, idea.Version, "K-I", "CORR-I", "{}", idea.Scope),
        Actor("IDEA_OWNER"), idea, policy);
    True(plan is null);
}

async Task EvaluationCompletionCopiesRoute()
{
    var route = CommitteeRoute();
    var idea = Idea(state: "UNDER_REVIEW");
    var plan = Plan(route);
    var assignment = new EvaluationAssignmentEnvelope(
        "EASG-1", plan.PlanId, idea.AggregateId, idea.Version, "IDEA_EVALUATOR", idea.Scope!, true,
        "PENDING", DateTimeOffset.UtcNow.AddMinutes(-5), "CORR-CREATE");
    var store = new WorkflowStore(idea, plan, assignment);
    var service = new EvaluationCompletionService(store, new ValidAssessmentValidator());
    var result = await service.CompleteAsync(
        new EvaluationCompletionCommand(idea.AggregateId, idea.Version, plan.PlanId, plan.PlanVersion,
            assignment.AssignmentId, assignment.AssignmentVersion, "K-E", "CORR-E", "{}", idea.Scope),
        Actor("IDEA_EVALUATOR"));
    Eq(200, result.HttpStatus);
    True(store.CommittedPlan is not null && store.CreatedAssessment is not null);
    True(G04RouteIntegrity.PlanAssessmentMatch(store.CommittedPlan!, store.CreatedAssessment!));
    Eq("G04_COMMITTEE", store.CreatedAssessment!.DecisionRoute!);
    Eq("MAJORITY", store.CreatedAssessment.DecisionMethod!);
}

async Task VotingMissingRouteDenied()
{
    var store = new VotingStore(Idea(state: "UNDER_REVIEW"), Assessment(route: null));
    var result = await Voting(store, Profile()).VoteAsync(VoteCommand(), Actor("G04_COMMITTEE_MEMBER"));
    Eq(409, result.HttpStatus);
    Eq("P1_G04_VOTE_ROUTE_CONTEXT_MISSING", result.Code);
}

async Task IndividualVotingDenied()
{
    var route = new G04DecisionRouteMetadata("UNIT_RND_DECISION", "INDIVIDUAL", "INDIVIDUAL_GOVERNANCE_DECISION", "GOV-I", "1.0");
    var store = new VotingStore(Idea(state: "UNDER_REVIEW"), Assessment(route));
    var result = await Voting(store, Profile()).VoteAsync(VoteCommand(), Actor("G04_COMMITTEE_MEMBER"));
    Eq(409, result.HttpStatus);
    Eq("P1_G04_VOTE_ROUTE_NOT_COMMITTEE", result.Code);
}

async Task GovernanceDriftDenied()
{
    var store = new VotingStore(Idea(state: "UNDER_REVIEW"), Assessment(CommitteeRoute()));
    var drift = Profile() with { GovernanceProfileId = "GOV-DRIFT" };
    var result = await Voting(store, drift).VoteAsync(VoteCommand(), Actor("G04_COMMITTEE_MEMBER"));
    Eq(503, result.HttpStatus);
    Eq("P1_G04_GOVERNANCE_PROFILE_NOT_BOUND", result.Code);
}

async Task CommitteeRouteSucceeds()
{
    var store = new VotingStore(Idea(state: "UNDER_REVIEW"), Assessment(CommitteeRoute()));
    var result = await Voting(store, Profile()).VoteAsync(VoteCommand(), Actor("G04_COMMITTEE_MEMBER"));
    Eq(200, result.HttpStatus);
    True(result.StateMutated);
    True(store.Snapshot is not null);
    Eq("G04_COMMITTEE", store.Snapshot!.DecisionRoute);
    Eq("GOV-1", store.Snapshot.GovernanceProfileId);
    Eq("MAJORITY", store.Snapshot.VoteRule);
}

async Task SnapshotMismatchDenied()
{
    var idea = Idea(state: "UNDER_REVIEW");
    var assessment = Assessment(CommitteeRoute());
    var store = new VotingStore(idea, assessment)
    {
        Snapshot = new G04CommitteeSnapshotEnvelope(
            "SNAP-X", assessment.AssessmentId, idea.AggregateId, idea.Version, idea.Scope!, "G04_COMMITTEE",
            "GOV-WRONG", "1.0", 1, "MAJORITY", "P-M1", "IDEA_DECISION", "REF",
            Members(), DateTimeOffset.UtcNow.AddMinutes(-2), "CORR-S")
    };
    var result = await Voting(store, Profile()).VoteAsync(VoteCommand(), Actor("G04_COMMITTEE_MEMBER"));
    Eq(409, result.HttpStatus);
    Eq("P1_G04_FROZEN_SNAPSHOT_ROUTE_MISMATCH", result.Code);
}

Task SafetyBoundaries()
{
    var safety = wave.GetProperty("runtimeSafety");
    False(B(safety, "g04FinalDecisionPromoted"));
    False(B(safety, "portfolioEligibilityTriggered"));
    False(B(safety, "p5CommandGatewayBound"));
    False(B(safety, "physicalOracleReadyClaimed"));
    False(B(safety, "liveWindowsDomainReadyClaimed"));
    False(B(safety, "networkPilotReadyClaimed"));
    True(B(safety, "v6360Unchanged"));
    return Task.CompletedTask;
}

static G04VotingServiceWave8 Voting(VotingStore store, G04GovernanceProfile profile) =>
    new(store, new StaticProfileProvider(profile), new StaticMemberResolver(Members()), new RecoveredApiCommandCatalogWave7());

static AggregateSnapshot Idea(string state = "DRAFT") =>
    new("IDEA-1", "Idea", state, 7, "P-OWNER", "IDEA_OWNER", "UNIT:RND",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["passportCompletionPercent"] = "90",
            ["strategyLinkActive"] = "true",
            ["primaryObjectiveReady"] = "true",
            ["requiresTechnicalEvaluation"] = "false",
            ["requiresHseEvaluation"] = "false",
            ["requiresFinancialEvaluation"] = "false",
            ["requiresItEvaluation"] = "false"
        });

static G04DecisionRouteMetadata CommitteeRoute() => new("G04_COMMITTEE", "COMMITTEE", "MAJORITY", "GOV-1", "1.0");

static EvaluationPlanEnvelope Plan(G04DecisionRouteMetadata route) =>
    new("PLAN-1", "IDEA-1", 7, 1, "ACTIVE", DateTimeOffset.UtcNow.AddMinutes(-10), "CORR-PLAN", null,
        route.DecisionRoute, route.DecisionRouteKind, route.DecisionMethod, route.GovernanceProfileId, route.GovernanceProfileVersion);

static G04AssessmentEnvelope Assessment(G04DecisionRouteMetadata? route) =>
    new("G04-1", "PLAN-1", "IDEA-1", 7, 2, "PENDING", "READY-SHA", DateTimeOffset.UtcNow.AddMinutes(-2), "CORR-A",
        route?.DecisionRoute, route?.DecisionRouteKind, route?.DecisionMethod, route?.GovernanceProfileId, route?.GovernanceProfileVersion);

static AuthorityActor Actor(string role) =>
    new("P-M1", "DOMAIN\\m1", "WINDOWS_PRINCIPAL", role == "G04_COMMITTEE_MEMBER" ? "ASG-M1" : "ASG-AUTH",
        new[] { role }, new[] { "UNIT:RND" });

static G04GovernanceProfile Profile() => new("GOV-1", "1.0", 1, "MAJORITY", "P-M1", "IDEA_DECISION", "GOV-REF");

static IReadOnlyCollection<G04CommitteeMemberEnvelope> Members() => new[]
{
    new G04CommitteeMemberEnvelope("P-M1", "Member 1", new[] { "G04_COMMITTEE_MEMBER" }, new[] { "ASG-M1" })
};

static G04VoteCommand VoteCommand() =>
    new("PLAN-1", "G04-1", 7, 0, "K-VOTE", "CORR-VOTE", "APPROVE", "valid committee approval note", "UNIT:RND");

static string S(JsonElement e, string p) => e.GetProperty(p).GetString() ?? throw new Exception($"Missing string {p}");
static bool B(JsonElement e, string p) => e.GetProperty(p).GetBoolean();
static void True(bool v) { if (!v) throw new Exception("Expected true"); }
static void False(bool v) { if (v) throw new Exception("Expected false"); }
static void Eq<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected '{expected}', actual '{actual}'");
}

sealed class StaticProfileProvider(G04GovernanceProfile profile) : IG04GovernanceProfileProvider
{
    public ValueTask<G04GovernanceProfile?> ResolveAsync(G04AssessmentEnvelope assessment, AggregateSnapshot idea, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<G04GovernanceProfile?>(profile);
}

sealed class StaticMemberResolver(IReadOnlyCollection<G04CommitteeMemberEnvelope> members) : IG04CommitteeMembershipResolver
{
    public ValueTask<IReadOnlyCollection<G04CommitteeMemberEnvelope>> ResolveAsync(
        G04AssessmentEnvelope assessment, AggregateSnapshot idea, G04GovernanceProfile profile, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(members);
}

sealed class VotingStore(AggregateSnapshot idea, G04AssessmentEnvelope assessment) : IG04VotingStore
{
    public G04CommitteeSnapshotEnvelope? Snapshot { get; set; }
    public G04CommitteeStateEnvelope? State { get; set; }
    public List<G04EffectiveVoteEnvelope> Effective { get; } = new();

    public ValueTask<AggregateSnapshot?> GetAggregateAsync(string aggregateId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AggregateSnapshot?>(string.Equals(aggregateId, idea.AggregateId, StringComparison.Ordinal) ? idea : null);
    public ValueTask<G04AssessmentEnvelope?> GetG04AssessmentForPlanAsync(string planId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<G04AssessmentEnvelope?>(string.Equals(planId, assessment.PlanId, StringComparison.Ordinal) ? assessment : null);
    public ValueTask<IdempotencyRecord?> GetVoteIdempotencyAsync(string assessmentId, string idempotencyKey, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IdempotencyRecord?>(null);
    public ValueTask<G04CommitteeSnapshotEnvelope?> GetCommitteeSnapshotAsync(string assessmentId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Snapshot);
    public ValueTask<G04CommitteeStateEnvelope?> GetCommitteeStateAsync(string assessmentId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(State);
    public ValueTask<IReadOnlyCollection<G04EffectiveVoteEnvelope>> GetEffectiveVotesAsync(string assessmentId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyCollection<G04EffectiveVoteEnvelope>>(Effective.AsReadOnly());

    public ValueTask<AuthorityResult> CommitG04VoteAsync(G04VoteRequest request, G04VoteCommit commit, CancellationToken cancellationToken = default)
    {
        Snapshot = commit.SnapshotAfter;
        State = commit.StateAfter;
        Effective.RemoveAll(x => string.Equals(x.PersonId, commit.EffectiveVoteAfter.PersonId, StringComparison.OrdinalIgnoreCase));
        Effective.Add(commit.EffectiveVoteAfter);
        return ValueTask.FromResult(new AuthorityResult(
            200, "P1_G04_VOTE_COMMITTED", true, true, false, commit.StateAfter.CommitteeVersion,
            request.Command.CorrelationId, commit.OutboxEvents.Select(x => x.EventName).ToArray()));
    }
}

sealed class ValidAssessmentValidator : IEvaluatorAssessmentValidator
{
    public AssessmentValidationResult Validate(string role, string rawAssessmentJson) =>
        new(true, "OK", "SCHEMA-1", "1.0", "CONFIRMED", "{}", null);
}

sealed class WorkflowStore(AggregateSnapshot idea, EvaluationPlanEnvelope plan, EvaluationAssignmentEnvelope assignment) : IEvaluationWorkflowStore
{
    public EvaluationPlanEnvelope? CommittedPlan { get; private set; }
    public G04AssessmentEnvelope? CreatedAssessment { get; private set; }

    public ValueTask<AggregateSnapshot?> GetAggregateAsync(string aggregateId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AggregateSnapshot?>(string.Equals(aggregateId, idea.AggregateId, StringComparison.Ordinal) ? idea : null);
    public ValueTask<IdempotencyRecord?> GetIdempotencyAsync(string commandName, string aggregateId, string idempotencyKey, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IdempotencyRecord?>(null);
    public ValueTask<AuthorityResult> CommitAsync(MutationRequest request, MutationCommit commit, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public ValueTask<EvaluationPlanEnvelope?> GetEvaluationPlanAsync(string planId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<EvaluationPlanEnvelope?>(string.Equals(planId, plan.PlanId, StringComparison.Ordinal) ? plan : null);
    public ValueTask<EvaluationAssignmentEnvelope?> GetEvaluationAssignmentAsync(string evaluationAssignmentId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<EvaluationAssignmentEnvelope?>(string.Equals(evaluationAssignmentId, assignment.AssignmentId, StringComparison.Ordinal) ? assignment : null);
    public ValueTask<IReadOnlyCollection<EvaluationAssignmentEnvelope>> GetEvaluationAssignmentsForPlanAsync(string planId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyCollection<EvaluationAssignmentEnvelope>>(new[] { assignment });
    public ValueTask<G04AssessmentEnvelope?> GetG04AssessmentForPlanAsync(string planId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CreatedAssessment);
    public ValueTask<AuthorityResult> CommitEvaluationCompletionAsync(EvaluationCompletionRequest request, EvaluationCompletionCommit commit, CancellationToken cancellationToken = default)
    {
        CommittedPlan = commit.PlanAfter;
        CreatedAssessment = commit.G04Assessment;
        return ValueTask.FromResult(new AuthorityResult(200, "P1_EVALUATION_COMPLETED", true, true, false,
            commit.PlanAfter.PlanVersion, request.Command.CorrelationId, commit.OutboxEvents.Select(x => x.EventName).ToArray()));
    }
}
