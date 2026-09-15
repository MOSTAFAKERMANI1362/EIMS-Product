using System.Text.Json;
using EIMS.Authority.Recovery;
using EIMS.Identity.Rbac;
using EIMS.Persistence.Recovery;
using EIMS.PilotAssembly.Core;

if (args.Length != 2 || args.Any(x => !File.Exists(x)))
{
    Console.Error.WriteLine("Usage: EIMS.P1.Wave7.G04Voting.ContractTests <ACR-P0-006-json> <P1-wave7-json>");
    return 2;
}

using var acrDoc = JsonDocument.Parse(File.ReadAllText(args[0]));
using var waveDoc = JsonDocument.Parse(File.ReadAllText(args[1]));
var acr = acrDoc.RootElement;
var wave = waveDoc.RootElement;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("P1W7-CT-01 ACR-P0-006 identity and Wave7 source decision are exact", ArtifactIdentity),
    ("P1W7-CT-02 cumulative catalog preserves 21 identities and promotes exactly five mutations", CatalogPromotion),
    ("P1W7-CT-03 final G04 decision remains fail closed", FinalDecisionStillClosed),
    ("P1W7-CT-04 P5 gateway remains fail closed", P5StillClosed),
    ("P1W7-CT-05 P3 exact committee authority assignment resolves", P3ResolvesCommitteeActor),
    ("P1W7-CT-06 first valid vote freezes server governance and membership snapshot", FirstVoteFreezesSnapshot),
    ("P1W7-CT-07 first vote creates version one OPEN committee state", FirstVoteState),
    ("P1W7-CT-08 governance provider drift cannot alter frozen snapshot", FrozenSnapshotIgnoresProviderDrift),
    ("P1W7-CT-09 wrong authority role is denied", WrongRoleDenied),
    ("P1W7-CT-10 wrong authority scope is denied", WrongScopeDenied),
    ("P1W7-CT-11 nonmember Person is denied", NonMemberDenied),
    ("P1W7-CT-12 wrong frozen authority assignment is denied", WrongAssignmentDenied),
    ("P1W7-CT-13 invalid vote value is denied", InvalidVoteDenied),
    ("P1W7-CT-14 short vote note is denied", ShortNoteDenied),
    ("P1W7-CT-15 stale committee version is denied", StaleVersionDenied),
    ("P1W7-CT-16 majority rule approves after quorum", MajorityApproves),
    ("P1W7-CT-17 same member correction appends revision without increasing effective vote count", CorrectionIsAppendOnly),
    ("P1W7-CT-18 correction supersedes prior effective revision", CorrectionSupersedesPrior),
    ("P1W7-CT-19 exact replay is idempotent and duplicates no evidence", ExactReplay),
    ("P1W7-CT-20 changed replay conflicts", ChangedReplayConflict),
    ("P1W7-CT-21 completed committee stage rejects later vote", CompletedStageLocked),
    ("P1W7-CT-22 open vote emits only vote-recorded event", OpenEventSet),
    ("P1W7-CT-23 completing vote emits vote-recorded plus voting-completed", CompletedEventSet),
    ("P1W7-CT-24 integration event excludes vote note and snapshot bodies", EventMinimization),
    ("P1W7-CT-25 audit retains Person and exact authority assignment", AuditAuthorityContext),
    ("P1W7-CT-26 consensus approves when all effective votes after quorum are APPROVE", ConsensusApproves),
    ("P1W7-CT-27 consensus nonapproval stays open until all frozen members vote", ConsensusWaitsForAllOnFailure),
    ("P1W7-CT-28 chair tiebreak approves tie when chair effective vote is APPROVE", ChairTiebreakApprove),
    ("P1W7-CT-29 chair tiebreak completes nonapproval when chair rejects and all voted", ChairTiebreakReject),
    ("P1W7-CT-30 invalid governance profile fails closed", InvalidGovernanceFailsClosed),
    ("P1W7-CT-31 invalid committee membership fails closed", InvalidMembershipFailsClosed),
    ("P1W7-CT-32 snapshot route is server-fixed to G04_COMMITTEE", RouteFixed),
    ("P1W7-CT-33 voting persistence contract is logically ready but not Oracle-bound", PersistenceContractBoundary),
    ("P1W7-CT-34 rollback after snapshot staging removes all evidence", () => RollbackAt(G04VotingPersistenceFaultPoint.AfterSnapshotStaged)),
    ("P1W7-CT-35 rollback after vote revision staging removes all evidence", () => RollbackAt(G04VotingPersistenceFaultPoint.AfterVoteRevisionStaged)),
    ("P1W7-CT-36 rollback after effective projection staging removes all evidence", () => RollbackAt(G04VotingPersistenceFaultPoint.AfterEffectiveProjectionStaged)),
    ("P1W7-CT-37 rollback after committee state staging removes all evidence", () => RollbackAt(G04VotingPersistenceFaultPoint.AfterCommitteeStateStaged)),
    ("P1W7-CT-38 rollback after outbox staging removes all evidence", () => RollbackAt(G04VotingPersistenceFaultPoint.AfterOutboxStaged)),
    ("P1W7-CT-39 rollback after idempotency staging removes all evidence", () => RollbackAt(G04VotingPersistenceFaultPoint.AfterIdempotencyStaged)),
    ("P1W7-CT-40 vote note remains authoritative evidence while absent from events", VoteNoteEvidenceOnly),
    ("P1W7-CT-41 G04Assessment remains PENDING after committee stage completion", AssessmentRemainsPending),
    ("P1W7-CT-42 committee vote emits no Portfolio event", NoPortfolioEvent),
    ("P1W7-CT-43 Wave7 preserves frozen v6.360 hash and no-live-readiness claims", WaveSafety)
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
    Eq("ACR-P0-006", S(acr, "acrId"));
    Eq("APPROVED_FOR_P1_IMPLEMENTATION", S(acr, "status"));
    Eq("POST_FREEZE_ARCHITECTURE_DECISION", S(acr, "decisionClass"));
    Eq("EIMS-P1-RECOVERY-WAVE7-G04-VOTING-REBASELINE-1.0", S(wave, "schema"));
    Eq("ACR-P0-006", S(wave.GetProperty("sourceDecision"), "acrId"));
    True(B(wave.GetProperty("sourceDecision"), "postFreezeDecisionUsed"));
    return Task.CompletedTask;
}

