using System.Text.Json;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: EIMS.ACR.P0.G04Voting.ContractTests <acr-json>");
    return 2;
}

using var doc = JsonDocument.Parse(File.ReadAllText(args[0]));
var root = doc.RootElement;
var rows = new List<(string Id, string Name, bool Pass)>();
void Add(string id, string name, bool pass) => rows.Add((id, name, pass));
string S(JsonElement e,string p)=>e.TryGetProperty(p,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??string.Empty:string.Empty;
bool B(JsonElement e,string p)=>e.TryGetProperty(p,out var v)&&v.ValueKind is JsonValueKind.True or JsonValueKind.False&&v.GetBoolean();
int I(JsonElement e,string p)=>e.GetProperty(p).GetInt32();
string[] A(JsonElement e,string p)=>e.GetProperty(p).EnumerateArray().Select(x=>x.GetString()??string.Empty).ToArray();

Add("G04V-CT-01","ACR identity and schema are exact",S(root,"acrId")=="ACR-P0-006"&&S(root,"schema")=="EIMS-ACR-G04-COMMITTEE-VOTING-1.0");
Add("G04V-CT-02","decision is explicitly post-freeze",S(root,"decisionClass")=="POST_FREEZE_ARCHITECTURE_DECISION");
Add("G04V-CT-03","frozen v6.360 hash remains exact and unmodified",S(root.GetProperty("frozenProduct"),"sha256")=="057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a"&&!B(root.GetProperty("frozenProduct"),"modifiedByThisDecision"));
Add("G04V-CT-04","historical original machine contract is not falsely claimed",S(root.GetProperty("provenance"),"historicalOriginalMachineContractStatus")=="PARTIAL_RECOVERY_ONLY"&&B(root.GetProperty("provenance"),"mustNotBeRepresentedAsRecoveredOriginal"));

var command=root.GetProperty("command");
Add("G04V-CT-05","command identity target and role are exact",S(command,"commandName")=="g04.vote"&&S(command,"targetType")=="G04Assessment"&&S(command,"requiredRole")=="G04_COMMITTEE_MEMBER");
Add("G04V-CT-06","vote only applies to PENDING committee route",S(command,"allowedAssessmentState")=="PENDING"&&S(command,"requiredDecisionRoute")=="G04_COMMITTEE");
Add("G04V-CT-07","vote options are exactly APPROVE and REJECT",A(command,"voteOptions").SequenceEqual(new[]{"APPROVE","REJECT"}));
Add("G04V-CT-08","final frozen vote note minimum is 10",B(command,"voteNoteRequired")&&I(command,"voteNoteMinLength")==10);
Add("G04V-CT-09","version and idempotency are mandatory",B(command,"expectedCommitteeVersionRequired")&&B(command,"idempotencyRequired"));

var snapshot=root.GetProperty("frozenSnapshot");
Add("G04V-CT-10","governance and membership snapshot is mandatory",B(snapshot,"required")&&B(snapshot,"immutableAfterFirstValidComposition"));
Add("G04V-CT-11","snapshot freezes quorum rule chair profile and approval reference",new[]{"governanceProfileId","governanceProfileVersion","quorumRequired","voteRule","chairPersonId","approvalAuthority","approvalRef"}.All(A(snapshot,"governanceFields").Contains));
Add("G04V-CT-12","membership snapshot keeps Person role and authority assignment context",new[]{"personId","displayName","sourceRoles","authorityAssignmentIds"}.All(A(snapshot,"membershipFields").Contains));
Add("G04V-CT-13","membership freeze source is explicit",S(snapshot,"membershipFreezeSource")=="RESOLVED_ROLE_ASSIGNMENTS_AT_FIRST_VALID_COMPOSITION");
Add("G04V-CT-14","quorum minimum is one",I(snapshot,"quorumMinimum")==1);
Add("G04V-CT-15","three frozen vote rules are supported",A(snapshot,"supportedVoteRules").Order().SequenceEqual(new[]{"CHAIR_TIEBREAK","CONSENSUS","MAJORITY"}.Order()));
Add("G04V-CT-16","chair is mandatory for chair tiebreak",B(snapshot,"chairRequiredWhenRuleIsChairTiebreak"));
Add("G04V-CT-17","client cannot alter governance snapshot",!B(snapshot,"clientMayAlterSnapshot"));

var profile=root.GetProperty("frozenBaselineProfile");
Add("G04V-CT-18","frozen default profile identity is exact",S(profile,"id")=="G04-GOV-V1.0"&&S(profile,"version")=="1.0"&&S(profile,"status")=="ACTIVE");
Add("G04V-CT-19","frozen baseline defaults retain AUTO BLOCK quorum2 majority",S(profile,"decisionMode")=="AUTO"&&S(profile,"evaluatorDecisionPolicy")=="BLOCK"&&I(profile,"quorumRequired")==2&&S(profile,"voteRule")=="MAJORITY");
Add("G04V-CT-20","baseline profile is recoverable default not universal hard-code",B(profile,"isRecoverableDefaultNotUniversalCustomerHardCode"));

var auth=root.GetProperty("authority");
Add("G04V-CT-21","Person and authority assignment are server-derived",B(auth,"personIdServerDerived")&&B(auth,"authorityAssignmentIdServerDerived"));
Add("G04V-CT-22","actor must be frozen committee member with committee role",B(auth,"actorMustBeFrozenMember")&&B(auth,"actorAuthorityRoleMustIncludeG04CommitteeMember"));
Add("G04V-CT-23","scope remains server-authoritative",B(auth,"actorScopeMustContainAssessmentScope"));
Add("G04V-CT-24","client cannot forge governance or member identity",new[]{"clientMaySelectAnotherMember","clientMaySetPersonId","clientMaySetAssignmentId","clientMaySetQuorum","clientMaySetVoteRule","clientMaySetChair","clientMaySetMembers","clientMaySetGovernanceProfile","clientMaySetEventIdentity"}.All(x=>!B(auth,x)));

var projection=root.GetProperty("effectiveVoteProjection");
Add("G04V-CT-25","only one effective vote per frozen Person counts",B(projection,"oneEffectiveVotePerFrozenPerson")&&B(projection,"onlyFrozenMembersCount"));
Add("G04V-CT-26","vote history is append-only",B(projection,"voteHistoryAppendOnly")&&!B(projection,"historyOverwriteAllowed"));
Add("G04V-CT-27","frozen replacement semantic is preserved as append-only revision",B(projection,"frozenUiReplacementSemanticsPreservedAsRevision")&&S(projection,"correctionBeforeStageComplete")=="NEW_APPEND_ONLY_REVISION_SUPERSEDES_PRIOR_EFFECTIVE_REVISION");
Add("G04V-CT-28","no vote correction after stage completion",!B(projection,"correctionAfterStageCompleteAllowed"));

var rule=root.GetProperty("approvalRule");
Add("G04V-CT-29","approval requires quorum first",B(rule,"requiresQuorumFirst"));
Add("G04V-CT-30","majority semantics are exact",S(rule,"MAJORITY")=="approveCount > rejectCount");
Add("G04V-CT-31","consensus semantics are exact",S(rule,"CONSENSUS")=="approveCount == effectiveVoteCount");
Add("G04V-CT-32","chair tiebreak semantics are exact",S(rule,"CHAIR_TIEBREAK").Contains("chair effective vote must be APPROVE",StringComparison.Ordinal));

var stage=root.GetProperty("stageCompletion");
Add("G04V-CT-33","stage completes on approval rule or all frozen members voted",B(stage,"stageCompleteWhenApprovalRuleSatisfied")&&B(stage,"stageCompleteWhenNoApprovalAndEveryFrozenMemberHasVoted"));
Add("G04V-CT-34","stage cannot complete before quorum",B(stage,"quorumMustBeReachedBeforeStageCanComplete"));
Add("G04V-CT-35","committee stage completion is not final Idea decision",B(stage,"committeeActionEndsWhenStageComplete")&&B(stage,"g04AssessmentRemainsPendingForFinalDecision")&&B(stage,"committeeVoteIsNotFinalIdeaDecision"));
Add("G04V-CT-36","committee vote cannot trigger Portfolio eligibility",B(stage,"committeeVoteDoesNotTriggerPortfolioEligibility"));

var events=root.GetProperty("events").EnumerateArray().ToArray();
Add("G04V-CT-37","exactly two post-freeze event identities are stabilized",events.Length==2&&events.All(x=>S(x,"decisionClass")=="POST_FREEZE_NEW_IDENTITY"));
Add("G04V-CT-38","vote-recorded event is emitted on every successful vote or revision",events.Any(x=>S(x,"eventType")=="G04CommitteeVoteRecorded.v1"&&S(x,"emission")=="EVERY_SUCCESSFUL_VOTE_OR_REVISION"));
Add("G04V-CT-39","voting-completed event is transition-only",events.Any(x=>S(x,"eventType")=="G04CommitteeVotingCompleted.v1"&&S(x,"emission")=="ONLY_ON_OPEN_TO_COMPLETE_STAGE_TRANSITION"));

var minimize=root.GetProperty("eventDataMinimization");
Add("G04V-CT-40","events exclude vote note and full snapshots",B(minimize,"voteNoteExcluded")&&B(minimize,"fullGovernanceSnapshotExcluded")&&B(minimize,"fullMembershipSnapshotExcluded")&&B(minimize,"assessmentAnswersExcluded"));

var atomic=root.GetProperty("atomicPersistence");
Add("G04V-CT-41","vote revision projection audit outbox idempotency are one boundary",new[]{"VoteRevision","EffectiveVoteProjection","CommitteeVersionAndStageSummary","Audit","Outbox","Idempotency"}.All(A(atomic,"requiredComponents").Contains));
Add("G04V-CT-42","atomicity concurrency rollback are mandatory",B(atomic,"singleLogicalTransactionRequired")&&B(atomic,"optimisticConcurrencyRequired")&&B(atomic,"rollbackAllOnFailure"));

var gate=root.GetProperty("runtimePromotionGate");
Add("G04V-CT-43","ACR alone cannot promote g04.vote",!B(gate,"commandPromotionAllowedByThisAcrAlone"));
Add("G04V-CT-44","runtime gate requires governance membership persistence authority and security evidence",new[]{"serverGovernanceProfileProvider","serverCommitteeMembershipResolver","frozenSnapshotPersistence","committeeVoteStore","voteRuleEvaluator","P3IdentityRoleScopeTests","atomicPersistenceTests","securityBaseline"}.All(A(gate,"stillRequired").Contains));

var down=root.GetProperty("downstreamSeparation");
Add("G04V-CT-45","final G04 decision stays separate and unpromoted",S(down,"finalDecisionCommand")=="g04.final-decision"&&S(down,"finalDecisionRole")=="IDEA_DECISION"&&!B(down,"finalDecisionPromotedByThisAcr"));
Add("G04V-CT-46","P5 and Portfolio remain outside committee vote",!B(down,"portfolioEligibilityTriggeredByVote")&&!B(down,"p5BoundByThisAcr"));

foreach(var r in rows) Console.WriteLine($"{(r.Pass?"PASS":"FAIL")} {r.Id} {r.Name}");
var pass=rows.Count(x=>x.Pass);
Console.WriteLine($"RESULT {pass}/{rows.Count} PASS");
return pass==rows.Count?0:1;