Task CatalogPromotion()
{
    var catalog = new RecoveredApiCommandCatalogWave7();
    Eq(21, catalog.All.Count);
    Eq(5, catalog.All.Count(x => x.MutationContractRecovered));
    Eq(5, RecoveredApiCommandCatalogWave7.Wave7RecoveredMutationCommandCount);
    var bound = catalog.All.Where(x => x.MutationContractRecovered).Select(x => x.CommandName).OrderBy(x => x).ToArray();
    Seq(new[] { "evaluation-assignments.complete", "g04.vote", "ideas.submit-g04", "needs.g03-decision", "needs.submit-g03" }, bound);
    return Task.CompletedTask;
}

Task FinalDecisionStillClosed()
{
    var catalog = new RecoveredApiCommandCatalogWave7();
    True(catalog.TryGet("g04.final-decision", out var policy));
    False(policy.MutationContractRecovered);
    return Task.CompletedTask;
}

async Task P5StillClosed()
{
    ICommandGateway gateway = new FailClosedCommandGateway();
    var result = await gateway.ExecuteAsync(new CommandAttempt("g04.vote", "DOMAIN\\m1", "CORR-P5", 0, "P5-G04", "{}"));
    Eq(503, result.HttpStatus); Eq("P5_COMMAND_GATEWAY_NOT_BOUND", result.Code); False(result.StateMutated);
}

async Task P3ResolvesCommitteeActor()
{
    var now = DateTimeOffset.UtcNow;
    var directory = new InMemoryIdentityDirectoryStore(
        new[] { new PersonDirectoryEntry("P-M1", "DOMAIN\\m1", DirectoryPersonStatus.Active) },
        new[] { new RoleAssignmentEntry("ASG-M1", "P-M1", "G04_COMMITTEE_MEMBER", new[] { "UNIT:RND" }, now.AddDays(-1), null, false) });
    var resolver = new WindowsIdentityRbacResolver(directory);
    var result = await resolver.ResolveAsync(new IdentityResolutionRequest("DOMAIN\\m1", "ASG-M1", "UNIT:RND", now));
    True(result.Allowed); Eq("P-M1", result.Actor!.PersonId); Eq("ASG-M1", result.Actor.AssignmentId);
    Seq(new[] { "G04_COMMITTEE_MEMBER" }, result.Actor.Roles);
}

async Task FirstVoteFreezesSnapshot()
{
    var r = Runtime();
    var result = await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1"));
    Eq(200, result.HttpStatus);
    var snapshot = Single(r.Store.CommitteeSnapshots);
    Eq("GOV-1", snapshot.GovernanceProfileId); Eq("1.0", snapshot.GovernanceProfileVersion);
    Eq(2, snapshot.QuorumRequired); Eq("MAJORITY", snapshot.VoteRule); Eq(3, snapshot.Members.Count);
    Eq(1, r.Profile.ResolveCount); Eq(1, r.Members.ResolveCount);
}

async Task FirstVoteState()
{
    var r = Runtime();
    await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1"));
    var state = Single(r.Store.CommitteeStates);
    Eq(1, state.CommitteeVersion); Eq("OPEN", state.VotingStageState); False(state.QuorumReached); False(state.ApprovalRuleSatisfied);
    Eq(1, state.EffectiveVoteCount); Eq(1, state.ApproveCount); Eq(0, state.RejectCount);
}

async Task FrozenSnapshotIgnoresProviderDrift()
{
    var r = Runtime();
    await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1"));
    r.Profile.Profile = Profile("CONSENSUS", 1) with { GovernanceProfileId = "EVIL-DRIFT", GovernanceProfileVersion = "99" };
    await r.Service.VoteAsync(Cmd(1, "K2", "REJECT", "corrected vote reason"), Actor("P-M1", "ASG-M1"));
    var snapshot = Single(r.Store.CommitteeSnapshots);
    Eq("GOV-1", snapshot.GovernanceProfileId); Eq("MAJORITY", snapshot.VoteRule); Eq(2, snapshot.QuorumRequired);
    Eq(1, r.Profile.ResolveCount); Eq(1, r.Members.ResolveCount);
}

async Task WrongRoleDenied()
{
    var r = Runtime();
    var result = await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1", "IDEA_EVALUATOR"));
    Eq(403, result.HttpStatus); Eq("P1_G04_COMMITTEE_ROLE_REQUIRED", result.Code); Pristine(r.Store);
}

async Task WrongScopeDenied()
{
    var r = Runtime();
    var actor = new AuthorityActor("P-M1", "DOMAIN\\m1", "WINDOWS_PRINCIPAL", "ASG-M1", new[] { "G04_COMMITTEE_MEMBER" }, new[] { "UNIT:FIN" });
    var result = await r.Service.VoteAsync(Cmd(0, "K1"), actor);
    Eq(403, result.HttpStatus); Eq("P1_G04_COMMITTEE_SCOPE_DENIED", result.Code); Pristine(r.Store);
}

async Task NonMemberDenied()
{
    var r = Runtime();
    var result = await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-X", "ASG-X"));
    Eq(403, result.HttpStatus); Eq("P1_G04_ACTOR_NOT_FROZEN_MEMBER", result.Code); Pristine(r.Store);
}

async Task WrongAssignmentDenied()
{
    var r = Runtime();
    var result = await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-WRONG"));
    Eq(403, result.HttpStatus); Eq("P1_G04_FROZEN_MEMBER_AUTHORITY_MISMATCH", result.Code); Pristine(r.Store);
}

async Task InvalidVoteDenied()
{
    var r = Runtime();
    var result = await r.Service.VoteAsync(Cmd(0, "K1", "ABSTAIN"), Actor("P-M1", "ASG-M1"));
    Eq(422, result.HttpStatus); Eq("P1_G04_VOTE_VALUE_INVALID", result.Code); Pristine(r.Store);
}

async Task ShortNoteDenied()
{
    var r = Runtime();
    var result = await r.Service.VoteAsync(Cmd(0, "K1", "APPROVE", "short"), Actor("P-M1", "ASG-M1"));
    Eq(422, result.HttpStatus); Eq("P1_G04_VOTE_NOTE_REQUIRED", result.Code); Pristine(r.Store);
}

async Task StaleVersionDenied()
{
    var r = Runtime();
    await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1"));
    var result = await r.Service.VoteAsync(Cmd(0, "K2"), Actor("P-M2", "ASG-M2"));
    Eq(409, result.HttpStatus); Eq("P1_G04_COMMITTEE_VERSION_CONFLICT", result.Code);
    Eq(1, r.Store.VoteRevisions.Count);
}

async Task MajorityApproves()
{
    var r = Runtime();
    await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1"));
    var result = await r.Service.VoteAsync(Cmd(1, "K2"), Actor("P-M2", "ASG-M2"));
    Eq(200, result.HttpStatus);
    var state = Single(r.Store.CommitteeStates);
    True(state.QuorumReached); True(state.ApprovalRuleSatisfied); Eq("COMPLETED", state.VotingStageState);
    Eq(2, state.CommitteeVersion); Eq(2, state.ApproveCount);
}

async Task CorrectionIsAppendOnly()
{
    var r = Runtime();
    await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1"));
    await r.Service.VoteAsync(Cmd(1, "K2", "REJECT", "corrected rejection reason"), Actor("P-M1", "ASG-M1"));
    Eq(2, r.Store.VoteRevisions.Count); Eq(1, r.Store.EffectiveVotes.Count);
    var effective = Single(r.Store.EffectiveVotes); Eq("REJECT", effective.Vote); Eq(2, effective.RevisionNumber);
    var state = Single(r.Store.CommitteeStates); Eq(1, state.EffectiveVoteCount); Eq(0, state.ApproveCount); Eq(1, state.RejectCount); Eq("OPEN", state.VotingStageState);
}

async Task CorrectionSupersedesPrior()
{
    var r = Runtime();
    await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1"));
    var first = Single(r.Store.VoteRevisions);
    await r.Service.VoteAsync(Cmd(1, "K2", "REJECT", "corrected rejection reason"), Actor("P-M1", "ASG-M1"));
    var second = r.Store.VoteRevisions.OrderBy(x => x.RevisionNumber).Last();
    Eq(first.VoteRevisionId, second.SupersedesVoteRevisionId!); Eq(2, second.RevisionNumber);
}

async Task ExactReplay()
{
    var r = Runtime();
    var command = Cmd(0, "K1");
    var first = await r.Service.VoteAsync(command, Actor("P-M1", "ASG-M1"));
    var replay = await r.Service.VoteAsync(command, Actor("P-M1", "ASG-M1"));
    True(first.StateMutated); True(replay.IdempotentReplay); False(replay.StateMutated);
    Eq(1, r.Store.CommitteeSnapshots.Count); Eq(1, r.Store.VoteRevisions.Count); Eq(1, r.Store.EffectiveVotes.Count);
    Eq(1, r.Store.VotingAuditLog.Count); Eq(1, r.Store.VotingOutbox.Count); Eq(1, r.Store.VotingIdempotencyRecords.Count);
}

async Task ChangedReplayConflict()
{
    var r = Runtime();
    await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1"));
    var changed = await r.Service.VoteAsync(Cmd(0, "K1", "REJECT", "different replay reason"), Actor("P-M1", "ASG-M1"));
    Eq(409, changed.HttpStatus); Eq("P1_IDEMPOTENCY_CONFLICT", changed.Code); Eq(1, r.Store.VoteRevisions.Count);
}

async Task CompletedStageLocked()
{
    var r = Runtime();
    await CompleteMajority(r);
    var result = await r.Service.VoteAsync(Cmd(2, "K3"), Actor("P-M3", "ASG-M3"));
    Eq(409, result.HttpStatus); Eq("P1_G04_VOTING_STAGE_COMPLETED", result.Code); Eq(2, r.Store.VoteRevisions.Count);
}

async Task OpenEventSet()
{
    var r = Runtime();
    var result = await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1"));
    Seq(new[] { "G04CommitteeVoteRecorded.v1" }, result.EmittedEvents);
    Eq(1, r.Store.VotingOutbox.Count);
}

async Task CompletedEventSet()
{
    var r = Runtime();
    await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1"));
    var result = await r.Service.VoteAsync(Cmd(1, "K2"), Actor("P-M2", "ASG-M2"));
    Eq(2, result.EmittedEvents.Count);
    True(result.EmittedEvents.Contains("G04CommitteeVoteRecorded.v1"));
    True(result.EmittedEvents.Contains("G04CommitteeVotingCompleted.v1"));
    Eq(3, r.Store.VotingOutbox.Count);
}

async Task EventMinimization()
{
    var r = Runtime();
    const string secretNote = "this vote note stays only as evidence";
    await r.Service.VoteAsync(Cmd(0, "K1", "APPROVE", secretNote), Actor("P-M1", "ASG-M1"));
    var ev = Single(r.Store.VotingOutbox);
    True(ev.Payload is not null);
    False(ev.Payload!.Keys.Any(k => k.Contains("note", StringComparison.OrdinalIgnoreCase) || k.Contains("snapshot", StringComparison.OrdinalIgnoreCase) || k.Contains("answer", StringComparison.OrdinalIgnoreCase)));
    False(ev.Payload.Values.Any(v => string.Equals(v, secretNote, StringComparison.Ordinal)));
}

async Task AuditAuthorityContext()
{
    var r = Runtime();
    await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1"));
    var audit = Single(r.Store.VotingAuditLog);
    Eq("P-M1", audit.PersonId); Eq("ASG-M1", audit.Assignment); Eq("DOMAIN\\m1", audit.NetworkIdentity);
    Eq("g04.vote", audit.CommandName); Eq("P1-G04-VOTE-ACR-P0-006-1.0", audit.RuleSet);
}

async Task ConsensusApproves()
{
    var r = Runtime(Profile("CONSENSUS", 2));
    await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1"));
    await r.Service.VoteAsync(Cmd(1, "K2"), Actor("P-M2", "ASG-M2"));
    var state = Single(r.Store.CommitteeStates);
    True(state.QuorumReached); True(state.ApprovalRuleSatisfied); Eq("COMPLETED", state.VotingStageState);
}

async Task ConsensusWaitsForAllOnFailure()
{
    var r = Runtime(Profile("CONSENSUS", 2));
    await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1"));
    await r.Service.VoteAsync(Cmd(1, "K2", "REJECT", "member two rejection reason"), Actor("P-M2", "ASG-M2"));
    var mid = Single(r.Store.CommitteeStates); True(mid.QuorumReached); False(mid.ApprovalRuleSatisfied); Eq("OPEN", mid.VotingStageState);
    await r.Service.VoteAsync(Cmd(2, "K3", "REJECT", "member three rejection reason"), Actor("P-M3", "ASG-M3"));
    var end = Single(r.Store.CommitteeStates); False(end.ApprovalRuleSatisfied); Eq("COMPLETED", end.VotingStageState); Eq(3, end.EffectiveVoteCount);
}

async Task ChairTiebreakApprove()
{
    var members = DefaultMembers().Take(2).ToArray();
    var r = Runtime(Profile("CHAIR_TIEBREAK", 2, "P-M1"), members);
    await r.Service.VoteAsync(Cmd(0, "K1", "APPROVE", "chair approval reason"), Actor("P-M1", "ASG-M1"));
    await r.Service.VoteAsync(Cmd(1, "K2", "REJECT", "other member rejection"), Actor("P-M2", "ASG-M2"));
    var state = Single(r.Store.CommitteeStates); True(state.ApprovalRuleSatisfied); Eq("COMPLETED", state.VotingStageState);
}

async Task ChairTiebreakReject()
{
    var members = DefaultMembers().Take(2).ToArray();
    var r = Runtime(Profile("CHAIR_TIEBREAK", 2, "P-M1"), members);
    await r.Service.VoteAsync(Cmd(0, "K1", "REJECT", "chair rejection reason"), Actor("P-M1", "ASG-M1"));
    await r.Service.VoteAsync(Cmd(1, "K2", "APPROVE", "other member approval"), Actor("P-M2", "ASG-M2"));
    var state = Single(r.Store.CommitteeStates); False(state.ApprovalRuleSatisfied); Eq("COMPLETED", state.VotingStageState);
}

async Task InvalidGovernanceFailsClosed()
{
    var r = Runtime(Profile("CHAIR_TIEBREAK", 2, ""));
    var result = await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1"));
    Eq(503, result.HttpStatus); Eq("P1_G04_GOVERNANCE_PROFILE_NOT_BOUND", result.Code); Pristine(r.Store);
}

async Task InvalidMembershipFailsClosed()
{
    var r = Runtime(Profile("MAJORITY", 3), DefaultMembers().Take(2).ToArray());
    var result = await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1"));
    Eq(503, result.HttpStatus); Eq("P1_G04_COMMITTEE_MEMBERSHIP_NOT_BOUND", result.Code); Pristine(r.Store);
}

async Task RouteFixed()
{
    var r = Runtime();
    await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1"));
    Eq("G04_COMMITTEE", Single(r.Store.CommitteeSnapshots).DecisionRoute);
}

Task PersistenceContractBoundary()
{
    var contract = G04VotingPersistenceContractDescriptor.RecoveryBaseline();
    True(contract.IsLogicalContractReady); False(contract.PhysicalOracleBound);
    return Task.CompletedTask;
}

async Task RollbackAt(G04VotingPersistenceFaultPoint point)
{
    var r = Runtime();
    r.Store.FaultPoint = point;
    var threw = false;
    try { await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1")); }
    catch (PersistenceAtomicityException) { threw = true; }
    True(threw); Pristine(r.Store);
}

async Task VoteNoteEvidenceOnly()
{
    var r = Runtime();
    const string note = "authoritative evidence note";
    await r.Service.VoteAsync(Cmd(0, "K1", "APPROVE", note), Actor("P-M1", "ASG-M1"));
    Eq(note, Single(r.Store.VoteRevisions).Note);
    False(r.Store.VotingOutbox.Any(x => x.Payload is not null && x.Payload.Values.Contains(note)));
}

async Task AssessmentRemainsPending()
{
    var r = Runtime();
    await CompleteMajority(r);
    var assessment = await r.Context.GetG04AssessmentForPlanAsync("PLAN-1");
    Eq("PENDING", assessment!.State);
}

async Task NoPortfolioEvent()
{
    var r = Runtime();
    await CompleteMajority(r);
    False(r.Store.VotingOutbox.Any(x => x.EventName.Contains("Portfolio", StringComparison.OrdinalIgnoreCase)));
}

Task WaveSafety()
{
    Eq("057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a", S(wave.GetProperty("frozenProduct"), "sha256"));
    var safety = wave.GetProperty("runtimeSafety");
    False(B(safety, "g04FinalDecisionPromoted")); False(B(safety, "portfolioEligibilityTriggered")); False(B(safety, "p5CommandGatewayBound"));
    False(B(safety, "physicalOracleReadyClaimed")); False(B(safety, "liveWindowsDomainReadyClaimed")); False(B(safety, "networkPilotReadyClaimed")); True(B(safety, "v6360Unchanged"));
    return Task.CompletedTask;
}

static async Task CompleteMajority(TestRuntime r)
{
    await r.Service.VoteAsync(Cmd(0, "K1"), Actor("P-M1", "ASG-M1"));
    await r.Service.VoteAsync(Cmd(1, "K2"), Actor("P-M2", "ASG-M2"));
}

static G04VoteCommand Cmd(int committeeVersion, string key, string vote = "APPROVE", string note = "valid committee vote reason") =>
    new("PLAN-1", "G04-1", 7, committeeVersion, key, $"CORR-{key}", vote, note, "UNIT:RND");

static AuthorityActor Actor(string personId, string assignmentId, string role = "G04_COMMITTEE_MEMBER") =>
    new(personId, $"DOMAIN\\{personId.ToLowerInvariant()}", "WINDOWS_PRINCIPAL", assignmentId, new[] { role }, new[] { "UNIT:RND" });

static G04GovernanceProfile Profile(string rule = "MAJORITY", int quorum = 2, string chair = "") =>
    new("GOV-1", "1.0", quorum, rule, chair, "ORG_APPROVAL", "SYSTEM_BASELINE");

static G04CommitteeMemberEnvelope[] DefaultMembers() =>
[
    new("P-M1", "Member 1", new[] { "G04_COMMITTEE_MEMBER" }, new[] { "ASG-M1" }),
    new("P-M2", "Member 2", new[] { "G04_COMMITTEE_MEMBER" }, new[] { "ASG-M2" }),
    new("P-M3", "Member 3", new[] { "G04_COMMITTEE_MEMBER" }, new[] { "ASG-M3" })
];

static TestRuntime Runtime(G04GovernanceProfile? profile = null, IReadOnlyCollection<G04CommitteeMemberEnvelope>? members = null)
{
    var idea = new AggregateSnapshot("IDEA-1", "Idea", "UNDER_REVIEW", 7, "P-OWNER", "IDEA_OWNER", "UNIT:RND");
    var assessment = new G04AssessmentEnvelope("G04-1", "PLAN-1", "IDEA-1", 7, 3, "PENDING", "readiness-sha", DateTimeOffset.UtcNow.AddMinutes(-1), "CORR-CREATE");
    var context = new SeededEvaluationWorkflowStore(idea, assessment);
    var store = new G04VotingTransactionalStore(context);
    var p = new StaticGovernanceProvider(profile ?? Profile());
    var m = new StaticMembershipResolver(members ?? DefaultMembers());
    var service = new G04VotingService(store, p, m, new RecoveredApiCommandCatalogWave7());
    return new TestRuntime(service, store, p, m, context);
}

static void Pristine(G04VotingTransactionalStore store)
{
    Eq(0, store.CommitteeSnapshots.Count); Eq(0, store.CommitteeStates.Count); Eq(0, store.VoteRevisions.Count); Eq(0, store.EffectiveVotes.Count);
    Eq(0, store.VotingAuditLog.Count); Eq(0, store.VotingOutbox.Count); Eq(0, store.VotingIdempotencyRecords.Count);
}

static string S(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
static bool B(JsonElement e, string p) => e.GetProperty(p).GetBoolean();
static T Single<T>(IReadOnlyCollection<T> items) => items.Count == 1 ? items.Single() : throw new InvalidOperationException($"Expected one item, actual {items.Count}.");
static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
static void Eq<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
}
static void Seq<T>(IEnumerable<T> expected, IEnumerable<T> actual)
{
    if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("Sequences differ.");
}

sealed record TestRuntime(
    G04VotingService Service,
    G04VotingTransactionalStore Store,
    StaticGovernanceProvider Profile,
    StaticMembershipResolver Members,
    SeededEvaluationWorkflowStore Context);

sealed class StaticGovernanceProvider(G04GovernanceProfile profile) : IG04GovernanceProfileProvider
{
    public G04GovernanceProfile Profile { get; set; } = profile;
    public int ResolveCount { get; private set; }
    public ValueTask<G04GovernanceProfile?> ResolveAsync(G04AssessmentEnvelope assessment, AggregateSnapshot idea, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ResolveCount++; return ValueTask.FromResult<G04GovernanceProfile?>(Profile);
    }
}

sealed class StaticMembershipResolver(IReadOnlyCollection<G04CommitteeMemberEnvelope> members) : IG04CommitteeMembershipResolver
{
    public IReadOnlyCollection<G04CommitteeMemberEnvelope> Members { get; set; } = members;
    public int ResolveCount { get; private set; }
    public ValueTask<IReadOnlyCollection<G04CommitteeMemberEnvelope>> ResolveAsync(G04AssessmentEnvelope assessment, AggregateSnapshot idea, G04GovernanceProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ResolveCount++; return ValueTask.FromResult(Members);
    }
}

sealed class SeededEvaluationWorkflowStore(AggregateSnapshot idea, G04AssessmentEnvelope assessment) : IEvaluationWorkflowStore
{
    private readonly AggregateSnapshot _idea = idea;
    private readonly G04AssessmentEnvelope _assessment = assessment;

    public ValueTask<AggregateSnapshot?> GetAggregateAsync(string aggregateId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<AggregateSnapshot?>(string.Equals(aggregateId, _idea.AggregateId, StringComparison.Ordinal) ? _idea : null);
    }

    public ValueTask<IdempotencyRecord?> GetIdempotencyAsync(string commandName, string aggregateId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IdempotencyRecord?>(null);

    public ValueTask<AuthorityResult> CommitAsync(MutationRequest request, MutationCommit commit, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(AuthorityResult.Deny(503, "TEST_CONTEXT_READ_ONLY", request.Command.CorrelationId));

    public ValueTask<EvaluationPlanEnvelope?> GetEvaluationPlanAsync(string planId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<EvaluationPlanEnvelope?>(null);

    public ValueTask<EvaluationAssignmentEnvelope?> GetEvaluationAssignmentAsync(string evaluationAssignmentId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<EvaluationAssignmentEnvelope?>(null);

    public ValueTask<IReadOnlyCollection<EvaluationAssignmentEnvelope>> GetEvaluationAssignmentsForPlanAsync(string planId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyCollection<EvaluationAssignmentEnvelope>>(Array.Empty<EvaluationAssignmentEnvelope>());

    public ValueTask<G04AssessmentEnvelope?> GetG04AssessmentForPlanAsync(string planId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<G04AssessmentEnvelope?>(string.Equals(planId, _assessment.PlanId, StringComparison.Ordinal) ? _assessment : null);
    }

    public ValueTask<AuthorityResult> CommitEvaluationCompletionAsync(EvaluationCompletionRequest request, EvaluationCompletionCommit commit, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(AuthorityResult.Deny(503, "TEST_CONTEXT_READ_ONLY", request.Command.CorrelationId));
}
